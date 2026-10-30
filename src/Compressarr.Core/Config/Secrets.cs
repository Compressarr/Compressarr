using System.Text.Json.Nodes;
using Compressarr.Core.Conversion;
using Compressarr.Core.Logging;
using Compressarr.Core.Notifications;
using Microsoft.Data.Sqlite;

namespace Compressarr.Core.Config;

/// <summary>Where the keys, tokens and webhook addresses are kept: the <c>secrets</c> table of <c>compressarr.state.db</c>, not <c>compressarr.settings.json</c>, so the settings file can
/// be shared, attached to a support request or put in a repository without leaking them. They stay readable (Compressarr has to send them), so this is hygiene against accidental
/// sharing, not encryption, and backups still carry them (they exist to restore the same install). See docs/design-api.md, stage 3c.</summary>
public interface ISecretStore
{
    /// <summary>Every saved secret by name; null when the database cannot be opened.</summary>
    IReadOnlyDictionary<string, string>? ReadAll();

    /// <summary>Makes the saved set exactly <paramref name="secrets"/> (a name that is missing is removed). False when the database cannot be written, so the caller keeps the secrets
    /// somewhere else rather than losing them.</summary>
    bool ReplaceAll(IReadOnlyDictionary<string, string> secrets);
}

/// <summary>The secrets in the queue's SQLite file. Nothing is written when the set is already what is stored (the settings are saved often).</summary>
public sealed class SqliteSecretStore : ISecretStore
{
    private readonly SqliteResumeStateStore _store;
    private readonly Func<string> _resumeFilePath;

    public SqliteSecretStore(SqliteResumeStateStore store) : this(store, AppPaths.GetResumeFilePath) { }

    /// <summary>For tests that keep the state somewhere other than the app data folder.</summary>
    internal SqliteSecretStore(SqliteResumeStateStore store, Func<string> resumeFilePath)
    {
        _store = store;
        _resumeFilePath = resumeFilePath;
    }

