using System.Net;
using Compressarr.Core.Auth;
using Compressarr.Core.Logging;
using Compressarr.Web.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

/// <summary>Where a person may be sent after signing in: only a plain path on this site, never another site (<c>//evil.example</c>, <c>https://evil.example</c>, <c>/\evil</c>),
/// never the login page itself or an API address.</summary>
public static class ReturnUrls
{
    public static string Safe(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > 2000) return "/";
        if (url[0] != '/' || url.Length > 1 && (url[1] == '/' || url[1] == '\\')) return "/";
        if (url.Contains('\\') || url.Any(char.IsControl)) return "/";
        if (url.StartsWith("/login.html", StringComparison.OrdinalIgnoreCase) || url.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return "/";
        return url;
    }
}

/// <summary>The web interface's sign-in endpoints: who the browser is, the login form's call, sign out, and the Settings > Security card's own calls. See docs/design-login.md.</summary>
public static class AuthEndpoints
{
    public sealed record LoginRequest(string? Username, string? Password, bool? Remember, string? ReturnUrl);

    public sealed record AuthSettingsRequest(string? Method, string? Required, bool? TrustCgnat, string? Username, string? Password, string? PasswordConfirmation, string? CurrentPassword);

    private static IResult Error(int status, string code, string message) => Results.Json(new { error = new { code, message } }, statusCode: status);

    private static string AddressOf(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static bool IsFree(HttpContext http, AuthSettings settings) =>
        WebAuthPolicy.IsFree(settings, http.Connection.RemoteIpAddress, WebAuthPolicy.HasForwardingHeader(http.Request.Headers.Keys));

    private static void SetCookie(HttpContext http, string token, DateTimeOffset expires, bool persistent) =>
        http.Response.Cookies.Append(WebAuthMiddleware.SessionCookie, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = http.Request.IsHttps,
            Path = "/",
            IsEssential = true,
            Expires = persistent ? expires : null
        });

    private static void ClearCookie(HttpContext http) =>
        http.Response.Cookies.Delete(WebAuthMiddleware.SessionCookie, new CookieOptions { Path = "/", HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = http.Request.IsHttps });

    private static object Describe(AuthSettings s, bool free) => new
    {
        method = s.Method.ToString(),
        required = s.Required.ToString(),
        trustCgnat = s.TrustCgnat,
        username = s.Username,
        hasPassword = s.HasPassword,
        requestIsLocal = free
    };

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Anonymous: what the login page needs to know. The username is only given to someone who is free of the login anyway, or already signed in.
        app.MapGet("/api/auth/status", (HttpContext http, IWebAuth auth) =>
        {
            var settings = auth.GetSettings();
            var free = IsFree(http, settings);
            var session = http.Request.Cookies.TryGetValue(WebAuthMiddleware.SessionCookie, out var token) ? auth.ValidateSession(token) : null;
            var signedIn = session is not null;
            return Results.Json(new
            {
                method = settings.Method.ToString(),
                credentialsSet = settings.CredentialsSet,
                free,
                signedIn,
                needsLogin = settings.Method == AuthMethod.Forms && settings.CredentialsSet && !free && !signedIn,
                username = free || signedIn ? settings.Username : null
            });
        });

        app.MapPost("/api/auth/login", async (HttpContext http, LoginRequest request, IWebAuth auth, IRunLogger logger) =>
        {
            var address = AddressOf(http);
            var result = auth.Login(request.Username ?? "", request.Password ?? "", address, request.Remember ?? false);
            switch (result.Outcome)
            {
                case LoginOutcome.Success:
                    SetCookie(http, result.Token!, result.Expires!.Value, result.Persistent);
                    return Results.Json(new { ok = true, redirect = ReturnUrls.Safe(request.ReturnUrl) });
                case LoginOutcome.LockedOut:
                    return Locked(http, result.RetryAfter);
                case LoginOutcome.NotAvailable:
                    return Error(StatusCodes.Status409Conflict, "login_not_available", "Signing in with a login page is not turned on, or no username and password have been set yet.");
                default:
                    await Task.Delay(250);   // every wrong attempt waits a moment
                    if (result.RetryAfter is not null)
                    {
                        logger.Log($"Sign-in: five wrong passwords from {address}; ignoring that address for five minutes.", LogSeverity.Info);
                        return Locked(http, result.RetryAfter);
                    }

                    return Error(StatusCodes.Status401Unauthorized, "wrong_credentials", "The username or password is not right.");
            }
        });

