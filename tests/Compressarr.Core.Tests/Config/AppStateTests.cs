using System.Text.Json.Nodes;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Tests.Presets;
using Microsoft.Data.Sqlite;

namespace Compressarr.Core.Tests.Config;

/// <summary>The values the app writes as it works live in the state database, not the settings file: they round-trip, a value an older settings file still
/// carries is moved once (and taken out of the file), an import drops another machine's values, and the settings file never gets them back.</summary>
public class AppStateTests : AppDataTestBase
{
    private readonly JsonConfigStore _configStore = new();

    private SqliteAppState NewState() => new(new SqliteResumeStateStore(), _configStore);

    private string SettingsText() => File.ReadAllText(AppPaths.GetConfigFilePath());

    [Fact]
    public void EveryValue_RoundTrips_AndStartsEmpty()
    {
        var state = NewState();
        Assert.Equal(0, state.HistoryViewedThroughRunNumber);
        Assert.Null(state.BackupLastRunUtc);
        Assert.Null(state.GetDigestSent("chan1", DigestKind.Daily));

        var when = new DateTimeOffset(2026, 10, 25, 9, 30, 15, TimeSpan.FromHours(-4)).AddTicks(1234);
        state.SetHistoryViewedThroughRunNumber(412);
        state.SetBackupLastRunUtc(when);
        state.SetDigestSent("chan1", DigestKind.Daily, new DateOnly(2026, 10, 24));
        state.SetDigestSent("chan1", DigestKind.Weekly, new DateOnly(2026, 10, 19));
        state.SetDigestSent(AppStateTargets.Toast, DigestKind.Daily, new DateOnly(2026, 10, 25));

        var again = NewState();   // a different instance reads the same database
        Assert.Equal(412, again.HistoryViewedThroughRunNumber);
        Assert.Equal(when, again.BackupLastRunUtc);
        Assert.Equal(new DateOnly(2026, 10, 24), again.GetDigestSent("chan1", DigestKind.Daily));
        Assert.Equal(new DateOnly(2026, 10, 19), again.GetDigestSent("chan1", DigestKind.Weekly));
        Assert.Equal(new DateOnly(2026, 10, 25), again.GetDigestSent(AppStateTargets.Toast, DigestKind.Daily));
        Assert.Null(again.GetDigestSent(AppStateTargets.Toast, DigestKind.Weekly));
        Assert.Null(again.GetDigestSent("chan2", DigestKind.Daily));
    }

