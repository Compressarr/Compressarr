using System.IO.Compression;
using Compressarr.Core.Backup;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Notifications;
using Compressarr.Core.Routing;
using Compressarr.Core.Tests.Presets;
using Microsoft.Data.Sqlite;

namespace Compressarr.Core.Tests.Config;

internal sealed class SecretsFakeNotifier : INotifier
{
    public string Type => "fake";
    public string DisplayName => "Fake";
    public IReadOnlyList<NotifierField> Fields { get; } = new[]
    {
        new NotifierField("server", "Server", "text", Required: true),
        new NotifierField("token", "Token", "text", Required: true, Secret: true),
        new NotifierField("headers", "Headers", "textarea", Required: false, Secret: true)
    };

    public Task<NotifyResult> SendAsync(IReadOnlyDictionary<string, string> settings, NotificationEvent evt, CancellationToken ct) => Task.FromResult(new NotifyResult(true, "ok"));
    public Task<NotifyResult> TestAsync(IReadOnlyDictionary<string, string> settings, CancellationToken ct, string? machineName = null) => Task.FromResult(new NotifyResult(true, "ok"));
}

file sealed class BrokenSecretStore : ISecretStore
{
    public IReadOnlyDictionary<string, string>? ReadAll() => null;
    public bool ReplaceAll(IReadOnlyDictionary<string, string> secrets) => false;
}

/// <summary>A secret store whose reads fail a set number of times (a database that is busy for a moment) and are real after that.</summary>
file sealed class FlakyReadSecretStore : ISecretStore
{
    private readonly ISecretStore _inner;
    public FlakyReadSecretStore(ISecretStore inner) => _inner = inner;
    public int FailNextReads { get; set; }

    public IReadOnlyDictionary<string, string>? ReadAll()
    {
        if (FailNextReads > 0) { FailNextReads--; return null; }
        return _inner.ReadAll();
    }

    public bool ReplaceAll(IReadOnlyDictionary<string, string> secrets) => _inner.ReplaceAll(secrets);
}

file sealed class NoTrash : ITrashService
{
    public void DeleteFile(string path, DeleteAfterConvertMode mode) { }
    public void DeleteFolder(string path, DeleteAfterConvertMode mode) { }
}

file sealed class PassThroughPathExpander : IPathExpander
{
    public string Expand(string value) => value;
    public bool PathExists(string value) => Directory.Exists(value) || File.Exists(value);
}

/// <summary>Stage 3c: the keys, tokens and webhook addresses live in the state database, not in the settings file, and every reader still finds them on the settings object.</summary>
public class SecretsTests : AppDataTestBase
{
    private static readonly SecretsFakeNotifier[] Notifiers = { new() };

    private SecretsConfigStore NewStore(ISecretStore? secrets = null) =>
        new(new JsonConfigStore(), secrets ?? new SqliteSecretStore(new SqliteResumeStateStore()), () => Notifiers);

    private static CompressarrConfig ConfigWithSecrets()
    {
        var config = new CompressarrConfig();
        config.Arrs.Sonarr.Url = "http://sonarr:8989";
        config.Arrs.Sonarr.ApiKey = "son-secret";
        config.Arrs.Radarr.ApiKey = "rad-secret";
        config.Posters.TmdbApiKey = "tmdb-secret";
        config.Posters.OmdbApiKey = "omdb-secret";
        config.Notifications.Channels.Add(new NotificationChannel
        {
            Id = "chan1", Type = "fake", DisplayName = "One",
            Settings = new Dictionary<string, string> { ["server"] = "https://s.example", ["token"] = "tok-secret", ["headers"] = "Authorization: Bearer abc" }
        });
        return config;
    }

    private string SettingsText() => File.ReadAllText(AppPaths.GetConfigFilePath());

