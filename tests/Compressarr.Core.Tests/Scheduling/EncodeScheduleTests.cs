using System.Diagnostics;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Scheduling;

namespace Compressarr.Core.Tests.Scheduling;

internal sealed class ScheduleTestConfigStore : IConfigStore
{
    public CompressarrConfig Config { get; } = new();
    public CompressarrConfig Load(string path) => Config;
    public void Save(CompressarrConfig config, string path) { }
    public T Update<T>(string path, Func<CompressarrConfig, T> mutate) => mutate(Config);
}

/// <summary>A clock the test moves by hand, in UTC so "local" time is the same wall clock
/// everywhere the tests run.</summary>
internal sealed class ScheduleTestClock : TimeProvider
{
    private DateTimeOffset _now;
    public ScheduleTestClock(DateTime utcWallClock) => _now = new DateTimeOffset(DateTime.SpecifyKind(utcWallClock, DateTimeKind.Utc));
    public override DateTimeOffset GetUtcNow() => _now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public void Set(DateTime utcWallClock) => _now = new DateTimeOffset(DateTime.SpecifyKind(utcWallClock, DateTimeKind.Utc));
}

internal sealed class ScheduleTestActiveProcess : IActiveEncodeProcess
{
    public bool IsRunning { get; set; }
    public bool IsPaused { get; private set; }
    public EncodePriority? Priority { get; private set; }
    public int PauseCalls { get; private set; }
    public int ResumeCalls { get; private set; }

    public void Register(Process process) { }
    public void Unregister() { }
    public void Pause() { if (IsRunning && !IsPaused) { IsPaused = true; PauseCalls++; } }
    public void Resume() { if (IsPaused) { IsPaused = false; ResumeCalls++; } }
    public void SetPriority(EncodePriority? priority) => Priority = priority;
    public void UserPause() => IsPaused = true;
}

