using System.Net;
using System.Text;
using Compressarr.Core.Auth;
using Compressarr.Core.Config;
using Compressarr.Core.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Compressarr.Web.Auth;

/// <summary>The sign-in settings and the allowed hosts, kept in memory for a moment so that the many small requests of one page (scripts, styles, polls) do not each read the
/// database and the settings file. Anything that changes them calls <see cref="Invalidate"/>; a change made some other way (a restored backup) is picked up within seconds.</summary>
public sealed class WebAuthGate
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(2);

    private readonly IWebAuth _auth;
    private readonly IConfigStore _configStore;
    private readonly object _lock = new();
    private DateTimeOffset _loaded = DateTimeOffset.MinValue;
    private AuthSettings _settings = AuthSettings.Default;
    private string[] _allowedHosts = Array.Empty<string>();

    public WebAuthGate(IWebAuth auth, IConfigStore configStore)
    {
        _auth = auth;
        _configStore = configStore;
    }

    public void Invalidate()
    {
        lock (_lock) _loaded = DateTimeOffset.MinValue;
    }

    public (AuthSettings Settings, string[] AllowedHosts) Current()
    {
        lock (_lock)
        {
            if (DateTimeOffset.UtcNow - _loaded < Lifetime) return (_settings, _allowedHosts);

            _settings = _auth.GetSettings();
            try
            {
                _allowedHosts = ParseHosts(_configStore.Load(AppPaths.GetConfigFilePath()).Web.AllowedHosts);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                // A momentarily unreadable settings file must not shut the web interface: keep the last list.
            }

            _loaded = DateTimeOffset.UtcNow;
            return (_settings, _allowedHosts);
        }
    }

    /// <summary>The names and addresses in a comma-separated list, lower case, without ports, empty entries dropped.</summary>
    public static string[] ParseHosts(string? text) =>
        (text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => HostOf(h).ToLowerInvariant()).Where(h => h.Length > 0).Distinct().ToArray();

    /// <summary>The host part of a Host header or an allowed-hosts entry: no port, brackets of an IPv6 address kept off.</summary>
    public static string HostOf(string value)
    {
        value = value.Trim();
        if (value.StartsWith('['))
        {
            var close = value.IndexOf(']');
            return close > 0 ? value[1..close] : value.Trim('[', ']');
        }

        var colon = value.LastIndexOf(':');
        return colon > 0 && value.IndexOf(':') == colon ? value[..colon] : value;
    }
}

/// <summary>What stands in front of the whole web interface, in this order: the allowed-hosts check (against DNS rebinding), the cross-site check on every state-changing
/// request, and the sign-in wall decided by <see cref="WebAuthPolicy"/> - exactly as Sonarr and Radarr do it, with the decision made from the TCP peer address only.
/// <c>/api/v1</c> has its own door (API keys) and is only checked for host. See docs/design-login.md.</summary>
public sealed class WebAuthMiddleware
{
    public const string SessionCookie = "compressarr.session";
    public const string UserItem = "Compressarr.WebUser";

    private static readonly string[] AnonymousFiles = { "/login.html", "/login.js", "/styles.css", "/manifest.json", "/sw.js" };
    private static readonly string[] AnonymousApi = { "/api/auth/status", "/api/auth/login", "/api/auth/logout" };

    private readonly RequestDelegate _next;
    private readonly WebAuthGate _gate;
    private readonly IWebAuth _auth;
    private readonly IRunLogger _logger;

    public WebAuthMiddleware(RequestDelegate next, WebAuthGate gate, IWebAuth auth, IRunLogger logger)
    {
        _next = next;
        _gate = gate;
        _auth = auth;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var (settings, allowedHosts) = _gate.Current();
        var path = context.Request.Path.Value ?? "/";
        var isV1 = path.Equals("/api/v1", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase);

        if (!HostAllowed(context, allowedHosts, isV1))
        {
            await Plain(context, StatusCodes.Status400BadRequest, "Invalid host. Open Compressarr by its IP address or computer name, or add the name you are using to Settings > Host > Allowed Hosts.");
            return;
        }

        if (isV1)
        {
            await _next(context);
            return;
        }

        if (!CrossSiteAllowed(context))
        {
            await Plain(context, StatusCodes.Status403Forbidden, "This request came from another web site, so Compressarr refused it.");
            return;
        }

        if (IsAnonymousPath(path))
        {
            await _next(context);
            return;
        }

        var peer = context.Connection.RemoteIpAddress;
        var forwarded = WebAuthPolicy.HasForwardingHeader(context.Request.Headers.Keys);
        if (WebAuthPolicy.IsFree(settings, peer, forwarded))
        {
            await _next(context);
            return;
        }

        // Credentials are needed from here on.
        var address = peer?.ToString() ?? "unknown";
        if (!settings.CredentialsSet)
        {
            await NotSetUp(context);
            return;
        }

        if (settings.Method == AuthMethod.Forms)
        {
            if (context.Request.Cookies.TryGetValue(SessionCookie, out var token) && _auth.ValidateSession(token) is { } session)
            {
                context.Items[UserItem] = session.Username;
                await _next(context);
                return;
            }

            await LoginRequired(context);
            return;
        }

        // Basic
        var result = _auth.AuthenticateBasic(context.Request.Headers.Authorization.ToString(), address);
        switch (result.Outcome)
        {
            case LoginOutcome.Success:
                context.Items[UserItem] = settings.Username;
                await _next(context);
                return;
            case LoginOutcome.LockedOut:
                await Locked(context, result.RetryAfter);
                return;
            default:
                if (result.Outcome == LoginOutcome.WrongCredentials && result.RetryAfter is not null) _logger.Log($"Sign-in: five wrong passwords from {address}; ignoring that address for five minutes.", LogSeverity.Info);
                context.Response.Headers.WWWAuthenticate = "Basic realm=\"Compressarr\", charset=\"UTF-8\"";
                await LoginRequired(context, withRedirect: false);
                return;
        }
    }

