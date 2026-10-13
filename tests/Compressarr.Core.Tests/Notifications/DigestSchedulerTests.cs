using Compressarr.Core.Config;
using Compressarr.Core.Logging;
using Compressarr.Core.Notifications;

namespace Compressarr.Core.Tests.Notifications;

file sealed class FixedConfigStore : IConfigStore
{
    public CompressarrConfig Config { get; }
    public FixedConfigStore(CompressarrConfig config) => Config = config;

    public CompressarrConfig Load(string path) => Config;
    public void Save(CompressarrConfig config, string path) { }
    public T Update<T>(string path, Func<CompressarrConfig, T> mutate) => mutate(Config);
}

file sealed class FakeHistoryStore : IRunHistoryStore
{
    public IReadOnlyList<RunHistoryRecord> History { get; set; } = Array.Empty<RunHistoryRecord>();
    public void AppendRun(string logFilePath, RunHistoryRecord record) { }
    public IReadOnlyList<RunHistoryRecord> GetHistory(string logFilePath) => History;
    public int GetRunCount(string runCountPath) => 0;
    public void IncrementRunCount(string runCountPath) { }
}

file sealed class IdentityPathExpander : IPathExpander
{
    public string Expand(string value) => value;
    public bool PathExists(string value) => true;
}

file sealed class FakeNotifier : INotifier
{
    public string Type { get; init; } = "fake";
    public string DisplayName => "Fake";
    public IReadOnlyList<NotifierField> Fields { get; } = Array.Empty<NotifierField>();
    public List<NotificationEvent> Sent { get; } = new();
    public bool ThrowOnSend { get; init; }

    public Task<NotifyResult> SendAsync(IReadOnlyDictionary<string, string> settings, NotificationEvent evt, CancellationToken ct)
    {
        if (ThrowOnSend) throw new InvalidOperationException("simulated notifier failure");
        Sent.Add(evt);
        return Task.FromResult(new NotifyResult(true, "OK"));
    }

    public Task<NotifyResult> TestAsync(IReadOnlyDictionary<string, string> settings, CancellationToken ct, string? machineName = null) =>
        Task.FromResult(new NotifyResult(true, "OK"));
}

file sealed class FakeNotificationService : INotificationService
{
    public List<(DigestSummary Summary, string PeriodLabel)> DigestCalls { get; } = new();
    public void NotifyRunComplete(RunCompletionSummary summary, string? launchPath) { }
    public void NotifyDigestComplete(DigestSummary summary, string periodLabel, string? launchPath) => DigestCalls.Add((summary, periodLabel));
}

file sealed class NoOpRunLogger : IRunLogger
{
    public event Action<string, LogSeverity>? LineWritten;
    public bool HasLoggedError => false;
    public string Initialize(string logFilePath, string timestamp) => "";
    public void Log(string message, LogSeverity severity = LogSeverity.Info) { }
    public void LogProblem(string key, string message) { }
    public void ClearProblem(string key) { }
    public bool HasLaneProblemsChanged(string laneId, IReadOnlyCollection<string> problemCodes) => true;
    public void FileStart(string laneDisplayName, int index, int total, string fileName, double sizeGb, string contentType, string preset) { }
    public void FileComplete(string fileName, double beginSizeGb, double endSizeGb, TimeSpan duration, bool success, string? detailLogFile) { }
}

// UTC LocalTimeZone so the scheduler's local-time-of-day math is deterministic regardless of the
// machine actually running the tests.
file sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;
    public FakeTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;
    public override DateTimeOffset GetUtcNow() => _utcNow;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public void Set(DateTimeOffset utcNow) => _utcNow = utcNow;
}

public class DigestSchedulerTests
{
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

    private static DigestScheduler MakeScheduler(CompressarrConfig config, TimeProvider timeProvider, IEnumerable<INotifier> notifiers, INotificationService toastService) =>
        new(new FixedConfigStore(config), new FakeHistoryStore(), new IdentityPathExpander(), notifiers, toastService, new NoOpRunLogger(), timeProvider, TinyInterval);

    private static NotificationChannel DailyChannel(string time = "09:00", DateOnly? lastSent = null) => new()
    {
        Type = "fake",
        Trigger = NotificationTrigger.Never, // isolate digest behavior from per-run Trigger
        DigestDailyEnabled = true,
        DigestDailyTime = time,
        LastDailyDigestSentDate = lastSent
    };

