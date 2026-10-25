using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Scheduling;
using Compressarr.Core.Tests.Presets;

namespace Compressarr.Core.Tests.Scheduling;

/// <summary>A hold with a clock the test controls, for the schedule tests (the real one is in the state database).</summary>
public sealed class InMemoryExternalHold : IExternalHold
{
    private readonly TimeProvider _time;
    private HoldInfo? _hold;

    public InMemoryExternalHold() : this(TimeProvider.System) { }

    internal InMemoryExternalHold(TimeProvider time) => _time = time;

    public HoldInfo? Current()
    {
        if (_hold?.Until is { } until && until <= _time.GetUtcNow()) _hold = null;
        return _hold;
    }

    public HoldInfo Set(string? reason, DateTimeOffset? until, bool suspendCurrent, string? setBy) =>
        _hold = new HoldInfo(string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), _hold?.Since ?? _time.GetUtcNow(), until, suspendCurrent, setBy);

    public bool Release()
    {
        var had = _hold is not null;
        _hold = null;
        return had;
    }
}

/// <summary>The external hold in the state database: placed, changed (keeping its start), lapsing by itself, surviving a restart.</summary>
public class ExternalHoldTests : AppDataTestBase
{
    private readonly ScheduleTestClock _clock = new(new DateTime(2026, 10, 25, 20, 0, 0));

    private SqliteExternalHold NewHold() => new(new SqliteResumeStateStore(), AppPaths.GetResumeFilePath, _clock);

    [Fact]
    public void NoHoldToStartWith_AndReleasingNothingSaysSo()
    {
        var hold = NewHold();

        Assert.Null(hold.Current());
        Assert.False(hold.Release());
    }

    [Fact]
    public void APlacedHold_IsRemembered_WithEverythingItWasPlacedWith()
    {
        var until = new DateTimeOffset(2026, 10, 25, 23, 30, 0, TimeSpan.Zero);

        var placed = NewHold().Set("  TV on  ", until, suspendCurrent: true, "Home Assistant");

        var read = NewHold().Current();   // another instance reads the same database
        Assert.Equal(placed, read);
        Assert.Equal("TV on", read!.Reason);
        Assert.Equal(until, read.Until);
        Assert.True(read.SuspendCurrent);
        Assert.Equal("Home Assistant", read.SetBy);
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 20, 0, 0, TimeSpan.Zero), read.Since);
    }

    [Fact]
    public void ABlankReason_IsNone_AndNoUntilMeansUntilReleased()
    {
        var read = NewHold().Set("   ", null, false, "k");

        Assert.Null(read.Reason);
        Assert.Null(read.Until);
        Assert.False(read.SuspendCurrent);
    }

    [Fact]
    public void PlacingItAgain_ChangesTheHold_ButKeepsWhenItStarted()
    {
        var hold = NewHold();
        hold.Set("first", null, false, "a");
        _clock.Set(new DateTime(2026, 10, 25, 21, 0, 0));

        var again = hold.Set("second", new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero), true, "b");

        Assert.Equal(new DateTimeOffset(2026, 10, 25, 20, 0, 0, TimeSpan.Zero), again.Since);
        Assert.Equal(("second", true, "b"), (again.Reason, again.SuspendCurrent, again.SetBy));
        Assert.Equal(again, hold.Current());
    }

    [Fact]
    public void AHoldWithAnEnd_LapsesByItself_AndReadsAsNone()
    {
        var hold = NewHold();
        hold.Set("movie night", new DateTimeOffset(2026, 10, 25, 22, 0, 0, TimeSpan.Zero), false, "k");
        Assert.NotNull(hold.Current());

        _clock.Set(new DateTime(2026, 10, 25, 22, 0, 0));

        Assert.Null(hold.Current());
        Assert.Null(NewHold().Current());   // and it is gone from the database, not just hidden
    }

    [Fact]
    public void ReleasingARealHold_RemovesIt()
    {
        var hold = NewHold();
        hold.Set("x", null, false, "k");

        Assert.True(hold.Release());
        Assert.Null(hold.Current());
        Assert.False(hold.Release());
    }

    [Fact]
    public void WhenTheDatabaseCannotBeOpened_TheHoldLivesInMemoryForTheSession()
    {
        Directory.CreateDirectory(AppPaths.GetStateDbPath());   // a folder where the database file should be
        var hold = NewHold();

        hold.Set("still works", null, false, "k");

        Assert.Equal("still works", hold.Current()!.Reason);
        Assert.True(hold.Release());
        Assert.Null(hold.Current());
    }
}
