using Compressarr.Core.Config;
using Compressarr.Core.Scheduling;

namespace Compressarr.Core.Tests.Scheduling;

/// <summary>An external hold (the Automation API holding the queue) inside the schedule: it holds with or without the Scheduler page on, "run anyway" does not lift
/// it, it can freeze the file in flight, and that file is thawed by a stop request, a manual resume or the release.</summary>
public class EncodeScheduleHoldTests
{
    private readonly ScheduleTestConfigStore _store = new();
    private readonly ScheduleTestClock _clock = new(new DateTime(2026, 10, 5, 12, 0, 0)); // Monday noon
    private readonly ScheduleTestActiveProcess _process = new();
    private readonly InMemoryExternalHold _hold;
    private readonly EncodeSchedule _schedule;

    public EncodeScheduleHoldTests()
    {
        _hold = new InMemoryExternalHold(_clock);
        _schedule = new EncodeSchedule(_store, _process, _hold, _clock);
    }

    [Fact]
    public void WithTheScheduleOff_AHoldStillHolds_AndReleasingFreesIt()
    {
        _store.Config.Schedule.Enabled = false;

        Assert.False(_schedule.GetStatus().IsHeld);
        _hold.Set("TV on", null, false, "Home Assistant");
        var held = _schedule.GetStatus();

        Assert.True(held.IsHeld);
        Assert.Equal("TV on", held.Hold!.Reason);
        Assert.False(held.Enabled);
        _hold.Release();
        Assert.False(_schedule.GetStatus().IsHeld);
        Assert.Null(_schedule.GetStatus().Hold);
    }

    [Fact]
    public void WithTheScheduleOn_NoHoldMeansTheScheduleDecides_AsBefore()
    {
        _store.Config.Schedule.Enabled = true;
        _store.Config.Schedule.OnlyEncodeOffHours = true;

        var daytime = _schedule.GetStatus();

        Assert.True(daytime.IsHeld);   // off-hours only, and it is noon
        Assert.Null(daytime.Hold);
    }

    [Fact]
    public void RunAnyway_LiftsTheSchedulesHold_ButNotAnExternalOne()
    {
        _store.Config.Schedule.Enabled = true;
        _store.Config.Schedule.OnlyEncodeOffHours = true;
        _hold.Set("TV on", null, false, "k");

        _schedule.RunAnyway();
        var status = _schedule.GetStatus();

        Assert.True(status.OverrideActive);   // the schedule's own hold is lifted
        Assert.True(status.IsHeld);           // the automation's is not
        _hold.Release();
        Assert.False(_schedule.GetStatus().IsHeld);
    }

    [Fact]
    public void AHoldThatLapses_StopsHolding()
    {
        _store.Config.Schedule.Enabled = false;
        _hold.Set("movie night", new DateTimeOffset(2026, 10, 5, 13, 0, 0, TimeSpan.Zero), false, "k");
        Assert.True(_schedule.GetStatus().IsHeld);

        _clock.Set(new DateTime(2026, 10, 5, 13, 0, 0));

        Assert.False(_schedule.GetStatus().IsHeld);
    }

    [Fact]
    public void AHoldThatDoesNotSuspend_LetsTheFileInFlightFinish()
    {
        _process.IsRunning = true;
        _hold.Set("TV on", null, suspendCurrent: false, "k");

        _schedule.Tick();

        Assert.False(_process.IsPaused);
        Assert.Equal(0, _process.PauseCalls);
    }

    [Fact]
    public void AHoldThatSuspends_FreezesTheFileInFlight_AndReleasingThawsIt()
    {
        _process.IsRunning = true;
        _hold.Set("TV on", null, suspendCurrent: true, "k");

        _schedule.Tick();
        Assert.True(_process.IsPaused);

        _hold.Release();
        _schedule.Tick();

        Assert.False(_process.IsPaused);
        Assert.Equal(1, _process.ResumeCalls);
    }

    [Fact]
    public void AnEncodeTheUserPausedByHand_StaysPausedWhenTheHoldIsReleased()
    {
        _process.IsRunning = true;
        _process.UserPause();
        _hold.Set("TV on", null, suspendCurrent: true, "k");
        _schedule.Tick();

        _hold.Release();
        _schedule.Tick();

        Assert.True(_process.IsPaused);   // it was the user's pause, not the hold's
    }

    [Fact]
    public void RunAnyway_ThawsTheFrozenFile_AndItStaysThawedWhileThatHoldStands()
    {
        _process.IsRunning = true;
        _hold.Set("TV on", null, suspendCurrent: true, "k");
        _schedule.Tick();
        Assert.True(_process.IsPaused);

        _schedule.RunAnyway();   // what a manual Resume and a Stop Monitoring request both do
        _schedule.Tick();
        _schedule.Tick();

        Assert.False(_process.IsPaused);
        Assert.True(_schedule.GetStatus().IsHeld);   // still held: nothing new starts
    }

    [Fact]
    public void ANewHold_FreezesAgain_AfterTheOldOneWasThawed()
    {
        _process.IsRunning = true;
        _hold.Set("first", null, suspendCurrent: true, "k");
        _schedule.Tick();
        _schedule.RunAnyway();
        _hold.Release();
        _schedule.Tick();
        Assert.False(_process.IsPaused);

        _clock.Set(new DateTime(2026, 10, 5, 12, 30, 0));   // a different moment, so a different hold
        _hold.Set("second", null, suspendCurrent: true, "k");
        _schedule.Tick();

        Assert.True(_process.IsPaused);
    }

    [Fact]
    public void TheHoldIsNoticedOnce_NotOnEveryCheck()
    {
        _hold.Set("TV on", null, false, "k");
        var status = _schedule.GetStatus();

        Assert.True(_schedule.TryMarkHoldNoticed(status));
        Assert.False(_schedule.TryMarkHoldNoticed(_schedule.GetStatus()));

        _hold.Release();
        _clock.Set(new DateTime(2026, 10, 5, 12, 5, 0));
        _hold.Set("again", null, false, "k");
        Assert.True(_schedule.TryMarkHoldNoticed(_schedule.GetStatus()));   // a new hold is worth saying
    }

    [Fact]
    public void PriorityStillFollowsTheWindow_WhileHeld()
    {
        _store.Config.Schedule.Enabled = true;
        _hold.Set("TV on", null, false, "k");
        _process.IsRunning = true;

        _schedule.Tick();

        Assert.Equal(EncodePriority.BelowNormal, _process.Priority);   // noon: the daytime priority
    }
}