    [Fact]
    public void SettingTheValues_NeverTouchesTheSettingsFile()
    {
        _configStore.Save(DefaultConfigFactory.Create(), AppPaths.GetConfigFilePath());
        var before = SettingsText();
        var stamp = File.GetLastWriteTimeUtc(AppPaths.GetConfigFilePath());
        Thread.Sleep(30);

        var state = NewState();
        state.SetBackupLastRunUtc(DateTimeOffset.UtcNow);
        state.SetDigestSent("chan1", DigestKind.Daily, new DateOnly(2026, 10, 25));
        state.SetHistoryViewedThroughRunNumber(9);

        Assert.Equal(before, SettingsText());
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(AppPaths.GetConfigFilePath()));
    }

    [Fact]
    public void ASavedSettingsFile_HasNoneOfTheLegacyKeys()
    {
        var config = DefaultConfigFactory.Create();
        config.Notifications.Channels.Add(new NotificationChannel { Type = "webhook", DisplayName = "x" });
        _configStore.Save(config, AppPaths.GetConfigFilePath());

        var text = SettingsText();

        Assert.DoesNotContain("LastRunUtc", text);
        Assert.DoesNotContain("DigestSentDate", text);
        Assert.DoesNotContain("HistoryViewedThroughRunNumber", text);
    }

    private void WriteOlderSettingsFile(string channelId)
    {
        var config = DefaultConfigFactory.Create();
        config.Notifications.Channels.Add(new NotificationChannel { Id = channelId, Type = "webhook", DisplayName = "x" });
        _configStore.Save(config, AppPaths.GetConfigFilePath());
        // What a 3.0.0-rc.2 (or older) settings file looks like: the app-written values sit inside it.
        var node = JsonNode.Parse(SettingsText())!.AsObject();
        node["UiState"]!["HistoryViewedThroughRunNumber"] = 77;
        node["Backup"]!["LastRunUtc"] = "2026-10-20T01:02:03+00:00";
        node["Notifications"]!["ToastLastDailyDigestSentDate"] = "2026-10-21";
        node["Notifications"]!["ToastLastWeeklyDigestSentDate"] = "2026-10-19";
        var channel = node["Notifications"]!["Channels"]!.AsArray()[0]!.AsObject();
        channel["LastDailyDigestSentDate"] = "2026-10-22";
        channel["LastWeeklyDigestSentDate"] = "2026-10-20";
        File.WriteAllText(AppPaths.GetConfigFilePath(), node.ToJsonString());
    }

    [Fact]
    public void ValuesAnOlderSettingsFileStillCarries_AreMovedOnce_AndTakenOutOfTheFile()
    {
        WriteOlderSettingsFile("chan1");
        var state = NewState();

        Assert.True(state.AdoptLegacyValues());

        Assert.Equal(77, state.HistoryViewedThroughRunNumber);
        Assert.Equal(DateTimeOffset.Parse("2026-10-20T01:02:03+00:00"), state.BackupLastRunUtc);
        Assert.Equal(new DateOnly(2026, 10, 21), state.GetDigestSent(AppStateTargets.Toast, DigestKind.Daily));
        Assert.Equal(new DateOnly(2026, 10, 19), state.GetDigestSent(AppStateTargets.Toast, DigestKind.Weekly));
        Assert.Equal(new DateOnly(2026, 10, 22), state.GetDigestSent("chan1", DigestKind.Daily));
        Assert.Equal(new DateOnly(2026, 10, 20), state.GetDigestSent("chan1", DigestKind.Weekly));
        var text = SettingsText();
        Assert.DoesNotContain("LastRunUtc", text);
        Assert.DoesNotContain("DigestSentDate", text);
        Assert.DoesNotContain("HistoryViewedThroughRunNumber", text);
        Assert.False(state.AdoptLegacyValues());   // nothing left to move
    }

    [Fact]
    public void AValueFoundInTheFile_ReplacesWhatTheDatabaseHeld_BecauseItJustArrivedFromElsewhere()
    {
        var state = NewState();
        state.SetHistoryViewedThroughRunNumber(500);   // newer than the restored backup's, but the backup is what the user asked for
        WriteOlderSettingsFile("chan1");

        state.AdoptLegacyValues();

        Assert.Equal(77, state.HistoryViewedThroughRunNumber);
    }

    [Fact]
    public void NothingInTheFile_MeansNothingIsChanged()
    {
        _configStore.Save(DefaultConfigFactory.Create(), AppPaths.GetConfigFilePath());
        var state = NewState();
        state.SetHistoryViewedThroughRunNumber(5);

        Assert.False(state.AdoptLegacyValues());

        Assert.Equal(5, state.HistoryViewedThroughRunNumber);
    }

    [Fact]
    public void AnImportDropsTheValuesInTheFile_SoAnotherComputersDigestStampDoesNotSilenceThisOne()
    {
        WriteOlderSettingsFile("chan1");
        var imported = _configStore.Load(AppPaths.GetConfigFilePath());
        Assert.NotNull(imported.Backup.LastRunUtc);

        SqliteAppState.ClearLegacyValues(imported);

        Assert.Null(imported.Backup.LastRunUtc);
        Assert.Null(imported.UiState.HistoryViewedThroughRunNumber);
        Assert.Null(imported.Notifications.ToastLastDailyDigestSentDate);
        Assert.Null(imported.Notifications.Channels[0].LastWeeklyDigestSentDate);
    }

    [Fact]
    public void WhenTheDatabaseCannotBeOpened_TheValuesAreKeptInMemoryForTheSession()
    {
        Directory.CreateDirectory(AppPaths.GetStateDbPath());   // a folder where the database file should be
        var state = NewState();

        state.SetHistoryViewedThroughRunNumber(31);
        state.SetDigestSent("chan1", DigestKind.Daily, new DateOnly(2026, 10, 25));

        Assert.Equal(31, state.HistoryViewedThroughRunNumber);
        Assert.Equal(new DateOnly(2026, 10, 25), state.GetDigestSent("chan1", DigestKind.Daily));
    }

    [Fact]
    public void ADatabaseFromBeforeTheKvTable_IsMigratedInPlace_AndKeepsItsQueue()
    {
        var resumePath = AppPaths.GetResumeFilePath();
        new SqliteResumeStateStore().Save(new List<ResumeEntry> { new() { LaneId = "lane1", FullName = @"C:\In\a.mkv", Order = 3 } }, resumePath);
        // Make it look like a database written by 3.0.0-rc.2: schema version 1, no kv, holds, api_secrets, auth, web_sessions or secrets table and none of the queue's later columns (all are added by the migration; v3's api_keys is dropped again by v5).
        var oldFolder = Directory.CreateDirectory(Path.Combine(AppData, "old")).FullName;
        var oldResume = Path.Combine(oldFolder, "compressarr.resume.json");
        StateDatabase.Snapshot(AppPaths.GetStateDbPath(), StateDatabase.PathFor(oldResume));
        using (var connection = new SqliteConnection($"Data Source={StateDatabase.PathFor(oldResume)};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE kv; DROP TABLE holds; DROP TABLE api_secrets; DROP TABLE auth; DROP TABLE web_sessions; DROP TABLE secrets; ALTER TABLE queue DROP COLUMN encoded_from_size; ALTER TABLE queue DROP COLUMN encoded_from_modified_utc; PRAGMA user_version = 1;";
            command.ExecuteNonQuery();
        }

        var store = new SqliteResumeStateStore();
        var state = new SqliteAppState(store, _configStore, () => oldResume);
        state.SetBackupLastRunUtc(DateTimeOffset.UtcNow);

        Assert.NotNull(state.BackupLastRunUtc);
        Assert.Equal(3, store.Load(oldResume).Single().Order);
    }
}