    // ---- the checks ----------------------------------------------------------------------------------------------------------------------

    /// <summary>With a list: the Host header's host must be listed; <c>localhost</c> and the loopback addresses always pass, so a typo can be fixed from the PC.
    /// Without one, the names a person uses on their own network still pass (an IP address, <c>localhost</c>, a computer name with no dots, a <c>.local</c>, <c>.lan</c>,
    /// <c>.home</c> or <c>.internal</c> name) but a public-looking domain name does not: that is what a DNS-rebinding page uses to reach a program on the local network
    /// under its own name, and its requests would carry the local network's free pass. A request that went through a reverse proxy (forwarding headers) has no free pass
    /// and is not held to this, nor is the Automation API (it needs a key).</summary>
    internal static bool HostAllowed(HttpContext context, string[] allowedHosts, bool isV1 = false)
    {
        var host = WebAuthGate.HostOf(context.Request.Host.Value ?? "").ToLowerInvariant();
        if (host is "localhost" or "127.0.0.1" or "::1") return true;
        if (allowedHosts.Length > 0) return allowedHosts.Contains(host);
        if (isV1 || WebAuthPolicy.HasForwardingHeader(context.Request.Headers.Keys)) return true;

        return IsLocalStyleHost(host);
    }

    private static readonly string[] LocalSuffixes = { ".local", ".lan", ".home", ".internal", ".localdomain", ".home.arpa" };

    internal static bool IsLocalStyleHost(string host)
    {
        if (host.Length == 0) return true;   // an HTTP/1.0 request without a Host header
        if (IPAddress.TryParse(host, out _)) return true;
        if (!host.Contains('.')) return true;
        return LocalSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.Ordinal));
    }

    /// <summary>For every state-changing request: the browser says where it came from (<c>Sec-Fetch-Site</c>), or - older browsers - the <c>Origin</c> must be this site. A request with
    /// neither (a script, curl) is not a browser acting for a web page and passes.</summary>
    internal static bool CrossSiteAllowed(HttpContext context)
    {
        var method = context.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)) return true;

        var site = context.Request.Headers["Sec-Fetch-Site"].ToString();
        if (site.Length > 0) return site is "same-origin" or "none";

        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length == 0) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && string.Equals(uri.Authority, context.Request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAnonymousPath(string path) =>
        AnonymousFiles.Contains(path, StringComparer.OrdinalIgnoreCase)
        || AnonymousApi.Contains(path, StringComparer.OrdinalIgnoreCase)
        || path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase);

    // ---- the answers ---------------------------------------------------------------------------------------------------------------------

    private static bool WantsPage(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return false;
        return HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
    }

    private static async Task Plain(HttpContext context, int status, string text)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(text);
    }

    private static async Task Json(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { error = new { code, message } }));
    }

    private static async Task LoginRequired(HttpContext context, bool withRedirect = true)
    {
        if (withRedirect && WantsPage(context))
        {
            var back = Uri.EscapeDataString(context.Request.Path + context.Request.QueryString);
            context.Response.Redirect($"/login.html?returnUrl={back}");
            return;
        }

        await Json(context, StatusCodes.Status401Unauthorized, "login_required", "Sign in to use Compressarr.");
    }

    private static async Task NotSetUp(HttpContext context)
    {
        const string message = "Compressarr needs a username and password before it can be used from outside your network. Open Settings > Security on the computer running Compressarr (or on your local network) to set them.";
        if (WantsPage(context))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Compressarr</title><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"></head>"
                + "<body style=\"font-family: system-ui, sans-serif; max-width: 34em; margin: 4em auto; padding: 0 1em; line-height: 1.5\"><h2>Compressarr</h2><p>"
                + System.Net.WebUtility.HtmlEncode(message) + "</p></body></html>");
            return;
        }

        await Json(context, StatusCodes.Status401Unauthorized, "login_not_set_up", message);
    }

    private static async Task Locked(HttpContext context, TimeSpan? retryAfter)
    {
        if (retryAfter is { } wait) context.Response.Headers.RetryAfter = Math.Ceiling(wait.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await Json(context, StatusCodes.Status429TooManyRequests, "too_many_attempts", "Too many wrong attempts from this address. Wait a few minutes and try again.");
    }
}

public static class WebAuthExtensions
{
    /// <summary>Puts the allowed-hosts check, the cross-site check and the sign-in wall in front of everything after it: call it before the static files and the endpoints.</summary>
    public static IApplicationBuilder UseCompressarrWebAuth(this IApplicationBuilder app) => app.UseMiddleware<WebAuthMiddleware>();
}