    [Fact]
    public void ASavedSecret_IsInTheDatabase_NotInTheSettingsFile_AndLoadsBackIntoTheSettings()
    {
        var store = NewStore();
        store.Save(ConfigWithSecrets(), AppPaths.GetConfigFilePath());

        var text = SettingsText();
        foreach (var secret in new[] { "son-secret", "rad-secret", "tmdb-secret", "omdb-secret", "tok-secret", "Bearer abc" }) Assert.DoesNotContain(secret, text);
        Assert.Contains("http://sonarr:8989", text);                    // the rest of the settings are there
        Assert.Contains("https://s.example", text);

        var loaded = NewStore().Load(AppPaths.GetConfigFilePath());     // a new store: nothing remembered in memory
        Assert.Equal("son-secret", loaded.Arrs.Sonarr.ApiKey);
        Assert.Equal("rad-secret", loaded.Arrs.Radarr.ApiKey);
        Assert.Equal("tmdb-secret", loaded.Posters.TmdbApiKey);
        Assert.Equal("omdb-secret", loaded.Posters.OmdbApiKey);
        var settings = loaded.Notifications.Channels.Single().Settings;
        Assert.Equal("tok-secret", settings["token"]);
        Assert.Equal("Authorization: Bearer abc", settings["headers"]);
        Assert.Equal("https://s.example", settings["server"]);
    }

    [Fact]
    public void AClearedSecret_IsRemoved_AndTheSavedConfigStillHasTheOthers()
    {
        var store = NewStore();
        store.Save(ConfigWithSecrets(), AppPaths.GetConfigFilePath());

        store.Update(AppPaths.GetConfigFilePath(), c => { c.Arrs.Sonarr.ApiKey = ""; c.Notifications.Channels[0].Settings.Remove("token"); return true; });

        var loaded = NewStore().Load(AppPaths.GetConfigFilePath());
        Assert.Equal("", loaded.Arrs.Sonarr.ApiKey);
        Assert.Equal("rad-secret", loaded.Arrs.Radarr.ApiKey);
        Assert.False(loaded.Notifications.Channels[0].Settings.ContainsKey("token"));
        Assert.Equal("Authorization: Bearer abc", loaded.Notifications.Channels[0].Settings["headers"]);
    }

    [Fact]
    public void ARemovedChannel_TakesItsSecretsWithIt()
    {
        var store = NewStore();
        store.Save(ConfigWithSecrets(), AppPaths.GetConfigFilePath());
        store.Update(AppPaths.GetConfigFilePath(), c => { c.Notifications.Channels.Clear(); return true; });

        var secrets = new SqliteSecretStore(new SqliteResumeStateStore()).ReadAll()!;

        Assert.DoesNotContain(secrets.Keys, name => name.StartsWith("channel.", StringComparison.Ordinal));
        Assert.Equal("son-secret", secrets["sonarr.apiKey"]);
    }

    [Fact]
    public void ASettingsFileFromBeforeTheMove_KeepsItsKeys_AndIsMovedOnTheFirstSave()
    {
        // The file as an older version wrote it: every key in clear.
        new JsonConfigStore().Save(ConfigWithSecrets(), AppPaths.GetConfigFilePath());
        Assert.Contains("son-secret", SettingsText());

        var store = NewStore();
        Assert.Equal("son-secret", store.Load(AppPaths.GetConfigFilePath()).Arrs.Sonarr.ApiKey);   // readable before anything is moved

        Assert.True(store.AdoptLegacySecrets());
        Assert.DoesNotContain("son-secret", SettingsText());
        Assert.DoesNotContain("tok-secret", SettingsText());
        var loaded = NewStore().Load(AppPaths.GetConfigFilePath());
        Assert.Equal("son-secret", loaded.Arrs.Sonarr.ApiKey);
        Assert.Equal("tok-secret", loaded.Notifications.Channels.Single().Settings["token"]);
        Assert.False(store.AdoptLegacySecrets());                                                   // nothing left to move
    }

    [Fact]
    public void AValueStillInTheFile_IsNewerThanTheDatabase()
    {
        var store = NewStore();
        store.Save(ConfigWithSecrets(), AppPaths.GetConfigFilePath());   // the database holds son-secret
        var fromElsewhere = new CompressarrConfig();
        fromElsewhere.Arrs.Sonarr.ApiKey = "newer-key";                  // e.g. an older settings file that arrived by hand
        new JsonConfigStore().Save(fromElsewhere, AppPaths.GetConfigFilePath());

        var loaded = NewStore().Load(AppPaths.GetConfigFilePath());

        Assert.Equal("newer-key", loaded.Arrs.Sonarr.ApiKey);
        Assert.Equal("rad-secret", loaded.Arrs.Radarr.ApiKey);           // what the file did not have still comes from the database
    }

