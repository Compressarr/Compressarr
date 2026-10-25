using System.Net;

namespace Compressarr.Core.Auth;

/// <summary>How people sign in to the web interface. <see cref="Forms"/> is the login page (and the default), <see cref="Basic"/> is the browser's own sign-in popup,
/// <see cref="None"/> means no sign-in at all (anyone who can reach the port can use Compressarr).</summary>
public enum AuthMethod
{
    None,
    Forms,
    Basic
}

/// <summary>For which requests credentials are asked. <see cref="DisabledForLocalAddresses"/> (the default) frees requests from the local network; <see cref="DisabledForLocalhost"/>
/// frees only this computer; <see cref="Enabled"/> asks everyone.</summary>
public enum AuthRequired
{
    Enabled,
    DisabledForLocalAddresses,
    DisabledForLocalhost
}

/// <summary>The sign-in settings as the Security card shows them. The password hash never leaves the service.</summary>
public sealed record AuthSettings(AuthMethod Method, AuthRequired Required, bool TrustCgnat, string? Username, bool HasPassword)
{
    public static AuthSettings Default { get; } = new(AuthMethod.Forms, AuthRequired.DisabledForLocalAddresses, false, null, false);

    /// <summary>True once a username and password exist, so there is something to sign in with.</summary>
    public bool CredentialsSet => HasPassword && !string.IsNullOrEmpty(Username);
}

public enum LoginOutcome
{
    /// <summary>Signed in; a session was made.</summary>
    Success,

    /// <summary>The username or password is not right (the message never says which).</summary>
    WrongCredentials,

    /// <summary>This address has failed too often and is locked out for a while.</summary>
    LockedOut,

    /// <summary>No username and password have been set up yet, or the method is not the one asked for.</summary>
    NotAvailable,

    /// <summary>A Basic request that carried no credentials at all (the browser's first request, before its popup): not a failed attempt.</summary>
    NoCredentials
}

public sealed record LoginResult(LoginOutcome Outcome, string? Token = null, DateTimeOffset? Expires = null, bool Persistent = false, TimeSpan? RetryAfter = null);

/// <summary>A session that is still good. <see cref="Persistent"/> says whether its cookie should outlive the browser (Remember me).</summary>
public sealed record SessionInfo(string Username, bool Persistent, DateTimeOffset Expires);

/// <summary>Which requests need credentials. Pure, so every row of the table can be tested.</summary>
public static class WebAuthPolicy
{
    /// <summary>Headers a reverse proxy adds. Any one of them on a request means the TCP peer is a proxy, not the person: never "local".</summary>
    public static readonly string[] ForwardingHeaders =
    {
        "X-Forwarded-For", "Forwarded", "X-Real-IP", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Forwarded-Port", "X-Client-IP", "CF-Connecting-IP", "True-Client-IP",
        "Via", "X-Forwarded-Server", "X-Original-Forwarded-For", "X-Cluster-Client-IP", "Fastly-Client-IP", "X-ProxyUser-Ip"
    };

    public static bool HasForwardingHeader(IEnumerable<string> headerNames) =>
        headerNames.Any(name => ForwardingHeaders.Contains(name, StringComparer.OrdinalIgnoreCase));

    /// <summary>True when the request needs no credentials: no sign-in at all, or a request from the local network (or this computer) that no proxy has touched.
    /// <paramref name="peer"/> is the TCP peer's address, nothing read from a header; unknown means not local.</summary>
    public static bool IsFree(AuthSettings settings, IPAddress? peer, bool hasForwardingHeader)
    {
        if (settings.Method == AuthMethod.None) return true;
        if (peer is null || hasForwardingHeader) return false;

        return settings.Required switch
        {
            AuthRequired.DisabledForLocalAddresses => LocalNetwork.IsLocalAddress(peer) || (settings.TrustCgnat && LocalNetwork.IsCgnatAddress(peer)),
            AuthRequired.DisabledForLocalhost => LocalNetwork.IsLoopback(peer),
            _ => false
        };
    }
}