        app.MapPost("/api/auth/logout", (HttpContext http, IWebAuth auth) =>
        {
            if (http.Request.Cookies.TryGetValue(WebAuthMiddleware.SessionCookie, out var token)) auth.EndSession(token);
            ClearCookie(http);
            return Results.Json(new { ok = true });
        });

        // The Security card. Behind the sign-in wall like everything else: a free (local) request, or a signed-in one.
        app.MapGet("/api/auth/settings", (HttpContext http, IWebAuth auth) =>
        {
            var settings = auth.GetSettings();
            return Results.Json(Describe(settings, IsFree(http, settings)));
        });

        app.MapPut("/api/auth/settings", (HttpContext http, AuthSettingsRequest request, IWebAuth auth, WebAuthGate gate) =>
        {
            if (!Enum.TryParse<AuthMethod>(request.Method, true, out var method)) return Error(StatusCodes.Status400BadRequest, "bad_request", "Authentication must be None, Forms or Basic.");
            if (!Enum.TryParse<AuthRequired>(request.Required, true, out var required)) return Error(StatusCodes.Status400BadRequest, "bad_request", "Authentication Required must be Enabled, DisabledForLocalAddresses or DisabledForLocalhost.");

            var password = request.Password ?? "";
            if (password.Length > 0 || (request.PasswordConfirmation ?? "").Length > 0)
            {
                if (password != (request.PasswordConfirmation ?? "")) return Error(StatusCodes.Status400BadRequest, "bad_request", "The two passwords are not the same.");
            }

            var current = auth.GetSettings();
            var address = AddressOf(http);

            // From outside the local network a signed-in person has to prove they know the password before anything about the sign-in can change.
            if (!IsFree(http, current) && current.CredentialsSet)
            {
                if (string.IsNullOrEmpty(request.CurrentPassword)) return Error(StatusCodes.Status403Forbidden, "current_password_required", "Enter the current password to change these settings.");
                if (!auth.CheckCurrentPassword(request.CurrentPassword, address)) return Error(StatusCodes.Status403Forbidden, "wrong_current_password", "The current password is not right.");
            }

            // Never save a state that nobody can get out of: asking everyone (or only this computer's neighbours) to sign in needs an account to sign in with.
            var usernameAfter = string.IsNullOrWhiteSpace(request.Username) ? current.Username : request.Username.Trim();
            var credentialsAfter = !string.IsNullOrEmpty(usernameAfter) && (password.Length > 0 || current.HasPassword);
            if (method != AuthMethod.None && required != AuthRequired.DisabledForLocalAddresses && !credentialsAfter)
                return Error(StatusCodes.Status400BadRequest, "bad_request", "Set a username and password first, or choose \"Disabled for Local Addresses\".");

            var sessionToken = http.Request.Cookies.TryGetValue(WebAuthMiddleware.SessionCookie, out var token) ? token : null;
            var session = sessionToken is null ? null : auth.ValidateSession(sessionToken);

            AuthSettings saved;
            try
            {
                saved = auth.SaveSettings(method, required, request.TrustCgnat ?? false, request.Username, password);
            }
            catch (ArgumentException ex)
            {
                return Error(StatusCodes.Status400BadRequest, "bad_request", ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Error(StatusCodes.Status503ServiceUnavailable, "unavailable", ex.Message);
            }

            gate.Invalidate();

            // A new password ended every session, this browser's too: give it a fresh one so the person who just changed it is not thrown out.
            if (password.Length > 0 && session is not null && saved.CredentialsSet)
            {
                var fresh = auth.StartSession(address, session.Persistent);
                SetCookie(http, fresh.Token!, fresh.Expires!.Value, fresh.Persistent);
            }

            return Results.Json(Describe(saved, IsFree(http, saved)));
        });

        app.MapPost("/api/auth/sign-out-everywhere", (HttpContext http, IWebAuth auth) =>
        {
            var ended = auth.EndAllSessions();
            ClearCookie(http);
            return Results.Json(new { ended });
        });
    }

    private static IResult Locked(HttpContext http, TimeSpan? retryAfter)
    {
        if (retryAfter is { } wait) http.Response.Headers.RetryAfter = Math.Ceiling(wait.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Error(StatusCodes.Status429TooManyRequests, "too_many_attempts", "Too many wrong attempts from this address. Wait a few minutes and try again.");
    }
}
