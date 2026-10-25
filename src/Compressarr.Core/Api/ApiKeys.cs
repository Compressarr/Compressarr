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

/// <summary>A key as shown in the list: never the key itself, only the first few characters so the user can tell keys apart.</summary>
public sealed record ApiKeyInfo(long Id, string Name, ApiKeyScope Scope, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt);

/// <summary>A key just created: the only time its full text exists anywhere (only a hash is stored).</summary>
public sealed record CreatedApiKey(ApiKeyInfo Info, string Key);

/// <summary>The keys for the Automation API (<c>/api/v1</c>), kept as SHA-256 hashes in the state database. See docs/design-api.md.</summary>
public interface IApiKeyStore
{
    /// <summary>The key the text belongs to, or null when it is not a current key. Notes when the key was last used (at most once a minute).</summary>
    ApiKeyInfo? Authenticate(string key);

    /// <summary>Makes a new key. The name is trimmed and must be 1 to 60 characters; at most <see cref="ApiKeys.MaxKeys"/> keys exist at once.</summary>
    CreatedApiKey Create(string name, ApiKeyScope scope);

    IReadOnlyList<ApiKeyInfo> List();

    /// <summary>Removes a key for good (it stops working at once). False if there was no such key.</summary>
    bool Revoke(long id);
}

public static class ApiKeys
{
    public const int MaxKeys = 20;
    public const int MaxNameLength = 60;
    public const string KeyPrefix = "cmpk_";

    /// <summary>A new random key: <c>cmpk_</c> and 43 URL-safe characters (256 bits).</summary>
    public static string NewKey() => KeyPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>What is shown to tell keys apart: <c>cmpk_</c> and the next four characters.</summary>
    public static string DisplayPrefix(string key) => key.Length <= KeyPrefix.Length + 4 ? key : key[..(KeyPrefix.Length + 4)];

    public static string ScopeName(ApiKeyScope scope) => scope == ApiKeyScope.Control ? "control" : "read";

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

/// <summary>The keys in the queue's SQLite file (<c>compressarr.state.db</c>, table <c>api_keys</c>). If that file cannot be opened there are no keys and none can be
/// made: <see cref="Create"/> says so, and <see cref="Authenticate"/> rejects everything - the API stays closed rather than open.</summary>
public sealed class SqliteApiKeyStore : IApiKeyStore
{
    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(1);

    private readonly SqliteResumeStateStore _store;
    private readonly Func<string> _resumeFilePath;

    public SqliteApiKeyStore(SqliteResumeStateStore store) : this(store, AppPaths.GetResumeFilePath) { }

    /// <summary>For tests that keep the state somewhere other than the app data folder.</summary>
    internal SqliteApiKeyStore(SqliteResumeStateStore store, Func<string> resumeFilePath)
    {
        _store = store;
        _resumeFilePath = resumeFilePath;
    }

    public ApiKeyInfo? Authenticate(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200) return null;

        using var connection = _store.OpenReady(_resumeFilePath());
        if (connection is null) return null;

        ApiKeyInfo? found;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, name, scope, prefix, created_at, last_used_at FROM api_keys WHERE hash = @hash;";
            command.Parameters.AddWithValue("@hash", ApiKeys.Hash(key));
            using var reader = command.ExecuteReader();
            found = reader.Read() ? ReadInfo(reader) : null;
        }

        if (found is null) return null;

        var now = DateTimeOffset.UtcNow;
        if (found.LastUsedAt is null || now - found.LastUsedAt > TouchEvery)
        {
            using var touch = connection.CreateCommand();
            touch.CommandText = "UPDATE api_keys SET last_used_at = @now WHERE id = @id;";
            touch.Parameters.AddWithValue("@now", now.ToString("O", CultureInfo.InvariantCulture));
            touch.Parameters.AddWithValue("@id", found.Id);
            touch.ExecuteNonQuery();
        }

        return found;
    }

    public CreatedApiKey Create(string name, ApiKeyScope scope)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is 0 or > ApiKeys.MaxNameLength) throw new ArgumentException($"A key needs a name of 1 to {ApiKeys.MaxNameLength} characters.", nameof(name));

        using var connection = _store.OpenReady(_resumeFilePath())
            ?? throw new InvalidOperationException("The queue database could not be opened, so keys cannot be saved right now.");

        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM api_keys;";
            if (Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture) >= ApiKeys.MaxKeys)
                throw new InvalidOperationException($"There are already {ApiKeys.MaxKeys} keys. Revoke one you no longer use first.");
        }

        var key = ApiKeys.NewKey();
        var created = DateTimeOffset.UtcNow;
        long id;
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO api_keys (name, scope, hash, prefix, created_at) VALUES (@name, @scope, @hash, @prefix, @created); SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("@name", trimmed);
            insert.Parameters.AddWithValue("@scope", ApiKeys.ScopeName(scope));
            insert.Parameters.AddWithValue("@hash", ApiKeys.Hash(key));
            insert.Parameters.AddWithValue("@prefix", ApiKeys.DisplayPrefix(key));
            insert.Parameters.AddWithValue("@created", created.ToString("O", CultureInfo.InvariantCulture));
            id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        return new CreatedApiKey(new ApiKeyInfo(id, trimmed, scope, ApiKeys.DisplayPrefix(key), created, null), key);
    }

    public IReadOnlyList<ApiKeyInfo> List()
    {
        using var connection = _store.OpenReady(_resumeFilePath());
        if (connection is null) return Array.Empty<ApiKeyInfo>();

        var keys = new List<ApiKeyInfo>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, scope, prefix, created_at, last_used_at FROM api_keys ORDER BY id;";
        using var reader = command.ExecuteReader();
        while (reader.Read()) keys.Add(ReadInfo(reader));
        return keys;
    }

    public bool Revoke(long id)
    {
        using var connection = _store.OpenReady(_resumeFilePath());
        if (connection is null) return false;

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM api_keys WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id);
        return command.ExecuteNonQuery() > 0;
    }

    private static ApiKeyInfo ReadInfo(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        ApiKeys.TryParseScope(reader.GetString(2), out var scope) ? scope : ApiKeyScope.Read,   // an unknown scope is the lesser one
        reader.GetString(3),
        DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
}
