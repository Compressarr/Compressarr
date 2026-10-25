using Compressarr.Core.Backup;
using Compressarr.Core.Config;

namespace Compressarr.Core.Tests.Backup;

file sealed class FixedConfigStore : IConfigStore
{
    public CompressarrConfig Config { get; }
    public FixedConfigStore(CompressarrConfig config) => Config = config;

    public CompressarrConfig Load(string path) => Config;
    public void Save(CompressarrConfig config, string path) { }
    public T Update<T>(string path, Func<CompressarrConfig, T> mutate) => mutate(Config);
}

file sealed class FakeBackupService : IBackupService
{
    public int CallCount;
    public Task<BackupResult> RunBackupAsync()
    {
        Interlocked.Increment(ref CallCount);
        return Task.FromResult(new BackupResult(true, "fake.zip", null));
    }

    public IReadOnlyList<BackupFileInfo> ListBackups(string? folderOverride = null) => Array.Empty<BackupFileInfo>();
    public Task<BackupResult> RestoreBackupAsync(string fileName, string? folderOverride = null) => Task.FromResult(new BackupResult(true, fileName, null));
    public string? GetBackupPath(string fileName, string? folderOverride = null) => null;
}

public class BackupSchedulerTests
{
    // Real, very short intervals rather than a fake TimeProvider - same tradeoff
    // RunLoopControllerTests makes, keeps tests fast without a hand-rolled fake clock.
    private static readonly TimeSpan TinyInterval = TimeSpan.FromMilliseconds(20);

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(5);
        }
        throw new TimeoutException("Condition was not met within the timeout.");
    }

    private static CompressarrConfig ConfigDueNow(int intervalDays = 1) => new()
    {
        Backup = new BackupSettings { IntervalDays = intervalDays, LastRunUtc = null }
    };

    private static CompressarrConfig ConfigNotDueYet() => new()
    {
        Backup = new BackupSettings { IntervalDays = 7 }
    };

    private static InMemoryAppState BackedUpJustNow()
    {
        var state = new InMemoryAppState();
        state.SetBackupLastRunUtc(DateTimeOffset.UtcNow);
        return state;
    }

    [Fact]
    public void Start_SetsIsRunningTrue()
    {
        var scheduler = new BackupScheduler(new FixedConfigStore(ConfigDueNow()), new FakeBackupService(), new InMemoryAppState(), TinyInterval);

        scheduler.Start();

        Assert.True(scheduler.IsRunning);
    }

    [Fact]
    public async Task Start_CalledTwice_DoesNotDoubleStart()
    {
        var backupService = new FakeBackupService();
        var scheduler = new BackupScheduler(new FixedConfigStore(ConfigNotDueYet()), backupService, BackedUpJustNow(), TinyInterval);

        scheduler.Start();
        scheduler.Start(); // second call should be a no-op

        await Task.Delay(TinyInterval * 3);
        Assert.True(scheduler.IsRunning);

        await scheduler.StopAsync();
    }

    [Fact]
    public async Task Loop_RunsBackup_WhenDue()
    {
        var backupService = new FakeBackupService();
        var scheduler = new BackupScheduler(new FixedConfigStore(ConfigDueNow()), backupService, new InMemoryAppState(), TinyInterval);

        scheduler.Start();
        await WaitUntil(() => backupService.CallCount >= 1, TimeSpan.FromSeconds(2));
        await scheduler.StopAsync();

        Assert.True(backupService.CallCount >= 1);
    }

    [Fact]
    public async Task Loop_DoesNotRunBackup_WhenNotYetDue()
    {
        var backupService = new FakeBackupService();
        var scheduler = new BackupScheduler(new FixedConfigStore(ConfigNotDueYet()), backupService, BackedUpJustNow(), TinyInterval);

        scheduler.Start();
        await Task.Delay(TinyInterval * 5);
        await scheduler.StopAsync();

        Assert.Equal(0, backupService.CallCount);
    }

    [Fact]
    public async Task StopAsync_SetsIsRunningFalse()
    {
        var scheduler = new BackupScheduler(new FixedConfigStore(ConfigNotDueYet()), new FakeBackupService(), BackedUpJustNow(), TinyInterval);
        scheduler.Start();

        await scheduler.StopAsync();

        Assert.False(scheduler.IsRunning);
    }
}