    [Fact]
    public async Task Daily_DoesNotFire_BeforeTargetTime()
    {
        var day = new DateOnly(2026, 9, 10);
        var time = new FakeTimeProvider(new DateTimeOffset(day.Year, day.Month, day.Day, 8, 59, 0, TimeSpan.Zero));
        var config = new CompressarrConfig { Notifications = new NotificationSettings { Channels = { DailyChannel("09:00") } } };
        var notifier = new FakeNotifier();
        var scheduler = MakeScheduler(config, time, new[] { notifier }, new FakeNotificationService());

        scheduler.Start();
        await Task.Delay(TinyInterval * 4);
        await scheduler.StopAsync();

        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task Daily_Fires_OnceTimeOfDayPassesTarget()
    {
        var day = new DateOnly(2026, 9, 10);
        var time = new FakeTimeProvider(new DateTimeOffset(day.Year, day.Month, day.Day, 9, 1, 0, TimeSpan.Zero));
        var config = new CompressarrConfig { Notifications = new NotificationSettings { Channels = { DailyChannel("09:00") } } };
        var notifier = new FakeNotifier();
        var scheduler = MakeScheduler(config, time, new[] { notifier }, new FakeNotificationService());

        scheduler.Start();
        await WaitUntil(() => notifier.Sent.Count == 1, TimeSpan.FromSeconds(2));
        await scheduler.StopAsync();

        Assert.Equal("Compressarr Daily Digest", notifier.Sent[0].Title);
        Assert.Equal(day, config.Notifications.Channels[0].LastDailyDigestSentDate);
    }

    [Fact]
    public async Task Daily_DoesNotDoubleFire_SameCalendarDay()
    {
        var day = new DateOnly(2026, 9, 10);
        var time = new FakeTimeProvider(new DateTimeOffset(day.Year, day.Month, day.Day, 9, 1, 0, TimeSpan.Zero));
        var config = new CompressarrConfig { Notifications = new NotificationSettings { Channels = { DailyChannel("09:00") } } };
        var notifier = new FakeNotifier();
        var scheduler = MakeScheduler(config, time, new[] { notifier }, new FakeNotificationService());

        scheduler.Start();
        await WaitUntil(() => notifier.Sent.Count == 1, TimeSpan.FromSeconds(2));
        // Several more ticks pass at the same simulated moment - must still be exactly 1.
        await Task.Delay(TinyInterval * 5);
        await scheduler.StopAsync();

        Assert.Single(notifier.Sent);
    }

    [Fact]
    public async Task Weekly_OnlyFires_OnMatchingDayOfWeek()
    {
        var thursday = new DateOnly(2026, 9, 10); // Thursday
        Assert.Equal(DayOfWeek.Thursday, thursday.DayOfWeek);
        var time = new FakeTimeProvider(new DateTimeOffset(thursday.Year, thursday.Month, thursday.Day, 9, 1, 0, TimeSpan.Zero));
        var channel = new NotificationChannel
        {
            Type = "fake",
            Trigger = NotificationTrigger.Never,
            DigestWeeklyEnabled = true,
            DigestWeeklyDay = DayOfWeek.Monday, // not today
            DigestWeeklyTime = "09:00"
        };
        var config = new CompressarrConfig { Notifications = new NotificationSettings { Channels = { channel } } };
        var notifier = new FakeNotifier();
        var scheduler = MakeScheduler(config, time, new[] { notifier }, new FakeNotificationService());

        scheduler.Start();
        await Task.Delay(TinyInterval * 4);
        await scheduler.StopAsync();

        Assert.Empty(notifier.Sent);
    }

    [Fact]
    public async Task Weekly_UsesItsOwnTime_IndependentOfDaily()
    {
        // Daily is enabled with an EARLIER time than Weekly - if Weekly still inherited Daily's
        // time (the old design), it would fire right alongside Daily at 09:01. It shouldn't: this
        // proves Weekly's own DigestWeeklyTime is what actually governs it now.
        var thursday = new DateOnly(2026, 9, 10);
        var time = new FakeTimeProvider(new DateTimeOffset(thursday.Year, thursday.Month, thursday.Day, 9, 1, 0, TimeSpan.Zero));
        var channel = new NotificationChannel
        {
            Type = "fake",
            Trigger = NotificationTrigger.Never,
            DigestDailyEnabled = true,
            DigestDailyTime = "09:00",
            DigestWeeklyEnabled = true,
            DigestWeeklyDay = thursday.DayOfWeek,
            DigestWeeklyTime = "14:00"
        };
        var config = new CompressarrConfig { Notifications = new NotificationSettings { Channels = { channel } } };
        var notifier = new FakeNotifier();
        var scheduler = MakeScheduler(config, time, new[] { notifier }, new FakeNotificationService());

        scheduler.Start();
        await WaitUntil(() => notifier.Sent.Count == 1, TimeSpan.FromSeconds(2));
        // Only Daily should be due at 09:01 - Weekly's own 14:00 hasn't arrived yet.
        Assert.Equal("Compressarr Daily Digest", notifier.Sent[0].Title);

        time.Set(new DateTimeOffset(thursday.Year, thursday.Month, thursday.Day, 14, 1, 0, TimeSpan.Zero));
        await WaitUntil(() => notifier.Sent.Any(e => e.Title == "Compressarr Weekly Digest"), TimeSpan.FromSeconds(2));
        await scheduler.StopAsync();
    }

    [Fact]
    public async Task ZeroFileWindow_StillSends()
    {
        var day = new DateOnly(2026, 9, 10);
        var time = new FakeTimeProvider(new DateTimeOffset(day.Year, day.Month, day.Day, 9, 1, 0, TimeSpan.Zero));
        var config = new CompressarrConfig { Notifications = new NotificationSettings { Channels = { DailyChannel("09:00") } } };
        var notifier = new FakeNotifier();
        var scheduler = MakeScheduler(config, time, new[] { notifier }, new FakeNotificationService()); // FakeHistoryStore defaults to empty

        scheduler.Start();
        await WaitUntil(() => notifier.Sent.Count == 1, TimeSpan.FromSeconds(2));
        await scheduler.StopAsync();

        Assert.Equal(0, notifier.Sent[0].TotalFiles);
        Assert.Contains("Compressed 0 files", notifier.Sent[0].Body);
    }

    [Fact]
    public async Task ToastDigest_Fires_ThroughNotificationService()
    {
        var day = new DateOnly(2026, 9, 10);
        var time = new FakeTimeProvider(new DateTimeOffset(day.Year, day.Month, day.Day, 9, 1, 0, TimeSpan.Zero));
        var config = new CompressarrConfig
        {
            Notifications = new NotificationSettings
            {
                ToastDigestDailyEnabled = true,
                ToastDigestDailyTime = "09:00"
            }
        };
        var toastService = new FakeNotificationService();
        var scheduler = MakeScheduler(config, time, new[] { new FakeNotifier() }, toastService);

        scheduler.Start();
        await WaitUntil(() => toastService.DigestCalls.Count == 1, TimeSpan.FromSeconds(2));
        await scheduler.StopAsync();

        Assert.Equal("Daily Digest", toastService.DigestCalls[0].PeriodLabel);
        Assert.Equal(day, config.Notifications.ToastLastDailyDigestSentDate);
    }

    [Fact]
    public async Task OneChannelThrows_OtherChannelStillFires()
    {
        var day = new DateOnly(2026, 9, 10);
        var time = new FakeTimeProvider(new DateTimeOffset(day.Year, day.Month, day.Day, 9, 1, 0, TimeSpan.Zero));
        var throwingChannel = new NotificationChannel
        {
            Type = "throwing", Trigger = NotificationTrigger.Never,
            DigestDailyEnabled = true, DigestDailyTime = "09:00"
        };
        var healthyChannel = new NotificationChannel
        {
            Type = "healthy", Trigger = NotificationTrigger.Never,
            DigestDailyEnabled = true, DigestDailyTime = "09:00"
        };
        var config = new CompressarrConfig { Notifications = new NotificationSettings { Channels = { throwingChannel, healthyChannel } } };

        var throwing = new FakeNotifier { Type = "throwing", ThrowOnSend = true };
        var healthy = new FakeNotifier { Type = "healthy" };
        var toastService = new FakeNotificationService();
        var scheduler = new DigestScheduler(
            new FixedConfigStore(config),
            new FakeHistoryStore(),
            new IdentityPathExpander(),
            new INotifier[] { throwing, healthy },
            toastService,
            new NoOpRunLogger(),
            time,
            TinyInterval);

        scheduler.Start();
        await WaitUntil(() => healthy.Sent.Count == 1, TimeSpan.FromSeconds(2));
        await scheduler.StopAsync();

        Assert.Empty(throwing.Sent);
        Assert.Single(healthy.Sent);
    }
}