public class SchedulePolicyTests
{
    // 2026-10-05 is a Monday; 10-09 Friday; 10-10 Saturday; 10-11 Sunday.
    private static DateTime At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0);

    private static ScheduleSettings Window(string start, string end) => new() { Enabled = true, DayStart = start, DayEnd = end };

    [Theory]
    [InlineData("08:00", true)]
    [InlineData("8:00", true)]
    [InlineData("23:59", true)]
    [InlineData("24:00", false)]
    [InlineData("8", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("noon", false)]
    public void TryParseTime_AcceptsOnlyClockTimes(string? text, bool ok) =>
        Assert.Equal(ok, SchedulePolicy.TryParseTime(text, out _));

    [Fact]
    public void Disabled_IsNeverDaytime()
    {
        var state = SchedulePolicy.Evaluate(new ScheduleSettings { Enabled = false, DayStart = "00:00", DayEnd = "23:00" }, At(5, 12));
        Assert.False(state.IsDaytime);
        Assert.Null(state.NextChangeLocal);
    }

    [Theory]
    [InlineData(7, 59, false, 5, 8, 0)]   // just before the window: night, day starts at 8
    [InlineData(8, 0, true, 5, 22, 0)]    // start is inclusive
    [InlineData(12, 0, true, 5, 22, 0)]
    [InlineData(21, 59, true, 5, 22, 0)]
    [InlineData(22, 0, false, 6, 8, 0)]   // end is exclusive; next window is tomorrow morning
    public void SimpleDaytimeWindow(int hour, int minute, bool isDay, int nextDay, int nextHour, int nextMinute)
    {
        var state = SchedulePolicy.Evaluate(Window("08:00", "22:00"), At(5, hour, minute));
        Assert.Equal(isDay, state.IsDaytime);
        Assert.Equal(At(nextDay, nextHour, nextMinute), state.NextChangeLocal);
    }

    [Fact]
    public void WindowCrossingMidnight_BelongsToTheDayItStartedOn()
    {
        var settings = Window("22:00", "06:00");

        var evening = SchedulePolicy.Evaluate(settings, At(5, 23));
        Assert.True(evening.IsDaytime);
        Assert.Equal(At(6, 6), evening.NextChangeLocal);

        var smallHours = SchedulePolicy.Evaluate(settings, At(6, 3));
        Assert.True(smallHours.IsDaytime);
        Assert.Equal(At(6, 6), smallHours.NextChangeLocal);

        var noon = SchedulePolicy.Evaluate(settings, At(6, 12));
        Assert.False(noon.IsDaytime);
        Assert.Equal(At(6, 22), noon.NextChangeLocal);
    }

    [Fact]
    public void StartEqualsEnd_MeansNoDaytimeWindow()
    {
        var state = SchedulePolicy.Evaluate(Window("08:00", "08:00"), At(5, 12));
        Assert.False(state.IsDaytime);
        Assert.Null(state.NextChangeLocal);
    }

    [Fact]
    public void UnparseableTimes_AreIgnored_SoTheScheduleStaysAtNight()
    {
        var state = SchedulePolicy.Evaluate(Window("soon", "later"), At(5, 12));
        Assert.False(state.IsDaytime);
        Assert.Null(state.NextChangeLocal);
    }

    [Fact]
    public void WeekendWindow_AppliesToSaturdayAndSunday_Only()
    {
        var settings = new ScheduleSettings { Enabled = true, DayStart = "08:00", DayEnd = "22:00", Mode = ScheduleMode.WeekdaysAndWeekends, WeekendDayStart = "10:00", WeekendDayEnd = "20:00" };

        Assert.True(SchedulePolicy.Evaluate(settings, At(9, 9)).IsDaytime);    // Friday 9:00 - weekday window
        Assert.False(SchedulePolicy.Evaluate(settings, At(10, 9)).IsDaytime);  // Saturday 9:00 - weekend window not open yet
        Assert.True(SchedulePolicy.Evaluate(settings, At(10, 10)).IsDaytime);
        Assert.False(SchedulePolicy.Evaluate(settings, At(11, 20)).IsDaytime); // Sunday 20:00 - weekend window over
        Assert.True(SchedulePolicy.Evaluate(settings, At(12, 8)).IsDaytime);   // Monday 8:00 - weekday again
    }

    [Fact]
    public void WeekendWithNoWindow_IsNightAllWeekend()
    {
        var settings = new ScheduleSettings { Enabled = true, DayStart = "08:00", DayEnd = "22:00", Mode = ScheduleMode.WeekdaysAndWeekends, WeekendDayStart = "00:00", WeekendDayEnd = "00:00" };

        var friday = SchedulePolicy.Evaluate(settings, At(9, 23));
        Assert.False(friday.IsDaytime);
        Assert.Equal(At(12, 8), friday.NextChangeLocal); // skips straight to Monday morning
    }

    [Fact]
    public void FridayWindowRunningIntoSaturdaysWindow_IsOneContinuousDaytime()
    {
        var settings = new ScheduleSettings { Enabled = true, DayStart = "22:00", DayEnd = "06:00", Mode = ScheduleMode.WeekdaysAndWeekends, WeekendDayStart = "04:00", WeekendDayEnd = "20:00" };

        var state = SchedulePolicy.Evaluate(settings, At(10, 5)); // Saturday 05:00
        Assert.True(state.IsDaytime);
        Assert.Equal(At(10, 20), state.NextChangeLocal); // not 06:00 - Saturday's own window carries on
    }

    // ---- "Each day" layout ----

    private static ScheduleSettings EachDay(params (DayOfWeek Day, string Start, string End)[] windows)
    {
        var settings = new ScheduleSettings { Enabled = true, Mode = ScheduleMode.EachDay };
        // Every day starts with no window; the test turns on only the days it names.
        foreach (var w in settings.Days) { w.Start = "00:00"; w.End = "00:00"; }
        foreach (var (day, start, end) in windows)
        {
            settings.Days[(int)day].Start = start;
            settings.Days[(int)day].End = end;
        }
        return settings;
    }

    [Fact]
    public void EachDay_UsesTheWindowOfTheDay_AndNoneForTheRest()
    {
        var settings = EachDay((DayOfWeek.Monday, "09:00", "17:00"), (DayOfWeek.Wednesday, "13:00", "15:00"));

        Assert.True(SchedulePolicy.Evaluate(settings, At(5, 10)).IsDaytime);   // Monday 10:00
        Assert.False(SchedulePolicy.Evaluate(settings, At(5, 18)).IsDaytime);  // Monday 18:00
        Assert.False(SchedulePolicy.Evaluate(settings, At(6, 10)).IsDaytime);  // Tuesday: no window
        Assert.False(SchedulePolicy.Evaluate(settings, At(7, 10)).IsDaytime);  // Wednesday 10:00 - before its window
        Assert.True(SchedulePolicy.Evaluate(settings, At(7, 14)).IsDaytime);   // Wednesday 14:00
    }

    [Fact]
    public void EachDay_NextChangeSkipsDaysWithNoWindow()
    {
        var settings = EachDay((DayOfWeek.Monday, "09:00", "17:00"), (DayOfWeek.Friday, "09:00", "17:00"));

        var mondayEvening = SchedulePolicy.Evaluate(settings, At(5, 18));
        Assert.False(mondayEvening.IsDaytime);
        Assert.Equal(At(9, 9), mondayEvening.NextChangeLocal); // straight to Friday 09:00
    }

    [Fact]
    public void EachDay_AWindowCrossingMidnightRunsIntoTheNextDay()
    {
        var settings = EachDay((DayOfWeek.Friday, "22:00", "06:00")); // Saturday has no window of its own

        var saturdayEarly = SchedulePolicy.Evaluate(settings, At(10, 3));
        Assert.True(saturdayEarly.IsDaytime);
        Assert.Equal(At(10, 6), saturdayEarly.NextChangeLocal);
        Assert.False(SchedulePolicy.Evaluate(settings, At(10, 7)).IsDaytime);
    }

    [Fact]
    public void EachDay_AMissingEntryMeansNoWindowThatDay()
    {
        var settings = EachDay((DayOfWeek.Monday, "09:00", "17:00"));
        settings.Days.RemoveRange(2, 5); // only Sunday and Monday left (a hand-edited file)

        Assert.True(SchedulePolicy.Evaluate(settings, At(5, 10)).IsDaytime);
        Assert.False(SchedulePolicy.Evaluate(settings, At(9, 10)).IsDaytime); // Friday: entry missing
    }

    [Fact]
    public void Everyday_IgnoresTheWeekendAndEachDayFields()
    {
        var settings = new ScheduleSettings { Enabled = true, Mode = ScheduleMode.Everyday, DayStart = "08:00", DayEnd = "22:00", WeekendDayStart = "00:00", WeekendDayEnd = "00:00" };
        settings.Days[6].Start = "00:00";
        settings.Days[6].End = "00:00";

        Assert.True(SchedulePolicy.Evaluate(settings, At(10, 12)).IsDaytime); // Saturday noon
    }

    // ---- ProjectCompletion ----

    [Fact]
    public void ProjectCompletion_OffHoursOnlyOff_IsJustNowPlusTheWork()
    {
        var settings = Window("08:00", "22:00");
        Assert.Equal(At(5, 15), SchedulePolicy.ProjectCompletion(settings, At(5, 12), 30, 150));

        settings.Enabled = false;
        settings.OnlyEncodeOffHours = true;
        Assert.Equal(At(5, 15), SchedulePolicy.ProjectCompletion(settings, At(5, 12), 30, 150));
    }

    [Fact]
    public void ProjectCompletion_QueuedWorkWaitsOutTheDaytime()
    {
        var settings = Window("08:00", "22:00");
        settings.OnlyEncodeOffHours = true;

        // 12:00, nothing running, 2h queued: held until 22:00, done at midnight.
        Assert.Equal(At(6, 0), SchedulePolicy.ProjectCompletion(settings, At(5, 12), 0, 120));
    }

    [Fact]
    public void ProjectCompletion_WorkLongerThanOneNight_SpansIntoTheNextNight()
    {
        var settings = Window("08:00", "22:00");
        settings.OnlyEncodeOffHours = true;

        // 22:00 start, 700 min queued: 600 min tonight (to 08:00), 100 more after the next 22:00.
        Assert.Equal(At(6, 23, 40), SchedulePolicy.ProjectCompletion(settings, At(5, 22), 0, 700));
    }

    [Fact]
    public void ProjectCompletion_FinishCurrentFile_RunsThroughTheDay_ThenQueuedWaits()
    {
        var settings = Window("08:00", "22:00");
        settings.OnlyEncodeOffHours = true;
        settings.WhenDayStarts = DayHoldBehavior.FinishCurrentFile;

        // 21:00: current file needs 30 min (runs on, done 21:30), then 2h queued waits for 22:00.
        Assert.Equal(At(6, 0), SchedulePolicy.ProjectCompletion(settings, At(5, 21), 30, 120));
    }

    [Fact]
    public void ProjectCompletion_SuspendEncode_CurrentFileAlsoWaitsForNight()
    {
        var settings = Window("08:00", "22:00");
        settings.OnlyEncodeOffHours = true;
        settings.WhenDayStarts = DayHoldBehavior.SuspendEncode;

        // 21:00: current (30 min) and queued (90 min) all wait: 22:00 + 120 min = 00:00.
        Assert.Equal(At(6, 0), SchedulePolicy.ProjectCompletion(settings, At(5, 21), 30, 90));
    }

    [Fact]
    public void ProjectCompletion_RunAnywayCountsTheOverriddenStretch()
    {
        var settings = Window("08:00", "22:00");
        settings.OnlyEncodeOffHours = true;

        // 12:00 with "run anyway" until 22:00: 2h of work simply runs now.
        Assert.Equal(At(5, 14), SchedulePolicy.ProjectCompletion(settings, At(5, 12), 0, 120, overrideUntilLocal: At(5, 22)));
    }

    [Fact]
    public void ProjectCompletion_NoDaytimeWindow_IsNowPlusTheWork()
    {
        var settings = Window("08:00", "08:00");
        settings.OnlyEncodeOffHours = true;
        Assert.Equal(At(5, 14), SchedulePolicy.ProjectCompletion(settings, At(5, 12), 0, 120));
    }
}

