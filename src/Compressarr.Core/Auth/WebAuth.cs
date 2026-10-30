using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;

namespace Compressarr.Core.Auth;

/// <summary>Slows down guessing the way Sonarr does: <see cref="MaxFailures"/> wrong attempts from one address lock that address for <see cref="LockFor"/> (a correct
/// password during the lock is refused too); a success clears the count.</summary>
public sealed class AttemptThrottle
{
    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset WindowStart;
        public DateTimeOffset? LockedUntil;
    }

    private readonly int _maxFailures;
    private readonly TimeSpan _window;
    private readonly TimeSpan _lockFor;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly object _lock = new();

    public AttemptThrottle(int maxFailures, TimeSpan window, TimeSpan lockFor, TimeProvider? time = null)
    {
        _maxFailures = maxFailures;
        _window = window;
        _lockFor = lockFor;
        _time = time ?? TimeProvider.System;
    }

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

    /// <summary>Notes a wrong attempt. True when this one locked the address (so the caller logs it once).</summary>
    public bool Failed(string address)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (_entries.Count > 1000) Prune(now);
            if (!_entries.TryGetValue(address, out var entry) || now - entry.WindowStart > _window)
            {
                entry = new Entry { WindowStart = now };
                _entries[address] = entry;
            }

            entry.Failures++;
            if (entry.Failures < _maxFailures || entry.LockedUntil is not null) return false;
            entry.LockedUntil = now + _lockFor;
            return true;
        }
    }

    public void Succeeded(string address)
    {
        lock (_lock) _entries.Remove(address);
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var stale in _entries.Where(kv => now - kv.Value.WindowStart > _window && (kv.Value.LockedUntil is null || kv.Value.LockedUntil < now)).Select(kv => kv.Key).ToList())
            _entries.Remove(stale);
    }
}

/// <summary>The web interface's sign-in: one account (username and password), the method and the "for whom is it asked" setting, and the server-side sessions behind the
/// login cookie. Everything is in the state database; none of it is in the settings file. See docs/design-login.md.</summary>
public interface IWebAuth
{
    AuthSettings GetSettings();

    /// <summary>Saves the settings. <paramref name="newPassword"/> blank keeps the stored password; a new one replaces it and ends every session. Throws
    /// <see cref="ArgumentException"/> with a plain message when something is not acceptable (nothing is saved then).</summary>
    AuthSettings SaveSettings(AuthMethod method, AuthRequired required, bool trustCgnat, string? username, string? newPassword);

    /// <summary>Checks a username and password from the login form (Forms method only) and makes a session when they are right.</summary>
    LoginResult Login(string username, string password, string address, bool remember);

    /// <summary>Checks an <c>Authorization: Basic</c> header (Basic method only). Success carries no token.</summary>
    LoginResult AuthenticateBasic(string? authorizationHeader, string address);

    /// <summary>True when <paramref name="password"/> is the account's current password (counts against the address's attempts when it is not). Asked before credentials
    /// are changed from outside the local network.</summary>
    bool CheckCurrentPassword(string password, string address);

    /// <summary>Starts a session for the account without asking for the password: for the one case where the caller already proved it (the current password was just
    /// checked, and changing it ended every session, this browser's too). Throws when no account is set up.</summary>
    LoginResult StartSession(string address, bool persistent);

    /// <summary>The session behind a cookie, extended by its sliding week, or null when it is unknown or has lapsed.</summary>
    SessionInfo? ValidateSession(string token);

    void EndSession(string token);

    /// <summary>Ends every session, except <paramref name="exceptToken"/> when given. Returns how many ended.</summary>
    int EndAllSessions(string? exceptToken = null);

    /// <summary>How long the address is locked out, or null.</summary>
    TimeSpan? LockedFor(string address);

    /// <summary>The way back for a forgotten password under "Enabled": "Disabled for Local Addresses" again, so the local network can open Settings and set a new one.</summary>
    void ResetToLocalOnly();

    /// <summary>Does <see cref="ResetToLocalOnly"/> when the environment variable <c>COMPRESSARR_RESET_LOGIN</c> is <c>1</c> (for a container or a service, once at start-up).</summary>
    bool ApplyEnvironmentReset();
}

