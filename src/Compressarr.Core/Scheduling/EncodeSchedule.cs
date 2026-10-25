using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Orchestration;

namespace Compressarr.Core.Scheduling;

/// <summary>The schedule as it stands right now, in the terms the engine, the status endpoint and
/// the top bar need.</summary>
/// <param name="Enabled">The schedule is switched on in Settings.</param>
/// <param name="IsDaytime">Inside the daytime window.</param>
/// <param name="Priority">The priority encodes should run at right now; null when disabled.</param>
/// <param name="OnlyOffHours">The "only encode during off hours" option is on.</param>
/// <param name="IsHeld">New files must not start: off-hours-only is on, it's daytime, and the user
/// hasn't chosen "run anyway" - or an external hold (<paramref name="Hold"/>) is in force, which "run anyway" does not lift.</param>
/// <param name="OverrideActive">"Run anyway" is in force (it lapses at the next window change).</param>
/// <param name="NextChange">When the window next flips (day to night or back), local time.</param>
/// <param name="Hold">The external hold in force (Automation API), or null. Works with the schedule switched off.</param>
public sealed record ScheduleStatus(
    bool Enabled,
    bool IsDaytime,
    EncodePriority? Priority,
    bool OnlyOffHours,
    bool IsHeld,
    bool OverrideActive,
    DateTimeOffset? NextChange,
    HoldInfo? Hold = null);

/// <summary>
/// The optional day/night encoding schedule: tells the engine whether a new file may start, keeps
/// the running encode's OS priority matched to the window (changing it live at the boundary), and -
/// if chosen - suspends an in-flight encode for the daytime. Everything is inert unless
/// ScheduleSettings.Enabled, so an install that never turns it on behaves exactly as before.
/// </summary>
public interface IEncodeSchedule
{
    /// <summary>The current state, from the config as saved on disk right now.</summary>
    ScheduleStatus GetStatus();

    /// <summary>The current state for an already-loaded config (what tests and callers that hold a
    /// snapshot use).</summary>
    ScheduleStatus GetStatus(CompressarrConfig config);

    /// <summary>"Run anyway": lets encoding proceed through the rest of this daytime window. Lapses
    /// by itself at the next window change and is never saved. No-op outside the daytime window.</summary>
    void RunAnyway();

    /// <summary>When everything still to encode would finish, counting time held for the daytime
    /// window (see SchedulePolicy.ProjectCompletion).</summary>
    DateTime? ProjectCompletion(double currentMinutes, double queuedMinutes);

    /// <summary>True the first time it is called for a given hold, so the run log says "held until
    /// ..." once per daytime window instead of on every poll.</summary>
    bool TryMarkHoldNoticed(ScheduleStatus status);

    /// <summary>Starts the background loop that applies priority and suspend/resume. Idempotent.
    /// runLoop is watched so that a Stop Monitoring request releases a held encode - Stop waits for
    /// the in-flight file, which would otherwise never finish while frozen.</summary>
    void Start(IRunLoopController? runLoop = null);

    Task StopAsync();

    /// <summary>One pass of what the background loop does; public so tests drive it directly.</summary>
    void Tick();
}