public class EncodeScheduleTests
{
    private readonly ScheduleTestConfigStore _store = new();
    private readonly ScheduleTestClock _clock = new(new DateTime(2026, 10, 5, 12, 0, 0)); // Monday noon
    private readonly ScheduleTestActiveProcess _process = new();
    private readonly EncodeSchedule _schedule;

    public EncodeScheduleTests()
    {
        _store.Config.Schedule.Enabled = true; // 08:00-22:00, below normal by day / normal at night
        _schedule = new EncodeSchedule(_store, _process, new InMemoryExternalHold(_clock), _clock);
    }

    private void At(int hour, int minute = 0, int day = 5) => _clock.Set(new DateTime(2026, 10, day, hour, minute, 0));

    [Fact]
    public void Disabled_ReportsNothingAndNeverHolds()
    {
        _store.Config.Schedule.Enabled = false;
        _store.Config.Schedule.OnlyEncodeOffHours = true;

        var status = _schedule.GetStatus();

        Assert.False(status.Enabled);
        Assert.False(status.IsHeld);
        Assert.Null(status.Priority);
        Assert.Null(status.NextChange);
    }

    [Fact]
    public void Daytime_UsesDayPriority_AndReportsWhenNightStarts()
    {
        var status = _schedule.GetStatus();

        Assert.True(status.IsDaytime);
        Assert.Equal(EncodePriority.BelowNormal, status.Priority);
        Assert.False(status.IsHeld); // off-hours-only is off
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 22, 0, 0, TimeSpan.Zero), status.NextChange);
    }

    [Fact]
    public void Night_UsesNightPriority()
    {
        At(23);
        var status = _schedule.GetStatus();

        Assert.False(status.IsDaytime);
        Assert.Equal(EncodePriority.Normal, status.Priority);
    }

    [Fact]
    public void OffHoursOnly_HoldsInTheDay_AndReleasesAtNight()
    {
        _store.Config.Schedule.OnlyEncodeOffHours = true;

        Assert.True(_schedule.GetStatus().IsHeld);
        At(22);
        Assert.False(_schedule.GetStatus().IsHeld);
    }

    [Fact]
    public void RunAnyway_ReleasesTheHold_UntilTheWindowEnds()
    {
        _store.Config.Schedule.OnlyEncodeOffHours = true;

        _schedule.RunAnyway();
        var status = _schedule.GetStatus();
        Assert.False(status.IsHeld);
        Assert.True(status.OverrideActive);

        At(21, 59);
        Assert.False(_schedule.GetStatus().IsHeld);

        // Night passes and tomorrow's daytime begins: the override has lapsed, the hold is back.
        At(8, 0, day: 6);
        var next = _schedule.GetStatus();
        Assert.True(next.IsHeld);
        Assert.False(next.OverrideActive);
    }

    [Fact]
    public void RunAnyway_AtNight_DoesNothing()
    {
        _store.Config.Schedule.OnlyEncodeOffHours = true;
        At(23);

        _schedule.RunAnyway();

        At(8, 0, day: 6);
        Assert.True(_schedule.GetStatus().IsHeld);
    }

    [Fact]
    public void HoldNotice_IsGivenOncePerDaytimeWindow()
    {
        _store.Config.Schedule.OnlyEncodeOffHours = true;

        Assert.True(_schedule.TryMarkHoldNoticed(_schedule.GetStatus()));
        Assert.False(_schedule.TryMarkHoldNoticed(_schedule.GetStatus()));

        At(8, 0, day: 6); // the next day's window is a new hold
        Assert.True(_schedule.TryMarkHoldNoticed(_schedule.GetStatus()));

        At(23, 0, day: 6); // not held at night: nothing to announce
        Assert.False(_schedule.TryMarkHoldNoticed(_schedule.GetStatus()));
    }

    [Fact]
    public void Tick_AppliesTheWindowsPriority_AndFollowsItAtTheBoundary()
    {
        _schedule.Tick();
        Assert.Equal(EncodePriority.BelowNormal, _process.Priority);

        At(22);
        _schedule.Tick();
        Assert.Equal(EncodePriority.Normal, _process.Priority);

        _store.Config.Schedule.NightPriority = EncodePriority.High;
        _schedule.Tick();
        Assert.Equal(EncodePriority.High, _process.Priority);
    }

    [Fact]
    public void Tick_WhenTheScheduleIsSwitchedOff_ReleasesThePriority()
    {
        _schedule.Tick();
        _store.Config.Schedule.Enabled = false;

        _schedule.Tick();

        Assert.Null(_process.Priority);
    }

    [Fact]
    public void Tick_SuspendMode_FreezesARunningEncodeInTheDay_AndThawsItAtNight()
    {
        _store.Config.Schedule.OnlyEncodeOffHours = true;
        _store.Config.Schedule.WhenDayStarts = DayHoldBehavior.SuspendEncode;
        _process.IsRunning = true;

        _schedule.Tick();
        Assert.True(_process.IsPaused);

        _schedule.Tick(); // stays frozen, no second Pause
        Assert.Equal(1, _process.PauseCalls);

        At(22);
        _schedule.Tick();
        Assert.False(_process.IsPaused);
        Assert.Equal(1, _process.ResumeCalls);
    }

    [Fact]
    public void Tick_FinishMode_NeverPausesARunningEncode()
    {
        _store.Config.Schedule.OnlyEncodeOffHours = true;
        _store.Config.Schedule.WhenDayStarts = DayHoldBehavior.FinishCurrentFile;
        _process.IsRunning = true;

        _schedule.Tick();

        Assert.False(_process.IsPaused);
    }

    [Fact]
    public void RunAnyway_ThawsAnEncodeTheScheduleFroze()
    {
        _store.Config.Schedule.OnlyEncodeOffHours = true;
        _store.Config.Schedule.WhenDayStarts = DayHoldBehavior.SuspendEncode;
        _process.IsRunning = true;
        _schedule.Tick();
        Assert.True(_process.IsPaused);

        _schedule.RunAnyway();

        Assert.False(_process.IsPaused);
    }

    [Fact]
    public void Tick_NeverThawsAnEncodeTheUserPausedByHand()
    {
        _store.Config.Schedule.OnlyEncodeOffHours = true;
        _store.Config.Schedule.WhenDayStarts = DayHoldBehavior.SuspendEncode;
        _process.IsRunning = true;
        _process.UserPause();

        _schedule.Tick();         // already paused: the schedule has nothing to do and owns nothing
        At(22);
        _schedule.Tick();

        Assert.True(_process.IsPaused);
        Assert.Equal(0, _process.ResumeCalls);
    }

    [Fact]
    public void ProjectCompletion_UsesTheSavedSettingsAndAnActiveOverride()
    {
        _store.Config.Schedule.OnlyEncodeOffHours = true;

        Assert.Equal(new DateTime(2026, 10, 6, 0, 0, 0), _schedule.ProjectCompletion(0, 120));

        _schedule.RunAnyway();
        Assert.Equal(new DateTime(2026, 10, 5, 14, 0, 0), _schedule.ProjectCompletion(0, 120));
    }
}