public sealed class SqliteWebAuth : IWebAuth
{
    public const string ResetVariable = "COMPRESSARR_RESET_LOGIN";
    public const int MinPasswordLength = 8;
    public const int MaxUsernameLength = 64;
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);
    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(1);

    private readonly SqliteResumeStateStore _store;
    private readonly Func<string> _resumeFilePath;
    private readonly TimeProvider _time;
    private readonly AttemptThrottle _throttle;
    private readonly Dictionary<string, DateTimeOffset> _basicVerified = new();   // SHA-256 of "user:password" -> good until
    private readonly object _basicLock = new();
    private static readonly TimeSpan BasicRemembered = TimeSpan.FromMinutes(5);

    public SqliteWebAuth(SqliteResumeStateStore store) : this(store, AppPaths.GetResumeFilePath, TimeProvider.System) { }

    /// <summary>For tests that keep the state somewhere other than the app data folder, with their own clock.</summary>
    internal SqliteWebAuth(SqliteResumeStateStore store, Func<string> resumeFilePath, TimeProvider time)
    {
        _store = store;
        _resumeFilePath = resumeFilePath;
        _time = time;
        _throttle = new AttemptThrottle(5, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5), time);
    }

    // ---- settings ------------------------------------------------------------------------------------------------------------------------

    private sealed record Stored(AuthSettings Settings, string? PasswordHash);

    private Stored Read()
    {
        using var connection = _store.OpenReady(_resumeFilePath());
        if (connection is null) return new Stored(AuthSettings.Unavailable, null);   // an unreadable database never frees anyone the settings might have held back

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT method, required, trust_cgnat, username, password_hash FROM auth WHERE id = 1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new Stored(AuthSettings.Default, null);

        var hash = reader.IsDBNull(4) ? null : reader.GetString(4);
        var settings = new AuthSettings(
            Enum.TryParse<AuthMethod>(reader.GetString(0), true, out var method) ? method : AuthMethod.Forms,
            Enum.TryParse<AuthRequired>(reader.GetString(1), true, out var required) ? required : AuthRequired.DisabledForLocalAddresses,
            reader.GetInt32(2) != 0,
            reader.IsDBNull(3) ? null : reader.GetString(3),
            !string.IsNullOrEmpty(hash));
        return new Stored(settings, hash);
    }

    public AuthSettings GetSettings() => Read().Settings;

    public AuthSettings SaveSettings(AuthMethod method, AuthRequired required, bool trustCgnat, string? username, string? newPassword)
    {
        var current = Read();
        var name = (username ?? "").Trim().ToLowerInvariant();
        if (name.Length > MaxUsernameLength || name.Any(char.IsControl)) throw new ArgumentException($"The username can be at most {MaxUsernameLength} characters, with no control characters.");
        if (name.Length == 0 && !string.IsNullOrEmpty(current.Settings.Username) && string.IsNullOrEmpty(newPassword)) name = current.Settings.Username!;

        var hash = current.PasswordHash;
        var passwordChanged = !string.IsNullOrEmpty(newPassword);
        if (passwordChanged)
        {
            if (name.Length == 0) throw new ArgumentException("Choose a username as well as a password.");
            if (newPassword!.Length < MinPasswordLength) throw new ArgumentException($"The password needs at least {MinPasswordLength} characters.");
            if (string.Equals(newPassword, name, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The password cannot be the same as the username.");
            hash = PasswordHasher.Hash(newPassword);
        }
        else if (name.Length > 0 && string.IsNullOrEmpty(hash))
        {
            throw new ArgumentException("Choose a password for that username.");
        }

        using var connection = _store.OpenReady(_resumeFilePath())
            ?? throw new InvalidOperationException("The queue database could not be opened, so the sign-in settings cannot be saved right now.");
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO auth (id, method, required, trust_cgnat, username, password_hash, updated_at) VALUES (1, @method, @required, @cgnat, @username, @hash, @now)
                ON CONFLICT (id) DO UPDATE SET method = excluded.method, required = excluded.required, trust_cgnat = excluded.trust_cgnat, username = excluded.username,
                                               password_hash = excluded.password_hash, updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("@method", method.ToString());
            command.Parameters.AddWithValue("@required", required.ToString());
            command.Parameters.AddWithValue("@cgnat", trustCgnat ? 1 : 0);
            command.Parameters.AddWithValue("@username", name.Length == 0 ? DBNull.Value : name);
            command.Parameters.AddWithValue("@hash", (object?)hash ?? DBNull.Value);
            command.Parameters.AddWithValue("@now", Iso(_time.GetUtcNow()));
            command.ExecuteNonQuery();
        }

        lock (_basicLock) _basicVerified.Clear();
        if (passwordChanged) EndAllSessions();
        return new AuthSettings(method, required, trustCgnat, name.Length == 0 ? null : name, !string.IsNullOrEmpty(hash));
    }

    // ---- passwords ------------------------------------------------------------------------------------------------------------------------

    private bool Check(Stored stored, string username, string password, string address)
    {
        // Both halves are always worked out, so how long a wrong username takes tells nothing.
        var userOk = string.Equals(username.Trim().ToLowerInvariant(), stored.Settings.Username, StringComparison.Ordinal);
        var passwordOk = PasswordHasher.Verify(password, stored.PasswordHash);
        if (!(userOk && passwordOk))
        {
            _throttle.Failed(address);
            return false;
        }

        _throttle.Succeeded(address);
        if (PasswordHasher.NeedsRehash(stored.PasswordHash)) Rehash(password);
        return true;
    }

    private void Rehash(string password)
    {
        using var connection = _store.OpenReady(_resumeFilePath());
        if (connection is null) return;
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE auth SET password_hash = @hash WHERE id = 1;";
        command.Parameters.AddWithValue("@hash", PasswordHasher.Hash(password));
        command.ExecuteNonQuery();
    }

    public TimeSpan? LockedFor(string address) => _throttle.RetryAfter(address);

    public LoginResult Login(string username, string password, string address, bool remember)
    {
        var stored = Read();
        if (stored.Settings.Method != AuthMethod.Forms || !stored.Settings.CredentialsSet) return new LoginResult(LoginOutcome.NotAvailable);
        if (_throttle.RetryAfter(address) is { } wait) return new LoginResult(LoginOutcome.LockedOut, RetryAfter: wait);

        if (!Check(stored, username ?? "", password ?? "", address))
            return _throttle.RetryAfter(address) is { } nowLocked ? new LoginResult(LoginOutcome.WrongCredentials, RetryAfter: nowLocked) : new LoginResult(LoginOutcome.WrongCredentials);

        var (token, expires) = CreateSession(stored.Settings.Username!, address, remember);
        return new LoginResult(LoginOutcome.Success, token, expires, remember);
    }

    public LoginResult AuthenticateBasic(string? authorizationHeader, string address)
    {
        var stored = Read();
        if (stored.Settings.Method != AuthMethod.Basic || !stored.Settings.CredentialsSet) return new LoginResult(LoginOutcome.NotAvailable);
        if (!TryParseBasic(authorizationHeader, out var username, out var password)) return new LoginResult(LoginOutcome.NoCredentials);   // the browser's first, header-less request: not a failure

        // A browser repeats its credentials on every request, so a pair that was just verified against the current account is trusted for a few minutes (cleared when the account changes).
        var remembered = HashToken(username.Trim().ToLowerInvariant() + ":" + password);
        lock (_basicLock)
        {
            if (_basicVerified.TryGetValue(remembered, out var until) && until > _time.GetUtcNow() && string.Equals(username.Trim().ToLowerInvariant(), stored.Settings.Username, StringComparison.Ordinal))
                return new LoginResult(LoginOutcome.Success);
        }

        if (_throttle.RetryAfter(address) is { } wait) return new LoginResult(LoginOutcome.LockedOut, RetryAfter: wait);

        if (Check(stored, username, password, address))
        {
            lock (_basicLock) _basicVerified[remembered] = _time.GetUtcNow() + BasicRemembered;
            return new LoginResult(LoginOutcome.Success);
        }

        return _throttle.RetryAfter(address) is { } nowLocked ? new LoginResult(LoginOutcome.WrongCredentials, RetryAfter: nowLocked) : new LoginResult(LoginOutcome.WrongCredentials);
    }

    internal static bool TryParseBasic(string? header, out string username, out string password)
    {
        username = password = "";
        const string scheme = "Basic ";
        if (header is null || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) || header.Length == scheme.Length) return false;

        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(header[scheme.Length..].Trim()));
            var colon = text.IndexOf(':');
            if (colon < 0) return false;
            username = text[..colon];
            password = text[(colon + 1)..];
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public bool CheckCurrentPassword(string password, string address)
    {
        var stored = Read();
        if (!stored.Settings.CredentialsSet) return false;
        if (_throttle.RetryAfter(address) is not null) return false;

        var ok = PasswordHasher.Verify(password ?? "", stored.PasswordHash);
        if (ok) _throttle.Succeeded(address); else _throttle.Failed(address);
        return ok;
    }

    // ---- sessions ------------------------------------------------------------------------------------------------------------------------

    private static string Iso(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private (string Token, DateTimeOffset Expires) CreateSession(string username, string address, bool persistent)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = _time.GetUtcNow();
        var expires = now + SessionLifetime;

        using var connection = _store.OpenReady(_resumeFilePath()) ?? throw new InvalidOperationException("The queue database could not be opened, so a sign-in cannot be remembered right now.");
        using (var purge = connection.CreateCommand())
        {
            purge.CommandText = "DELETE FROM web_sessions WHERE expires_at < @now;";
            purge.Parameters.AddWithValue("@now", Iso(now));
            purge.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO web_sessions (token_hash, username, created_at, last_used_at, expires_at, persistent, first_address) VALUES (@hash, @user, @now, @now, @expires, @persistent, @address);";
        insert.Parameters.AddWithValue("@hash", HashToken(token));
        insert.Parameters.AddWithValue("@user", username);
        insert.Parameters.AddWithValue("@now", Iso(now));
        insert.Parameters.AddWithValue("@expires", Iso(expires));
        insert.Parameters.AddWithValue("@persistent", persistent ? 1 : 0);
        insert.Parameters.AddWithValue("@address", address);
        insert.ExecuteNonQuery();
        return (token, expires);
    }

    public LoginResult StartSession(string address, bool persistent)
    {
        var stored = Read();
        if (!stored.Settings.CredentialsSet) throw new InvalidOperationException("No account is set up.");

        var (token, expires) = CreateSession(stored.Settings.Username!, address, persistent);
        return new LoginResult(LoginOutcome.Success, token, expires, persistent);
    }

    public SessionInfo? ValidateSession(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100) return null;

        using var connection = _store.OpenReady(_resumeFilePath());
        if (connection is null) return null;

        var hash = HashToken(token);
        string username;
        bool persistent;
        DateTimeOffset lastUsed, expires;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT username, persistent, last_used_at, expires_at FROM web_sessions WHERE token_hash = @hash;";
            command.Parameters.AddWithValue("@hash", hash);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            username = reader.GetString(0);
            persistent = reader.GetInt32(1) != 0;
            lastUsed = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            expires = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        }

        var now = _time.GetUtcNow();
        if (expires <= now)
        {
            EndSession(token);
            return null;
        }

        // The account the session was made for must still be the account (a changed username ends the sign-in).
        if (!string.Equals(username, Read().Settings.Username, StringComparison.Ordinal))
        {
            EndSession(token);
            return null;
        }

        if (now - lastUsed > TouchEvery)
        {
            expires = now + SessionLifetime;
            using var touch = connection.CreateCommand();
            touch.CommandText = "UPDATE web_sessions SET last_used_at = @now, expires_at = @expires WHERE token_hash = @hash;";
            touch.Parameters.AddWithValue("@now", Iso(now));
            touch.Parameters.AddWithValue("@expires", Iso(expires));
            touch.Parameters.AddWithValue("@hash", hash);
            touch.ExecuteNonQuery();
        }

        return new SessionInfo(username, persistent, expires);
    }

    public void EndSession(string token)
    {
        using var connection = _store.OpenReady(_resumeFilePath());
        if (connection is null) return;
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM web_sessions WHERE token_hash = @hash;";
        command.Parameters.AddWithValue("@hash", HashToken(token ?? ""));
        command.ExecuteNonQuery();
    }

    public int EndAllSessions(string? exceptToken = null)
    {
        using var connection = _store.OpenReady(_resumeFilePath());
        if (connection is null) return 0;
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM web_sessions WHERE (@keep IS NULL OR token_hash <> @keep);";
        command.Parameters.AddWithValue("@keep", string.IsNullOrEmpty(exceptToken) ? DBNull.Value : HashToken(exceptToken));
        return command.ExecuteNonQuery();
    }

    // ---- the way back --------------------------------------------------------------------------------------------------------------------

    public void ResetToLocalOnly()
    {
        lock (_basicLock) _basicVerified.Clear();
        var current = Read().Settings;
        using var connection = _store.OpenReady(_resumeFilePath());
        if (connection is null) return;
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO auth (id, method, required, trust_cgnat, username, password_hash, updated_at) VALUES (1, @method, @required, @cgnat, NULL, NULL, @now)
            ON CONFLICT (id) DO UPDATE SET required = excluded.required, updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("@method", (current.Method == AuthMethod.None ? AuthMethod.Forms : current.Method).ToString());
        command.Parameters.AddWithValue("@required", AuthRequired.DisabledForLocalAddresses.ToString());
        command.Parameters.AddWithValue("@cgnat", current.TrustCgnat ? 1 : 0);
        command.Parameters.AddWithValue("@now", Iso(_time.GetUtcNow()));
        command.ExecuteNonQuery();
    }

    public bool ApplyEnvironmentReset()
    {
        if (Environment.GetEnvironmentVariable(ResetVariable) != "1") return false;
        ResetToLocalOnly();
        return true;
    }
}
