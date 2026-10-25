using Compressarr.Core.Api;
using Compressarr.Core.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Compressarr.Web.Api;

/// <summary>Marks an <c>/api/v1</c> endpoint with the scope a key needs for it (an endpoint without it needs <see cref="ApiKeyScope.Read"/>).</summary>
public sealed record ApiScopeRequirement(ApiKeyScope Scope);

public static class ApiErrors
{
    /// <summary>The one error shape of the v1 API: a real HTTP status and <c>{ "error": { "code": "...", "message": "..." } }</c>.</summary>
    public static IResult Result(int status, string code, string message) =>
        Results.Json(new { error = new { code, message } }, statusCode: status);
}

public static class ApiScopeExtensions
{
    public static TBuilder RequireApiScope<TBuilder>(this TBuilder builder, ApiKeyScope scope) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new ApiScopeRequirement(scope));
}

/// <summary>Slows down guessing: after <see cref="MaxFailures"/> wrong keys from one address within <see cref="Window"/>, that address is ignored for
/// <see cref="LockFor"/>. A key is 256 random bits, so this is about quiet logs and a misconfigured automation, not about the odds.</summary>
public sealed class ApiKeyThrottle
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan LockFor = TimeSpan.FromMinutes(1);

    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset WindowStart;
        public DateTimeOffset? LockedUntil;
    }

    private readonly TimeProvider _time;
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly object _lock = new();

    public ApiKeyThrottle() : this(TimeProvider.System) { }

    internal ApiKeyThrottle(TimeProvider time) => _time = time;

    /// <summary>How long this address must wait, or null when it may try.</summary>
    public TimeSpan? RetryAfter(string address)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(address, out var entry) || entry.LockedUntil is not { } until) return null;
            var wait = until - _time.GetUtcNow();
            if (wait > TimeSpan.Zero) return wait;
            _entries.Remove(address);
            return null;
        }
    }

    /// <summary>Notes a wrong key. True when this one locked the address (so the caller logs it once).</summary>
    public bool Failed(string address)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (_entries.Count > 1000) Prune(now);
            if (!_entries.TryGetValue(address, out var entry) || now - entry.WindowStart > Window)
            {
                entry = new Entry { WindowStart = now };
                _entries[address] = entry;
            }

            entry.Failures++;
            if (entry.Failures < MaxFailures || entry.LockedUntil is not null) return false;
            entry.LockedUntil = now + LockFor;
            return true;
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var stale in _entries.Where(kv => now - kv.Value.WindowStart > Window && (kv.Value.LockedUntil is null || kv.Value.LockedUntil < now)).Select(kv => kv.Key).ToList())
            _entries.Remove(stale);
    }
}

/// <summary>The door in front of every <c>/api/v1</c> endpoint: a key in <c>X-Api-Key</c> (or <c>Authorization: Bearer</c>), checked against the stored hashes, with
/// the scope the endpoint needs. The two keys always exist (Settings > Security); there is no anonymous mode.</summary>
public sealed class ApiKeyFilter : IEndpointFilter
{
    public const string ItemKey = "Compressarr.ApiKey";

    private readonly IApiKeyStore _keys;
    private readonly ApiKeyThrottle _throttle;
    private readonly IRunLogger _logger;

    public ApiKeyFilter(IApiKeyStore keys, ApiKeyThrottle throttle, IRunLogger logger)
    {
        _keys = keys;
        _throttle = throttle;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var address = http.Connection.RemoteIpAddress?.ToString() ?? "local";

        if (_throttle.RetryAfter(address) is { } wait)
        {
            http.Response.Headers.RetryAfter = Math.Ceiling(wait.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return ApiErrors.Result(StatusCodes.Status429TooManyRequests, "too_many_attempts", "Too many wrong keys from this address. Wait a minute and try again.");
        }

        var key = ReadKey(http.Request);
        if (key is null)
        {
            return ApiErrors.Result(StatusCodes.Status401Unauthorized, "missing_key", "Send your API key in the X-Api-Key header (or as Authorization: Bearer <key>). It is in Settings > Security.");
        }

        var info = _keys.Authenticate(key);
        if (info is null)
        {
            if (_throttle.Failed(address)) _logger.Log($"API: five wrong keys from {address}; ignoring that address for a minute.", LogSeverity.Info);
            return ApiErrors.Result(StatusCodes.Status401Unauthorized, "invalid_key", "That API key is not valid (it may have been revoked).");
        }

        var needs = http.GetEndpoint()?.Metadata.GetMetadata<ApiScopeRequirement>()?.Scope ?? ApiKeyScope.Read;
        if (!ApiKeys.Allows(info.Scope, needs))
            return ApiErrors.Result(StatusCodes.Status403Forbidden, "insufficient_scope", "This key can only read. Use a key with the control scope for this call.");

        http.Items[ItemKey] = info;
        return await next(context);
    }

    private static string? ReadKey(HttpRequest request)
    {
        var header = request.Headers["X-Api-Key"].ToString().Trim();
        if (header.Length > 0) return header;

        var authorization = request.Headers.Authorization.ToString().Trim();
        const string bearer = "Bearer ";
        return authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase) && authorization.Length > bearer.Length ? authorization[bearer.Length..].Trim() : null;
    }
}