    [Fact]
    public void WhenTheDatabaseCannotBeWritten_TheSecretsStayInTheFile()
    {
        var store = NewStore(new BrokenSecretStore());
        store.Save(ConfigWithSecrets(), AppPaths.GetConfigFilePath());

        Assert.Contains("son-secret", SettingsText());
        Assert.Contains("tok-secret", SettingsText());
        Assert.Equal("son-secret", store.Load(AppPaths.GetConfigFilePath()).Arrs.Sonarr.ApiKey);
    }

    [Fact]
    public void AnUpdate_WhoseLoadCouldNotReadTheDatabase_DoesNotEraseTheStoredSecrets()
    {
        // The database is busy for a moment as the settings are loaded and free again by the time they are saved. The loaded settings carry no keys (they could not be
        // fetched, not "there are none"); saving them as they are used to write that empty set over every stored key and webhook address.
        var real = NewStore();
        real.Save(ConfigWithSecrets(), AppPaths.GetConfigFilePath());

        var flaky = new FlakyReadSecretStore(new SqliteSecretStore(new SqliteResumeStateStore())) { FailNextReads = 1 };
        NewStore(flaky).Update(AppPaths.GetConfigFilePath(), c => { c.Web.Port = 4321; return true; });

        var loaded = real.Load(AppPaths.GetConfigFilePath());
        Assert.Equal(4321, loaded.Web.Port);
        Assert.Equal("son-secret", loaded.Arrs.Sonarr.ApiKey);
        Assert.Equal("rad-secret", loaded.Arrs.Radarr.ApiKey);
        Assert.Equal("tok-secret", loaded.Notifications.Channels.Single().Settings["token"]);
        Assert.DoesNotContain("son-secret", SettingsText());
    }

    [Fact]
    public void AnUpdate_WhenTheDatabaseStaysUnreadable_LeavesTheStoredSecretsAlone()
    {
        var real = NewStore();
        real.Save(ConfigWithSecrets(), AppPaths.GetConfigFilePath());

        var flaky = new FlakyReadSecretStore(new SqliteSecretStore(new SqliteResumeStateStore())) { FailNextReads = 2 };   // the load and the look before the save
        NewStore(flaky).Update(AppPaths.GetConfigFilePath(), c => { c.Web.Port = 4321; return true; });

        var loaded = real.Load(AppPaths.GetConfigFilePath());
        Assert.Equal(4321, loaded.Web.Port);
        Assert.Equal("son-secret", loaded.Arrs.Sonarr.ApiKey);
        Assert.Equal("tok-secret", loaded.Notifications.Channels.Single().Settings["token"]);
    }

    [Fact]
    public void AnyOtherFile_IsReadAndWrittenAsItIs()
    {
        var store = NewStore();
        var other = Path.Combine(AppData, "elsewhere.json");

        store.Save(ConfigWithSecrets(), other);

        Assert.Contains("son-secret", File.ReadAllText(other));
        Assert.Equal("son-secret", store.Load(other).Arrs.Sonarr.ApiKey);
        Assert.Null(new SqliteSecretStore(new SqliteResumeStateStore()).ReadAll()?.GetValueOrDefault("sonarr.apiKey"));
    }

    [Fact]
    public void SavingTheSameSecretsAgain_WritesNothingToTheDatabase()
    {
        var store = NewStore();
        store.Save(ConfigWithSecrets(), AppPaths.GetConfigFilePath());
        var updatedAt = ReadUpdatedAt("sonarr.apiKey");
        Thread.Sleep(1100);                                                // the stamp has one-second resolution

        store.Update(AppPaths.GetConfigFilePath(), c => { c.Processing.Limit = 7; return true; });   // an ordinary settings save

        Assert.Equal(updatedAt, ReadUpdatedAt("sonarr.apiKey"));
        Assert.Equal(7, NewStore().Load(AppPaths.GetConfigFilePath()).Processing.Limit);
    }

