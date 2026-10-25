using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;

namespace Compressarr.Core.Api;

/// <summary>What a key may do. <see cref="Control"/> includes everything <see cref="Read"/> can.</summary>
public enum ApiKeyScope
{
    Read,
    Control
}

/// <summary>Which key a request presented: its display name (<c>API Key</c> or <c>Read-only API Key</c>) and scope.</summary>
public sealed record ApiKeyInfo(string Name, ApiKeyScope Scope);

/// <summary>The Automation API's two fixed keys, as Sonarr and Radarr keep theirs: an <b>API Key</b> (control) and a <b>Read-only API Key</b> (for dashboards), made on first
/// use, shown in Settings > Security with Copy and Regenerate, and kept readable in the state database. See docs/design-login.md.</summary>
public interface IApiKeyStore
{
    /// <summary>The key the text is, or null when it is neither key. Compared in constant time.</summary>
    ApiKeyInfo? Authenticate(string key);

    /// <summary>The key of this scope (made the first time it is asked for). Throws <see cref="InvalidOperationException"/> when the database cannot be opened.</summary>
    string GetKey(ApiKeyScope scope);

    /// <summary>Replaces the key of this scope; the old one stops working at once. Returns the new key.</summary>
    string Regenerate(ApiKeyScope scope);
}

public static class ApiKeys
{
    public const string KeyPrefix = "cmpk_";

    /// <summary>A new random key: <c>cmpk_</c> and 43 URL-safe characters (256 bits).</summary>
    public static string NewKey() => KeyPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string ScopeName(ApiKeyScope scope) => scope == ApiKeyScope.Control ? "control" : "read";

    public static string DisplayName(ApiKeyScope scope) => scope == ApiKeyScope.Control ? "API Key" : "Read-only API Key";

    public static bool TryParseScope(string? text, out ApiKeyScope scope)
    {
        scope = ApiKeyScope.Read;
        if (string.Equals(text, "read", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(text, "control", StringComparison.OrdinalIgnoreCase)) { scope = ApiKeyScope.Control; return true; }
        return false;
    }

    /// <summary>True when a key of scope <paramref name="have"/> may do something that needs <paramref name="need"/>.</summary>
    public static bool Allows(ApiKeyScope have, ApiKeyScope need) => have == ApiKeyScope.Control || need == ApiKeyScope.Read;
}

/// <summary>The keys in the queue's SQLite file (<c>compressarr.state.db</c>, table <c>api_secrets</c>). If that file cannot be opened nothing is accepted and no key can be
/// shown: the API stays closed rather than open.</summary>
public sealed class SqliteApiKeyStore : IApiKeyStore
{
    private readonly SqliteResumeStateStore _store;
    private readonly Func<string> _resumeFilePath;

    public SqliteApiKeyStore(SqliteResumeStateStore store) : this(store, AppPaths.GetResumeFilePath) { }

    /// <summary>For tests that keep the state somewhere other than the app data folder.</summary>
    internal SqliteApiKeyStore(SqliteResumeStateStore store, Func<string> resumeFilePath)
    {
        _store = store;
        _resumeFilePath = resumeFilePath;
    }

    private static string Kind(ApiKeyScope scope) => ApiKeys.ScopeName(scope);

    /// <summary>Both keys, made on first use. Null when the database cannot be opened.</summary>
    private Dictionary<ApiKeyScope, string>? ReadBoth()
    {
        using var connection = _store.OpenReady(_resumeFilePath());
        if (connection is null) return null;

        foreach (var scope in new[] { ApiKeyScope.Control, ApiKeyScope.Read })
        {
            using var make = connection.CreateCommand();
            make.CommandText = "INSERT OR IGNORE INTO api_secrets (kind, key, created_at) VALUES (@kind, @key, @now);";
            make.Parameters.AddWithValue("@kind", Kind(scope));
            make.Parameters.AddWithValue("@key", ApiKeys.NewKey());
            make.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            make.ExecuteNonQuery();
        }

        var keys = new Dictionary<ApiKeyScope, string>();
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT kind, key FROM api_secrets;";
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            if (ApiKeys.TryParseScope(reader.GetString(0), out var scope)) keys[scope] = reader.GetString(1);
        }

        return keys;
    }

    public ApiKeyInfo? Authenticate(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200) return null;
        var keys = ReadBoth();
        if (keys is null) return null;

        var candidate = Encoding.UTF8.GetBytes(key);
        ApiKeyInfo? found = null;
        foreach (var (scope, stored) in keys)
        {
            // Both are always compared, so which key it was (or that it was neither) cannot be told from how long the answer took.
            if (CryptographicOperations.FixedTimeEquals(candidate, Encoding.UTF8.GetBytes(stored))) found = new ApiKeyInfo(ApiKeys.DisplayName(scope), scope);
        }

        return found;
    }

    public string GetKey(ApiKeyScope scope)
    {
        var keys = ReadBoth() ?? throw new InvalidOperationException("The queue database could not be opened, so the API keys cannot be shown right now.");
        return keys[scope];
    }

    public string Regenerate(ApiKeyScope scope)
    {
        ReadBoth();   // makes them if they were never made
        using var connection = _store.OpenReady(_resumeFilePath())
            ?? throw new InvalidOperationException("The queue database could not be opened, so the API key cannot be replaced right now.");

        var key = ApiKeys.NewKey();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE api_secrets SET key = @key, regenerated_at = @now WHERE kind = @kind;";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@kind", Kind(scope));
        command.ExecuteNonQuery();
        return key;
    }
}