public class ActiveEncodeProcessPriorityTests
{
    private static Process StartSleeper() => Process.Start(new ProcessStartInfo
    {
        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe"),
        Arguments = "-n 30 127.0.0.1",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true
    })!;

    [Fact]
    public void SetPriority_ChangesARunningProcessLive_AndNullPutsItBackToNormal()
    {
        using var process = StartSleeper();
        try
        {
            var active = new ActiveEncodeProcess();
            active.Register(process);

            active.SetPriority(EncodePriority.Low);
            process.Refresh();
            Assert.Equal(ProcessPriorityClass.Idle, process.PriorityClass);

            active.SetPriority(EncodePriority.High);
            process.Refresh();
            Assert.Equal(ProcessPriorityClass.High, process.PriorityClass);

            active.SetPriority(null);
            process.Refresh();
            Assert.Equal(ProcessPriorityClass.Normal, process.PriorityClass);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void SetPriority_IsAppliedToTheNextProcessThatRegisters()
    {
        var active = new ActiveEncodeProcess();
        active.SetPriority(EncodePriority.BelowNormal); // nothing running yet - remembered

        using var process = StartSleeper();
        try
        {
            active.Register(process);

            process.Refresh();
            Assert.Equal(ProcessPriorityClass.BelowNormal, process.PriorityClass);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void SetPriority_WithNothingRunning_DoesNotThrow()
    {
        new ActiveEncodeProcess().SetPriority(EncodePriority.AboveNormal);
    }
}

public class ScheduleValidatorTests
{
    [Fact]
    public void ADisabledSchedule_IsNeverFlagged()
    {
        var schedule = new ScheduleSettings { Enabled = false, DayStart = "garbage" };
        Assert.Empty(ScheduleValidator.Validate(schedule));
    }

    [Fact]
    public void Everyday_ChecksOnlyTheEverydayWindow()
    {
        var schedule = new ScheduleSettings { Enabled = true, Mode = ScheduleMode.Everyday, DayStart = "8am", DayEnd = "22:00", WeekendDayStart = "bad" };

        var fields = ScheduleValidator.Validate(schedule).Select(i => i.Field).ToList();

        Assert.Equal(new[] { "dayStart" }, fields);
    }

    [Fact]
    public void WeekdaysAndWeekends_ChecksBothWindows()
    {
        var schedule = new ScheduleSettings { Enabled = true, Mode = ScheduleMode.WeekdaysAndWeekends, DayEnd = "x", WeekendDayStart = "y" };

        var fields = ScheduleValidator.Validate(schedule).Select(i => i.Field).ToList();

        Assert.Equal(new[] { "dayEnd", "weekendDayStart" }, fields);
    }

    [Fact]
    public void EachDay_NamesTheDayThatIsWrong()
    {
        var schedule = new ScheduleSettings { Enabled = true, Mode = ScheduleMode.EachDay };
        schedule.Days[3].End = "nope";

        var issue = Assert.Single(ScheduleValidator.Validate(schedule));

        Assert.Equal("day3End", issue.Field);
        Assert.Contains("Wednesday", issue.Message);
    }

    [Fact]
    public void EachDay_AShortListIsReported()
    {
        var schedule = new ScheduleSettings { Enabled = true, Mode = ScheduleMode.EachDay };
        schedule.Days.RemoveRange(4, 3);

        Assert.Contains(ScheduleValidator.Validate(schedule), i => i.Field == "days");
    }

    [Fact]
    public void ValidTimesProduceNoIssues()
    {
        Assert.Empty(ScheduleValidator.Validate(new ScheduleSettings { Enabled = true, Mode = ScheduleMode.EachDay }));
    }
}
