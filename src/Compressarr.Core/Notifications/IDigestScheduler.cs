using Compressarr.Core.Config;
using Compressarr.Core.Logging;

namespace Compressarr.Core.Notifications;

/// <summary>Drives periodic Daily/Weekly digest notifications for the app's whole lifetime -
/// mirrors IBackupScheduler's always-on, no-enable-switch posture (Compressarr.Core/Backup/
/// IBackupScheduler.cs), but on a per-channel/per-toast schedule rather than one global interval,
/// and using local wall-clock time-of-day (a Digest Time selector) rather than "N days since X".
/// Start() is called once, unconditionally, at app startup.</summary>
public interface IDigestScheduler
{
    bool IsRunning { get; }

    /// <summary>Idempotent - a second call while already running is a no-op.</summary>
    void Start();

    Task StopAsync();
}

public sealed class DigestScheduler : IDigestScheduler, IDisposable
{
    private readonly IConfigStore _configStore;
    private readonly IRunHistoryStore _historyStore;
    private readonly IPathExpander _pathExpander;
    private readonly IReadOnlyDictionary<string, INotifier> _notifiersByType;
    private readonly INotificationService _toastService;
    private readonly IRunLogger _logger;
    private readonly IAppState _appState;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _checkInterval;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public DigestScheduler(
        IConfigStore configStore,
        IRunHistoryStore historyStore,
        IPathExpander pathExpander,
        IEnumerable<INotifier> notifiers,
        INotificationService toastService,
        IRunLogger logger,
        IAppState appState)
        : this(configStore, historyStore, pathExpander, notifiers, toastService, logger, appState, TimeProvider.System, TimeSpan.FromMinutes(1))
    {
    }

    // Internal ctor lets tests drive the loop with a tiny check interval and a fake clock instead
    // of waiting real wall-clock minutes/hours for a Digest Time to arrive - same seam
    // RunLoopController/BackupScheduler already use for their own timing.
    internal DigestScheduler(
        IConfigStore configStore,
        IRunHistoryStore historyStore,
        IPathExpander pathExpander,
        IEnumerable<INotifier> notifiers,
        INotificationService toastService,
        IRunLogger logger,
        IAppState appState,
        TimeProvider timeProvider,
        TimeSpan checkInterval)
    {
        _configStore = configStore;
        _historyStore = historyStore;
        _pathExpander = pathExpander;
        _notifiersByType = notifiers.ToDictionary(n => n.Type, StringComparer.OrdinalIgnoreCase);
        _toastService = toastService;
        _logger = logger;
        _appState = appState;
        _timeProvider = timeProvider;
        _checkInterval = checkInterval;
    }

    public bool IsRunning => _cts is not null;

    public void Start()
    {
        if (_cts is not null) return;

        _cts = new CancellationTokenSource();
        _loopTask = LoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts is null) return;

