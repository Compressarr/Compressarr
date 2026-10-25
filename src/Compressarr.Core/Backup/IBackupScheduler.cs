using Compressarr.Core.Config;

namespace Compressarr.Core.Backup;

/// <summary>Drives IBackupService on a background loop for the app's whole lifetime - deliberately
/// simpler than IRunLoopController (no trigger-now/abort/disk-full handling needed here): reload
/// config, check whether IntervalDays have passed since the last backup (IAppState.BackupLastRunUtc), run a backup if so, wait,
/// repeat. No separate enable/disable switch, matching Sonarr's own always-on backup posture -
/// Start() is called once, unconditionally, at app startup.</summary>
public interface IBackupScheduler
{
    bool IsRunning { get; }

    /// <summary>Idempotent - a second call while already running is a no-op.</summary>
    void Start();

    Task StopAsync();
}

public sealed class BackupScheduler : IBackupScheduler, IDisposable
{
    private readonly IConfigStore _configStore;
    private readonly IBackupService _backupService;
    private readonly IAppState _appState;
    private readonly TimeSpan _checkInterval;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public BackupScheduler(IConfigStore configStore, IBackupService backupService, IAppState appState)
        : this(configStore, backupService, appState, TimeSpan.FromHours(1))
    {
    }

    // Internal ctor lets tests drive the loop with a tiny check interval instead of waiting a real
    // hour between ticks - same pattern RunLoopController uses for its own TimeProvider seam.
    internal BackupScheduler(IConfigStore configStore, IBackupService backupService, IAppState appState, TimeSpan checkInterval)
    {
        _configStore = configStore;
        _backupService = backupService;
        _appState = appState;
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
                var config = _configStore.Load(AppPaths.GetConfigFilePath());
                var intervalDays = Math.Max(1, config.Backup.IntervalDays);
                var dueAt = (_appState.BackupLastRunUtc ?? DateTimeOffset.MinValue).AddDays(intervalDays);

                if (DateTimeOffset.UtcNow >= dueAt)
                {
                    await _backupService.RunBackupAsync();
                }
            }
            catch
            {
                // Best-effort - an unreachable UNC folder or similar shouldn't kill the loop, just
                // retry on the next check.
            }

            try { await Task.Delay(_checkInterval, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose() => _cts?.Cancel();
}