    public IReadOnlyDictionary<string, string>? ReadAll()
    {
        try
        {
            using var connection = _store.OpenReady(_resumeFilePath());
            return connection is null ? null : Read(connection);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public bool ReplaceAll(IReadOnlyDictionary<string, string> secrets)
    {
        try
        {
            using var connection = _store.OpenReady(_resumeFilePath());
            if (connection is null) return false;

            var current = Read(connection);
            if (current.Count == secrets.Count && secrets.All(kv => current.TryGetValue(kv.Key, out var value) && value == kv.Value)) return true;

            using var transaction = connection.BeginTransaction();
            HistoryDatabase.Execute(connection, "DELETE FROM secrets;", transaction);
            var now = HistoryDatabase.Iso(DateTime.UtcNow);
            foreach (var (name, value) in secrets)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO secrets (name, value, updated_at) VALUES (@name, @value, @now);";
                insert.Parameters.AddWithValue("@name", name);
                insert.Parameters.AddWithValue("@value", value);
                insert.Parameters.AddWithValue("@now", now);
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static Dictionary<string, string> Read(SqliteConnection connection)
    {
        var all = new Dictionary<string, string>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, value FROM secrets;";
        using var reader = command.ExecuteReader();
        while (reader.Read()) all[reader.GetString(0)] = reader.GetString(1);
        return all;
    }
}

/// <summary>Which values of the settings are secrets, and moving them between the settings object and the store. The settings object always carries them (every part of the app reads them
/// from it); only the file on disk is without.</summary>
public static class ConfigSecrets
{
    public const string SonarrKey = "sonarr.apiKey";
    public const string RadarrKey = "radarr.apiKey";
    public const string TmdbKey = "posters.tmdbApiKey";
    public const string OmdbKey = "posters.omdbApiKey";

    /// <summary>For each notifier type, the setting keys its fields mark <c>Secret</c> (webhook addresses, tokens, user keys).</summary>
    public static Dictionary<string, HashSet<string>> ChannelKeys(IEnumerable<INotifier> notifiers) =>
        notifiers.ToDictionary(n => n.Type, n => n.Fields.Where(f => f.Secret).Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    public static HashSet<string> KeysFor(Dictionary<string, HashSet<string>> channelKeys, string type) =>
        channelKeys.TryGetValue(type, out var keys) ? keys : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static string ChannelName(string channelId, string key) => $"channel.{channelId}.{key}";

    /// <summary>Every secret the settings hold a value for, by name.</summary>
    public static Dictionary<string, string> Collect(CompressarrConfig config, Dictionary<string, HashSet<string>> channelKeys)
    {
        var all = new Dictionary<string, string>();
        void Add(string name, string? value) { if (!string.IsNullOrEmpty(value)) all[name] = value; }

        Add(SonarrKey, config.Arrs.Sonarr.ApiKey);
        Add(RadarrKey, config.Arrs.Radarr.ApiKey);
        Add(TmdbKey, config.Posters.TmdbApiKey);
        Add(OmdbKey, config.Posters.OmdbApiKey);
        foreach (var channel in config.Notifications.Channels)
        {
            foreach (var key in KeysFor(channelKeys, channel.Type))
            {
                if (channel.Settings.TryGetValue(key, out var value)) Add(ChannelName(channel.Id, key), value);
            }
        }

        return all;
    }

    /// <summary>Puts the stored secrets into the settings where the settings have none. A value already in the settings wins: it is newer information (a settings file written
    /// before secrets moved to the database, or an older backup, still holds its keys, and they are moved the next time the settings are saved).</summary>
    public static void Fill(CompressarrConfig config, IReadOnlyDictionary<string, string> stored, Dictionary<string, HashSet<string>> channelKeys)
    {
        string Pick(string current, string name) => string.IsNullOrEmpty(current) && stored.TryGetValue(name, out var value) ? value : current;

        config.Arrs.Sonarr.ApiKey = Pick(config.Arrs.Sonarr.ApiKey, SonarrKey);
        config.Arrs.Radarr.ApiKey = Pick(config.Arrs.Radarr.ApiKey, RadarrKey);
        config.Posters.TmdbApiKey = Pick(config.Posters.TmdbApiKey, TmdbKey);
        config.Posters.OmdbApiKey = Pick(config.Posters.OmdbApiKey, OmdbKey);
        foreach (var channel in config.Notifications.Channels)
        {
            foreach (var key in KeysFor(channelKeys, channel.Type))
            {
                var current = channel.Settings.TryGetValue(key, out var existing) ? existing : "";
                var picked = Pick(current, ChannelName(channel.Id, key));
                if (!string.IsNullOrEmpty(picked)) channel.Settings[key] = picked;
            }
        }
    }

    /// <summary>Copies the secrets <paramref name="source"/> holds a value for into <paramref name="target"/> (a channel is matched by its id): the secrets of a settings file
    /// that came from an older version, put into the settings that now carry the restored database's secrets.</summary>
    public static void CopyFilled(CompressarrConfig source, CompressarrConfig target, Dictionary<string, HashSet<string>> channelKeys)
    {
        var secrets = Collect(source, channelKeys);
        if (secrets.TryGetValue(SonarrKey, out var sonarr)) target.Arrs.Sonarr.ApiKey = sonarr;
        if (secrets.TryGetValue(RadarrKey, out var radarr)) target.Arrs.Radarr.ApiKey = radarr;
        if (secrets.TryGetValue(TmdbKey, out var tmdb)) target.Posters.TmdbApiKey = tmdb;
        if (secrets.TryGetValue(OmdbKey, out var omdb)) target.Posters.OmdbApiKey = omdb;
        foreach (var channel in target.Notifications.Channels)
        {
            foreach (var key in KeysFor(channelKeys, channel.Type))
            {
                if (secrets.TryGetValue(ChannelName(channel.Id, key), out var value)) channel.Settings[key] = value;
            }
        }
    }

    /// <summary>Empties the secrets in the JSON of the settings, which is what goes to the file once they are in the database.</summary>
    public static void Strip(JsonObject root, Dictionary<string, HashSet<string>> channelKeys)
    {
        if (root["Arrs"] is JsonObject arrs)
        {
            foreach (var service in new[] { "Sonarr", "Radarr" })
            {
                if (arrs[service] is JsonObject node && node.ContainsKey("ApiKey")) node["ApiKey"] = "";
            }
        }

        if (root["Posters"] is JsonObject posters)
        {
            foreach (var key in new[] { "TmdbApiKey", "OmdbApiKey" })
            {
                if (posters.ContainsKey(key)) posters[key] = "";
            }
        }

        if (root["Notifications"]?["Channels"] is JsonArray channels)
        {
            foreach (var channel in channels.OfType<JsonObject>())
            {
                var type = channel["Type"]?.GetValue<string>() ?? "";
                if (channel["Settings"] is not JsonObject settings) continue;
                foreach (var key in KeysFor(channelKeys, type)) settings.Remove(key);
            }
        }
    }
}

/// <summary>The settings store with the secrets taken out of the file: <see cref="Save"/> puts them in the database and writes the file without them, <see cref="Load"/> puts them back
/// into the settings object, so nothing that reads a key has to know where it is kept. Only the app's own settings file is treated this way; any other path (an extracted backup, a test's
/// file) is read and written as it is. If the database cannot be written the secrets stay in the file, so nothing is ever lost to a database problem.</summary>
public sealed class SecretsConfigStore : IConfigStore
{
    private readonly JsonConfigStore _inner;
    private readonly ISecretStore _secrets;
    private readonly Func<IEnumerable<INotifier>> _notifiers;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SecretsConfigStore(JsonConfigStore inner, ISecretStore secrets, Func<IEnumerable<INotifier>> notifiers)
    {
        _inner = inner;
        _secrets = secrets;
        _notifiers = notifiers;
    }

    private static bool IsAppFile(string path) =>
        string.Equals(Path.GetFullPath(path), Path.GetFullPath(AppPaths.GetConfigFilePath()), StringComparison.OrdinalIgnoreCase);

    private Dictionary<string, HashSet<string>> Keys() => ConfigSecrets.ChannelKeys(_notifiers());

    /// <summary>The settings objects Load handed out while the database could not be read: they carry no secrets, not because there are none but because they could not be
    /// fetched. Saving one as it is would write that empty set over the stored keys, so Save looks again first.</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<CompressarrConfig, object> _loadedWithoutSecrets = new();

    public CompressarrConfig Load(string path)
    {
        var config = _inner.Load(path);
        if (!IsAppFile(path)) return config;

        if (_secrets.ReadAll() is { } stored) ConfigSecrets.Fill(config, stored, Keys());
        else _loadedWithoutSecrets.AddOrUpdate(config, string.Empty);
        return config;
    }

    public void Save(CompressarrConfig config, string path)
    {
        if (!IsAppFile(path))
        {
            _inner.Save(config, path);
            return;
        }

        var keys = Keys();
        if (_loadedWithoutSecrets.TryGetValue(config, out _))
        {
            // The database could not be read when these settings were loaded. If it can be now, the stored keys fill whatever the settings lack (a value the settings
            // hold wins, as in Load). If it still cannot, the stored set is left exactly as it is (writing the settings' set over it could erase it) and the file keeps
            // whatever secrets the settings do hold, to be moved into the database by a later save.
            if (_secrets.ReadAll() is { } stored)
            {
                ConfigSecrets.Fill(config, stored, keys);
                _loadedWithoutSecrets.Remove(config);
            }
            else
            {
                _inner.Save(config, path);
                return;
            }
        }

        if (_secrets.ReplaceAll(ConfigSecrets.Collect(config, keys))) _inner.Save(config, path, node => ConfigSecrets.Strip(node, keys));
        else _inner.Save(config, path);
    }

    public T Update<T>(string path, Func<CompressarrConfig, T> mutate)
    {
        _lock.Wait();
        try
        {
            var config = Load(path);
            var result = mutate(config);
            Save(config, path);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Moves the secrets an older settings file still holds (a first start after an upgrade) into the database and out of the file. Returns true if anything was moved.</summary>
    public bool AdoptLegacySecrets()
    {
        var path = AppPaths.GetConfigFilePath();
        if (!File.Exists(path)) return false;
        if (ConfigSecrets.Collect(_inner.Load(path), Keys()).Count == 0) return false;

        Update(path, _ => true);
        return true;
    }
}