public sealed class EncodeSchedule : IEncodeSchedule, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);

    private readonly IConfigStore _configStore;
    private readonly IActiveEncodeProcess _activeProcess;
    private readonly IExternalHold _hold;
    private readonly TimeProvider _time;

    private readonly object _lock = new();
    private DateTimeOffset? _overrideUntil;
    private DateTimeOffset? _lastNoticedHoldEnd;
    private DateTimeOffset? _lastNoticedExternalHold;
    private DateTimeOffset? _thawedHoldSince;   // the external hold (by its start) whose frozen encode was released by hand or by a stop request
    private bool _pausedBySchedule;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private IRunLoopController? _runLoop;

    public EncodeSchedule(IConfigStore configStore, IActiveEncodeProcess activeProcess, IExternalHold hold)
        : this(configStore, activeProcess, hold, TimeProvider.System)
    {
    }

    internal EncodeSchedule(IConfigStore configStore, IActiveEncodeProcess activeProcess, IExternalHold hold, TimeProvider time)
    {
        _configStore = configStore;
        _activeProcess = activeProcess;
        _hold = hold;
        _time = time;
    }

    public ScheduleStatus GetStatus() => GetStatus(_configStore.Load(AppPaths.GetConfigFilePath()));

    public ScheduleStatus GetStatus(CompressarrConfig config)
    {
        var settings = config.Schedule;
        var hold = _hold.Current();
        if (!settings.Enabled)
        {
            return new ScheduleStatus(false, false, null, false, hold is not null, false, null, hold);
        }

        var now = _time.GetUtcNow();
        var state = SchedulePolicy.Evaluate(settings, LocalNow(now));
        var nextChange = state.NextChangeLocal is { } local ? ToOffset(local) : (DateTimeOffset?)null;

        bool overrideActive;
        lock (_lock)
        {
            if (_overrideUntil is { } until && now >= until) _overrideUntil = null;
            overrideActive = state.IsDaytime && _overrideUntil is not null;
        }

        var held = (settings.OnlyEncodeOffHours && state.IsDaytime && !overrideActive) || hold is not null;
        var priority = state.IsDaytime ? settings.DayPriority : settings.NightPriority;
        return new ScheduleStatus(true, state.IsDaytime, priority, settings.OnlyEncodeOffHours, held, overrideActive, nextChange, hold);
    }

    public void RunAnyway()
    {
        var status = GetStatus();

        // An external hold is not lifted (that is the automation's to release), but an encode it froze is thawed for as long as that hold stands: Run anyway,
        // a manual Resume and Stop Monitoring all mean "let the file in flight finish".
        if (status.Hold is { SuspendCurrent: true } hold) lock (_lock) { _thawedHoldSince = hold.Since; }

        if (status.Enabled && status.IsDaytime && status.NextChange is not null) lock (_lock) { _overrideUntil = status.NextChange; }
        Tick(); // thaw a suspended encode right away rather than on the next 10-second tick
    }

    public DateTime? ProjectCompletion(double currentMinutes, double queuedMinutes)
    {
        var settings = _configStore.Load(AppPaths.GetConfigFilePath()).Schedule;
        DateTime? overrideLocal;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            overrideLocal = _overrideUntil is { } until && until > now ? LocalNow(until) : null;
        }

        return SchedulePolicy.ProjectCompletion(settings, LocalNow(_time.GetUtcNow()), currentMinutes, queuedMinutes, overrideLocal);
    }

    public bool TryMarkHoldNoticed(ScheduleStatus status)
    {
        if (status.Hold is { } hold)
        {
            lock (_lock)
            {
                if (_lastNoticedExternalHold == hold.Since) return false;
                _lastNoticedExternalHold = hold.Since;
                return true;
            }
        }

        if (!status.IsHeld || status.NextChange is null) return false;
        lock (_lock)
        {
            if (_lastNoticedHoldEnd == status.NextChange) return false;
            _lastNoticedHoldEnd = status.NextChange;
            return true;
        }
    }

    public void Tick()
    {
        ScheduleStatus status;
        CompressarrConfig config;
        try
        {
            config = _configStore.Load(AppPaths.GetConfigFilePath());
            status = GetStatus(config);
        }
        catch
        {
            return; // a momentarily unreadable config must not take the loop down; try again next tick
        }

        // Priority follows the window at all times - including an encode that started in the day
        // and is still running when night begins, which is the whole point of changing it live.
        _activeProcess.SetPriority(status.Priority);

        // Two reasons to freeze the file in flight: the daytime hold of the schedule (when it is set to suspend), and an external hold that asked for it.
        var scheduledHold = status.Enabled && status.OnlyOffHours && status.IsDaytime && !status.OverrideActive;
        bool shouldSuspend;
        lock (_lock)
        {
            var holdSuspends = status.Hold is { SuspendCurrent: true } held && _thawedHoldSince != held.Since;
            shouldSuspend = (scheduledHold && config.Schedule.WhenDayStarts == DayHoldBehavior.SuspendEncode) || holdSuspends;
            if (status.Hold is null) _thawedHoldSince = null;

            if (shouldSuspend)
            {
                if (_activeProcess.IsRunning && !_activeProcess.IsPaused)
                {
                    _activeProcess.Pause();
                    _pausedBySchedule = _activeProcess.IsPaused;
                }
            }
            else if (_pausedBySchedule)
            {
                // Only ever thaw what this paused - an encode the user paused by hand stays paused.
                if (_activeProcess.IsPaused) _activeProcess.Resume();
                _pausedBySchedule = false;
            }

            if (_pausedBySchedule && !_activeProcess.IsRunning) _pausedBySchedule = false;
        }
    }

    public void Start(IRunLoopController? runLoop = null)
    {
        if (_cts is not null) return;

        if (runLoop is not null)
        {
            _runLoop = runLoop;
            runLoop.StoppingChanged += OnStoppingChanged;
        }

        _cts = new CancellationTokenSource();
        _loopTask = LoopAsync(_cts.Token);
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts is null) return;

        if (_runLoop is not null) _runLoop.StoppingChanged -= OnStoppingChanged;
        cts.Cancel();
        try { await (_loopTask ?? Task.CompletedTask); }
        catch (OperationCanceledException) { }
        _cts = null;
    }

    // Stop Monitoring lets the in-flight file finish, then stops. A file frozen for the daytime
    // would never finish, leaving Stop waiting until night - so asking to stop releases the hold
    // for the rest of this window (nothing new starts after a stop anyway).
    private void OnStoppingChanged(bool stopping)
    {
        if (stopping) RunAnyway();
    }

    private async Task LoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TickInterval, _time);
        Tick();
        try
        {
            while (await timer.WaitForNextTickAsync(token)) Tick();
        }
        catch (OperationCanceledException) { }
    }

    private DateTime LocalNow(DateTimeOffset utcNow) => TimeZoneInfo.ConvertTime(utcNow, _time.LocalTimeZone).DateTime;

    private DateTimeOffset ToOffset(DateTime local)
    {
        var zone = _time.LocalTimeZone;
        // A wall-clock time that doesn't exist (the hour skipped by "spring forward") lands an hour later.
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    public void Dispose() => _cts?.Cancel();
}