    private string ReadUpdatedAt(string name)
    {
        using var connection = new SqliteConnection($"Data Source={AppPaths.GetStateDbPath()};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT updated_at FROM secrets WHERE name = @name;";
        command.Parameters.AddWithValue("@name", name);
        return (string)command.ExecuteScalar()!;
    }

    // ---- backups ----------------------------------------------------------------------------------------------------------------

    private (BackupService Service, SecretsConfigStore Store, string Folder) BuildBackup()
    {
        var store = NewStore();
        var config = new CompressarrConfig();
        config.Backup.FolderPath = Path.Combine(AppData, "Backups");
        config.Logging.LogFilePath = Path.Combine(AppData, "Logs");
        store.Save(config, AppPaths.GetConfigFilePath());
        return (new BackupService(store, new PassThroughPathExpander(), new NoTrash(), new InMemoryAppState(), Notifiers), store, config.Backup.FolderPath);
    }

    [Fact]
    public async Task ABackup_CarriesTheSecrets_InTheDatabase_AndARestoreBringsThemBack()
    {
        var (service, store, folder) = BuildBackup();
        store.Update(AppPaths.GetConfigFilePath(), c =>
        {
            c.Backup.FolderPath = folder;
            c.Arrs.Sonarr.ApiKey = "son-secret";
            c.Notifications.Channels.Add(new NotificationChannel { Id = "chan1", Type = "fake", Settings = new Dictionary<string, string> { ["server"] = "s", ["token"] = "tok-secret" } });
            return true;
        });

        var backup = await service.RunBackupAsync();
        using (var zip = ZipFile.OpenRead(Path.Combine(folder, backup.FileName!)))
        {
            var settingsEntry = zip.Entries.Single(e => e.Name == "compressarr.settings.json");
            using var reader = new StreamReader(settingsEntry.Open());
            var text = reader.ReadToEnd();
            Assert.DoesNotContain("son-secret", text);                    // the settings file in the zip has none
            Assert.Contains(zip.Entries, e => e.Name == "compressarr.state.db");   // they travel in the database
        }

        store.Update(AppPaths.GetConfigFilePath(), c => { c.Backup.FolderPath = folder; c.Arrs.Sonarr.ApiKey = "changed"; c.Notifications.Channels.Clear(); return true; });
        var restore = await service.RestoreBackupAsync(backup.FileName!, folder);

        Assert.True(restore.Success, restore.Error);
        var loaded = NewStore().Load(AppPaths.GetConfigFilePath());
        Assert.Equal("son-secret", loaded.Arrs.Sonarr.ApiKey);
        Assert.Equal("tok-secret", loaded.Notifications.Channels.Single().Settings["token"]);
    }

    [Fact]
    public async Task ABackupFromBeforeTheMove_WithKeysInItsSettingsFile_RestoresThemIntoTheDatabase()
    {
        var (service, store, folder) = BuildBackup();
        Directory.CreateDirectory(folder);
        // An older backup: the settings file holds every key in clear, and the database (if any) has no secrets table content.
        var oldSettings = Path.Combine(AppData, "old-settings.json");
        new JsonConfigStore().Save(ConfigWithSecrets(), oldSettings);
        var oldDatabase = Path.Combine(AppData, "old-state");
        Directory.CreateDirectory(oldDatabase);
        var oldResume = Path.Combine(oldDatabase, "compressarr.resume.json");
        new SqliteResumeStateStore().Save(new List<ResumeEntry>(), oldResume);
        var zipPath = Path.Combine(folder, "Compressarr_Backup_old.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(oldSettings, "compressarr.settings.json");
            zip.CreateEntryFromFile(StateDatabase.PathFor(oldResume), "compressarr.state.db");
        }

        var restore = await service.RestoreBackupAsync("Compressarr_Backup_old.zip", folder);

        Assert.True(restore.Success, restore.Error);
        Assert.DoesNotContain("son-secret", SettingsText());
        var loaded = NewStore().Load(AppPaths.GetConfigFilePath());
        Assert.Equal("son-secret", loaded.Arrs.Sonarr.ApiKey);
        Assert.Equal("omdb-secret", loaded.Posters.OmdbApiKey);
        Assert.Equal("tok-secret", loaded.Notifications.Channels.Single().Settings["token"]);
        _ = store;
    }
}
