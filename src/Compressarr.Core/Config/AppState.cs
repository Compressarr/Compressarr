using System.Globalization;
using Compressarr.Core.Conversion;
using Compressarr.Core.Logging;

namespace Compressarr.Core.Config;

public enum DigestKind
{
    Daily,
    Weekly
}

/// <summary>Values the app itself writes as it works - not choices the user made, so not settings. Through 3.0.0-rc.2 they lived inside
/// <c>compressarr.settings.json</c> and made the schedulers rewrite the user's settings file (and its backups and exports carry them). They are in
/// <c>compressarr.state.db</c> now (its <c>kv</c> table). See docs/design-api.md, stage 0.</summary>
public interface IAppState
{
    /// <summary>The highest run number that existed the last time the History page was opened: the sidebar's error/warning badges count only runs newer
    /// than this. 0 before the page has ever been visited.</summary>
    int HistoryViewedThroughRunNumber { get; }

    void SetHistoryViewedThroughRunNumber(int runNumber);

    /// <summary>When the last automatic or manual backup finished; null if there has never been one.</summary>
    DateTimeOffset? BackupLastRunUtc { get; }

    void SetBackupLastRunUtc(DateTimeOffset value);

    /// <summary>The day a digest was last sent to <paramref name="target"/> (a notification channel's id, or <see cref="ToastTarget"/> for the desktop toast),
    /// which is what stops it going out twice on one day.</summary>
    DateOnly? GetDigestSent(string target, DigestKind kind);

    void SetDigestSent(string target, DigestKind kind, DateOnly day);

    /// <summary>Moves any of the values above that are still in the settings file (written by an earlier version: a first start after an upgrade, a restored
    /// older backup, an imported older export) into the state database and takes them out of the settings file. A value found in the file is the newer
    /// information (it just arrived from elsewhere), so it replaces what the database held. Returns true if anything was moved.</summary>
    bool AdoptLegacyValues();
}

public static class AppStateTargets
{
    /// <summary>The target name for the desktop toast's digests (channels use their own ids).</summary>
    public const string Toast = "toast";
}

/// <summary>The app state in the queue's SQLite file. If that file cannot be opened (see <see cref="SqliteResumeStateStore.ImportFailure"/>) the values are
/// kept in memory for the session instead: nothing breaks, but a restart forgets them (a digest may be sent a second time that day, an automatic backup
/// is due again), and the start-up notice already says the database could not be opened.</summary>
public sealed class SqliteAppState : IAppState
{
    private const string ViewedThroughKey = "history.viewed_through";
    private const string BackupLastRunKey = "backup.last_run_utc";

    private readonly SqliteResumeStateStore _store;
    private readonly IConfigStore _configStore;
    private readonly Func<string> _resumeFilePath;
    private readonly Dictionary<string, string> _memory = new();
    private readonly object _memoryLock = new();

    public SqliteAppState(SqliteResumeStateStore store, IConfigStore configStore)
        : this(store, configStore, AppPaths.GetResumeFilePath) { }

    /// <summary>For tests that keep the state somewhere other than the app data folder.</summary>
    internal SqliteAppState(SqliteResumeStateStore store, IConfigStore configStore, Func<string> resumeFilePath)
    {
        _store = store;
        _configStore = configStore;
        _resumeFilePath = resumeFilePath;
    }

    public int HistoryViewedThroughRunNumber => int.TryParse(Read(ViewedThroughKey), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

    public void SetHistoryViewedThroughRunNumber(int runNumber) =>
        Write(ViewedThroughKey, Math.Max(0, runNumber).ToString(CultureInfo.InvariantCulture));

    public DateTimeOffset? BackupLastRunUtc =>
        DateTimeOffset.TryParse(Read(BackupLastRunKey), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when) ? when : null;

    public void SetBackupLastRunUtc(DateTimeOffset value) => Write(BackupLastRunKey, value.ToString("O", CultureInfo.InvariantCulture));

    public DateOnly? GetDigestSent(string target, DigestKind kind) =>
        DateOnly.TryParseExact(Read(DigestKey(target, kind)), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    public void SetDigestSent(string target, DigestKind kind, DateOnly day) =>
        Write(DigestKey(target, kind), day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    public bool AdoptLegacyValues()
    {
        var path = AppPaths.GetConfigFilePath();
        var config = _configStore.Load(path);
        var moved = false;

        if (config.UiState.HistoryViewedThroughRunNumber is { } viewed) { SetHistoryViewedThroughRunNumber(viewed); moved = true; }
        if (config.Backup.LastRunUtc is { } backup) { SetBackupLastRunUtc(backup); moved = true; }
        foreach (var channel in config.Notifications.Channels)
        {
            if (channel.LastDailyDigestSentDate is { } daily) { SetDigestSent(channel.Id, DigestKind.Daily, daily); moved = true; }
            if (channel.LastWeeklyDigestSentDate is { } weekly) { SetDigestSent(channel.Id, DigestKind.Weekly, weekly); moved = true; }
        }

        if (config.Notifications.ToastLastDailyDigestSentDate is { } toastDaily) { SetDigestSent(AppStateTargets.Toast, DigestKind.Daily, toastDaily); moved = true; }
        if (config.Notifications.ToastLastWeeklyDigestSentDate is { } toastWeekly) { SetDigestSent(AppStateTargets.Toast, DigestKind.Weekly, toastWeekly); moved = true; }
        if (!moved) return false;

        // Taken out of the file only after they are safely in the database (a failed write above throws before this).
        _configStore.Update(path, c =>
        {
            ClearLegacyValues(c);
            return true;
        });
        return true;
    }

    /// <summary>Empties the app-written values an older version kept in the settings file, so they are no longer written there.</summary>
    public static void ClearLegacyValues(CompressarrConfig config)
    {
        config.UiState.HistoryViewedThroughRunNumber = null;
        config.Backup.LastRunUtc = null;
        foreach (var channel in config.Notifications.Channels)
        {
            channel.LastDailyDigestSentDate = null;
            channel.LastWeeklyDigestSentDate = null;
        }

        config.Notifications.ToastLastDailyDigestSentDate = null;
        config.Notifications.ToastLastWeeklyDigestSentDate = null;
    }

    private static string DigestKey(string target, DigestKind kind) => $"digest.{target}.{(kind == DigestKind.Daily ? "daily" : "weekly")}";

    private string? Read(string key)
    {
        using var connection = _store.OpenReady(_resumeFilePath());
        if (connection is null)
        {
            lock (_memoryLock) return _memory.TryGetValue(key, out var remembered) ? remembered : null;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM kv WHERE key = @key;";
        command.Parameters.AddWithValue("@key", key);
        return command.ExecuteScalar() as string;
    }

    private void Write(string key, string value)
    {
        using var connection = _store.OpenReady(_resumeFilePath());
        if (connection is null)
        {
            lock (_memoryLock) _memory[key] = value;
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO kv (key, value, updated_at) VALUES (@key, @value, @now) ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        command.Parameters.AddWithValue("@now", HistoryDatabase.Iso(DateTime.UtcNow));
        command.ExecuteNonQuery();
    }
}
