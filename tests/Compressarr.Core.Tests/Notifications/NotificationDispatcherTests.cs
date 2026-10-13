using Compressarr.Core.Config;
using Compressarr.Core.Logging;
using Compressarr.Core.Notifications;

namespace Compressarr.Core.Tests.Notifications;

file sealed class FakeNotifier : INotifier
{
    public string Type { get; init; } = "fake";
    public string DisplayName => "Fake";
    public IReadOnlyList<NotifierField> Fields { get; } = Array.Empty<NotifierField>();
    public List<NotificationEvent> Sent { get; } = new();
    public bool ThrowOnSend { get; init; }
    public NotifyResult ResultToReturn { get; init; } = new(true, "OK");

    public Task<NotifyResult> SendAsync(IReadOnlyDictionary<string, string> settings, NotificationEvent evt, CancellationToken ct)
    {
        if (ThrowOnSend) throw new InvalidOperationException("simulated notifier failure");
        Sent.Add(evt);
        return Task.FromResult(ResultToReturn);
    }

    public Task<NotifyResult> TestAsync(IReadOnlyDictionary<string, string> settings, CancellationToken ct, string? machineName = null) =>
        Task.FromResult(ResultToReturn);
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

public class NotificationDispatcherTests
{
    private static NotificationEvent SampleEvent(NotificationOutcome outcome = NotificationOutcome.Success) =>
        new(outcome, "Title", "Body", 1, 1.0, TimeSpan.FromMinutes(1), null);

    [Fact]
    public async Task DispatchAsync_Always_FiresRegardlessOfOutcome()
    {
        var notifier = new FakeNotifier();
        var dispatcher = new NotificationDispatcher(new[] { notifier }, new NoOpRunLogger());
        var settings = new NotificationSettings
        {
            Channels = { new NotificationChannel { Type = "fake", Trigger = NotificationTrigger.Always } }
        };

        await dispatcher.DispatchAsync(settings, SampleEvent(NotificationOutcome.Success));

        Assert.Single(notifier.Sent);
    }

    [Fact]
    public async Task DispatchAsync_Never_NeverFires()
    {
        var notifier = new FakeNotifier();
        var dispatcher = new NotificationDispatcher(new[] { notifier }, new NoOpRunLogger());
        var settings = new NotificationSettings
        {
            Channels = { new NotificationChannel { Type = "fake", Trigger = NotificationTrigger.Never } }
        };

        await dispatcher.DispatchAsync(settings, SampleEvent(NotificationOutcome.Error));

        Assert.Empty(notifier.Sent);
    }

    [Theory]
    [InlineData(NotificationOutcome.Success, false)]
    [InlineData(NotificationOutcome.Warning, true)]
    [InlineData(NotificationOutcome.Error, true)]
    public async Task DispatchAsync_OnError_FiresForWarningOrErrorButNotSuccess(NotificationOutcome outcome, bool expectFired)
    {
        var notifier = new FakeNotifier();
        var dispatcher = new NotificationDispatcher(new[] { notifier }, new NoOpRunLogger());
        var settings = new NotificationSettings
        {
            Channels = { new NotificationChannel { Type = "fake", Trigger = NotificationTrigger.OnError } }
        };

        await dispatcher.DispatchAsync(settings, SampleEvent(outcome));

        Assert.Equal(expectFired, notifier.Sent.Count == 1);
    }

    [Fact]
    public async Task DispatchAsync_OneChannelThrows_OtherChannelsStillFire()
    {
        var throwing = new FakeNotifier { Type = "throwing", ThrowOnSend = true };
        var healthy = new FakeNotifier { Type = "healthy" };
        var dispatcher = new NotificationDispatcher(new INotifier[] { throwing, healthy }, new NoOpRunLogger());
        var settings = new NotificationSettings
        {
            Channels =
            {
                new NotificationChannel { Type = "throwing", Trigger = NotificationTrigger.Always },
                new NotificationChannel { Type = "healthy", Trigger = NotificationTrigger.Always }
            }
        };

        await dispatcher.DispatchAsync(settings, SampleEvent());

        Assert.Single(healthy.Sent);
    }

    [Fact]
    public async Task DispatchAsync_OneChannelFailsResult_OtherChannelsStillFire()
    {
        var failing = new FakeNotifier { Type = "failing", ResultToReturn = new NotifyResult(false, "nope") };
        var healthy = new FakeNotifier { Type = "healthy" };
        var dispatcher = new NotificationDispatcher(new INotifier[] { failing, healthy }, new NoOpRunLogger());
        var settings = new NotificationSettings
        {
            Channels =
            {
                new NotificationChannel { Type = "failing", Trigger = NotificationTrigger.Always },
                new NotificationChannel { Type = "healthy", Trigger = NotificationTrigger.Always }
            }
        };

        await dispatcher.DispatchAsync(settings, SampleEvent());

        Assert.Single(failing.Sent); // it was called - the failure is in its returned result, not an exception
        Assert.Single(healthy.Sent);
    }

    [Fact]
    public async Task DispatchAsync_UnknownChannelType_SkippedWithoutThrowing()
    {
        var dispatcher = new NotificationDispatcher(Array.Empty<INotifier>(), new NoOpRunLogger());
        var settings = new NotificationSettings
        {
            Channels = { new NotificationChannel { Type = "does-not-exist", Trigger = NotificationTrigger.Always } }
        };

        await dispatcher.DispatchAsync(settings, SampleEvent()); // must not throw
    }
}