        cts.Cancel();
        try { await (_loopTask ?? Task.CompletedTask); }
        catch (OperationCanceledException) { }
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await TickAsync(token);
            }
            catch
            {
                // Best-effort - a bad config value or unreachable history file shouldn't kill the
                // loop, just retry on the next check.
            }

            try { await Task.Delay(_checkInterval, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var config = _configStore.Load(AppPaths.GetConfigFilePath());
        var nowLocal = _timeProvider.GetLocalNow();
        var today = DateOnly.FromDateTime(nowLocal.DateTime);
        var nowTime = TimeOnly.FromDateTime(nowLocal.DateTime);
        var todayDow = nowLocal.DayOfWeek;

        var dueChannels = new List<(NotificationChannel channel, bool isWeekly)>();
        foreach (var channel in config.Notifications.Channels)
        {
            if (ShouldFireDaily(channel.DigestDailyEnabled, channel.DigestDailyTime, _appState.GetDigestSent(channel.Id, DigestKind.Daily), today, nowTime))
                dueChannels.Add((channel, false));
            if (ShouldFireWeekly(channel.DigestWeeklyEnabled, channel.DigestWeeklyTime, channel.DigestWeeklyDay, _appState.GetDigestSent(channel.Id, DigestKind.Weekly), today, todayDow, nowTime))
                dueChannels.Add((channel, true));
        }

        var toastDailyDue = ShouldFireDaily(config.Notifications.ToastDigestDailyEnabled, config.Notifications.ToastDigestDailyTime, _appState.GetDigestSent(AppStateTargets.Toast, DigestKind.Daily), today, nowTime);
        var toastWeeklyDue = ShouldFireWeekly(config.Notifications.ToastDigestWeeklyEnabled, config.Notifications.ToastDigestWeeklyTime, config.Notifications.ToastDigestWeeklyDay, _appState.GetDigestSent(AppStateTargets.Toast, DigestKind.Weekly), today, todayDow, nowTime);

        if (dueChannels.Count == 0 && !toastDailyDue && !toastWeeklyDue) return;

        var logFilePath = _pathExpander.Expand(config.Logging.LogFilePath);
        var history = _historyStore.GetHistory(logFilePath);
        var dailySummary = DigestSummaryBuilder.BuildDaily(history, today);
        var weeklySummary = DigestSummaryBuilder.BuildWeekly(history, today);

        foreach (var (channel, isWeekly) in dueChannels)
        {
            var summary = isWeekly ? weeklySummary : dailySummary;
            var periodLabel = isWeekly ? "Weekly Digest" : "Daily Digest";
            await SendChannelDigestAsync(channel, summary, periodLabel, MachineLabel.For(config.Notifications), ct);
        }

        // A toast with no launch target never showed up at all (including in Action Center) on a
        // real machine, confirmed live 2026-09-07 - an unpackaged Win32 app's toast apparently
        // needs to be activatable. History is the most relevant place a digest toast can open to.
        var launchPath = $"http://localhost:{config.Web.Port}/history.html";
        if (toastDailyDue) SendToastDigest(dailySummary, "Daily Digest", launchPath);
        if (toastWeeklyDue) SendToastDigest(weeklySummary, "Weekly Digest", launchPath);

        // Stamp the dedupe days in the app state (the state database), keyed by channel Id - not in the settings file, so this can never clobber a
        // concurrent Settings edit from the web UI, and the scheduler no longer rewrites the user's settings.
        foreach (var (channel, isWeekly) in dueChannels) _appState.SetDigestSent(channel.Id, isWeekly ? DigestKind.Weekly : DigestKind.Daily, today);
        if (toastDailyDue) _appState.SetDigestSent(AppStateTargets.Toast, DigestKind.Daily, today);
        if (toastWeeklyDue) _appState.SetDigestSent(AppStateTargets.Toast, DigestKind.Weekly, today);
    }

    /// <summary>Fires once per calendar day, the first tick whose local time-of-day has passed
    /// dailyTime - date-equality (not a tight time window) means an app asleep past the target
    /// time still catches up on the next tick it's awake for, exactly once.</summary>
    private static bool ShouldFireDaily(bool enabled, string dailyTime, DateOnly? lastSentDate, DateOnly today, TimeOnly nowTime)
    {
        if (!enabled) return false;
        if (lastSentDate == today) return false;
        return nowTime >= ParseTimeOrDefault(dailyTime);
    }

    /// <summary>Fires once per matching DayOfWeek, at Weekly's own independently-configured time -
    /// same date-equality catch-up reasoning as ShouldFireDaily above.</summary>
    private static bool ShouldFireWeekly(bool weeklyEnabled, string weeklyTime, DayOfWeek weeklyDay, DateOnly? lastSentDate, DateOnly today, DayOfWeek todayDow, TimeOnly nowTime)
    {
        if (!weeklyEnabled) return false;
        if (todayDow != weeklyDay) return false;
        if (lastSentDate == today) return false;
        return nowTime >= ParseTimeOrDefault(weeklyTime);
    }

    private static TimeOnly ParseTimeOrDefault(string time) =>
        TimeOnly.TryParse(time, out var parsed) ? parsed : new TimeOnly(9, 0);

    private async Task SendChannelDigestAsync(NotificationChannel channel, DigestSummary summary, string periodLabel, string? machineName, CancellationToken ct)
    {
        if (!_notifiersByType.TryGetValue(channel.Type, out var notifier))
        {
            _logger.Log($"[Digest] Channel '{channel.DisplayName}' has unknown type '{channel.Type}' - skipped.", LogSeverity.Error);
            return;
        }

        var evt = summary.ToNotificationEvent(periodLabel, machineName);

        try
        {
            var result = await notifier.SendAsync(channel.Settings, evt, ct);
            if (!result.Success)
            {
                _logger.Log($"[Digest] {periodLabel} to '{channel.DisplayName}' failed: {result.Message}", LogSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.Log($"[Digest] {periodLabel} to '{channel.DisplayName}' failed: {ex.Message}", LogSeverity.Error);
        }
    }

    private void SendToastDigest(DigestSummary summary, string periodLabel, string launchPath)
    {
        try
        {
            _toastService.NotifyDigestComplete(summary, periodLabel, launchPath);
        }
        catch (Exception ex)
        {
            _logger.Log($"[Digest] Toast {periodLabel} failed: {ex.Message}", LogSeverity.Error);
        }
    }

    public void Dispose() => _cts?.Cancel();
}
