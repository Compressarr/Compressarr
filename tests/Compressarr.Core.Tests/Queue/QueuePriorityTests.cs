using Compressarr.Core.Conversion;
using Compressarr.Core.Queue;

namespace Compressarr.Core.Tests.Queue;

public class QueuePriorityTests
{
    private static QueuePriorityRules Rules(params PriorityRule[] rules) => new(rules);
    private static PriorityRule Lower(string match) => new() { Match = match, Priority = QueuePriority.Lower };
    private static PriorityRule Higher(string match) => new() { Match = match, Priority = QueuePriority.Higher };

    private static ResumeEntry Arrive(List<ResumeEntry> queue, string fileName, QueuePriorityRules rules)
    {
        var entry = new ResumeEntry { LaneId = "lane", FullName = @"D:\Input\" + fileName, Status = ResumeStatus.Pending };
        ResumeQueueOrder.Place(queue, entry, rules);
        queue.Add(entry);
        return entry;
    }

    private static List<string> Names(List<ResumeEntry> queue) =>
        queue.Where(e => e.Order.HasValue).OrderBy(e => e.Order!.Value).Select(e => Path.GetFileName(e.FullName)).ToList();

    // ---- matching ----

    [Theory]
    [InlineData("Doctor Who", @"D:\In\Doctor Who - S01E01.mkv", true)]
    [InlineData("doctor.who", @"D:\In\Doctor Who - S01E01.mkv", true)]
    [InlineData("DOCTOR WHO!", @"D:\In\doctor.who.s01e01.mkv", true)]
    [InlineData("Who", @"D:\In\Doctor Who - S01E01.mkv", true)]                    // text the title contains
    [InlineData("Babylon", @"D:\In\Doctor Who - S01E01.mkv", false)]
    [InlineData("Alien", @"D:\In\Alien (1979).mkv", true)]
    [InlineData("1979", @"D:\In\Alien (1979).mkv", false)]                          // the year is not part of the title
    public void ATitleRule_MatchesOnTheShowOrMovieTitle_IgnoringCaseAndPunctuation(string match, string path, bool matches)
    {
        Assert.Equal(matches, QueuePriorityRules.RuleMatches(Lower(match), path));
    }

    [Theory]
    [InlineData(@"D:\In\Show {tmdb-1396}\Season 01\Show - S01E01.mkv", true)]
    [InlineData(@"D:\In\Show [tmdbid-1396]\Season 01\Show - S01E01.mkv", true)]
    [InlineData(@"D:\In\Show (tmdb=1396)\x.mkv", true)]
    [InlineData(@"D:\In\Show {tmdb-13960}\x.mkv", false)]                           // a different id that merely starts the same way
    [InlineData(@"D:\In\Show\x.mkv", false)]
    public void ATmdbIdRule_MatchesAnIdTaggedInTheFileOrFolderName(string path, bool matches)
    {
        var rule = new PriorityRule { TmdbId = 1396, Priority = QueuePriority.Lower };

        Assert.Equal(matches, QueuePriorityRules.RuleMatches(rule, path));
    }

    [Fact]
    public void TheFirstMatchingRuleWins_AndNoRulesMeansNormal()
    {
        var rules = Rules(Higher("Bluey"), Lower("Bluey"), Lower("Cars"));

        Assert.Equal(1, rules.TierOf(@"D:\In\Bluey - S01E01.mkv"));
        Assert.Equal(-1, rules.TierOf(@"D:\In\Cars (2006).mkv"));
        Assert.Equal(0, rules.TierOf(@"D:\In\Alien (1979).mkv"));
        Assert.Equal(0, QueuePriorityRules.None.TierOf(@"D:\In\Bluey - S01E01.mkv"));
    }

    [Fact]
    public void ARuleWithNothingToMatch_IsIgnored()
    {
        var rules = Rules(new PriorityRule { Match = "  ", Priority = QueuePriority.Lower });

        Assert.True(rules.IsEmpty);
        Assert.Equal(0, rules.TierOf(@"D:\In\Anything (2000).mkv"));
    }

    // ---- placement of new arrivals ----

    [Fact]
    public void WithNoRules_EveryArrivalGoesToTheEnd_AsBefore()
    {
        var queue = new List<ResumeEntry>();

        Arrive(queue, "a.mkv", QueuePriorityRules.None);
        Arrive(queue, "b.mkv", QueuePriorityRules.None);
        Arrive(queue, "c.mkv", QueuePriorityRules.None);

        Assert.Equal(new[] { "a.mkv", "b.mkv", "c.mkv" }, Names(queue));
    }

    [Fact]
    public void ALoweredShowsThirtyEpisodes_DoNotBlockNewerContent()
    {
        var rules = Rules(Lower("Big Show"));
        var queue = new List<ResumeEntry>();
        Arrive(queue, "Alien (1979).mkv", rules);
        for (var i = 1; i <= 30; i++) Arrive(queue, $"Big Show - S01E{i:00}.mkv", rules);

        Arrive(queue, "Heat (1995).mkv", rules);            // newer content from another show
        Arrive(queue, "Ronin (1998).mkv", rules);

        var names = Names(queue);
        Assert.Equal(new[] { "Alien (1979).mkv", "Heat (1995).mkv", "Ronin (1998).mkv" }, names.Take(3));
        Assert.Equal("Big Show - S01E01.mkv", names[3]);     // the lowered show follows, in its own arrival order
        Assert.Equal("Big Show - S01E30.mkv", names[^1]);
        Assert.Equal(33, names.Count);
    }

    [Fact]
    public void ALoweredShowArrivingLater_GoesBehindTheBacklogAlreadyThere_NotAheadOfNewerFiles()
    {
        var rules = Rules(Lower("Big Show"));
        var queue = new List<ResumeEntry>();
        Arrive(queue, "Big Show - S01E01.mkv", rules);
        Arrive(queue, "Heat (1995).mkv", rules);
        Arrive(queue, "Big Show - S01E02.mkv", rules);
        Arrive(queue, "Ronin (1998).mkv", rules);

        Assert.Equal(new[] { "Heat (1995).mkv", "Ronin (1998).mkv", "Big Show - S01E01.mkv", "Big Show - S01E02.mkv" }, Names(queue));
    }

    [Fact]
    public void ARaisedShow_GoesAheadOfEverythingWaiting_AndItsOwnFilesKeepTheirArrivalOrder()
    {
        var rules = Rules(Higher("Bluey"));
        var queue = new List<ResumeEntry>();
        Arrive(queue, "a.mkv", rules);
        Arrive(queue, "b.mkv", rules);

        Arrive(queue, "Bluey - S01E01.mkv", rules);
        Arrive(queue, "Bluey - S01E02.mkv", rules);
        Arrive(queue, "c.mkv", rules);

        Assert.Equal(new[] { "Bluey - S01E01.mkv", "Bluey - S01E02.mkv", "a.mkv", "b.mkv", "c.mkv" }, Names(queue));
    }

    [Fact]
    public void RaisedNormalAndLowered_AreKeptInThreeBands()
    {
        var rules = Rules(Higher("Bluey"), Lower("Big Show"));
        var queue = new List<ResumeEntry>();
        foreach (var name in new[] { "Big Show - S01E01.mkv", "a.mkv", "Bluey - S01E01.mkv", "Big Show - S01E02.mkv", "b.mkv", "Bluey - S01E02.mkv" })
            Arrive(queue, name, rules);

        Assert.Equal(new[] { "Bluey - S01E01.mkv", "Bluey - S01E02.mkv", "a.mkv", "b.mkv", "Big Show - S01E01.mkv", "Big Show - S01E02.mkv" }, Names(queue));
    }

    [Fact]
    public void PlacingAFile_NeverReordersTheFilesAlreadyWaiting_OnlyShiftsThem()
    {
        var rules = Rules(Lower("Big Show"));
        var queue = new List<ResumeEntry>();
        Arrive(queue, "a.mkv", rules);
        Arrive(queue, "Big Show - S01E01.mkv", rules);
        Arrive(queue, "Big Show - S01E02.mkv", rules);
        // the user drags the second episode to the top by hand
        queue.Single(e => e.FullName.EndsWith("E02.mkv")).Order = -1;

        Arrive(queue, "b.mkv", rules);

        Assert.Equal(new[] { "Big Show - S01E02.mkv", "a.mkv", "b.mkv", "Big Show - S01E01.mkv" }, Names(queue));   // the hand placement stays on top
    }

    [Fact]
    public void FilesThatAreNotWaiting_DoNotCountWhenPlacing()
    {
        var rules = Rules(Lower("Big Show"));
        var queue = new List<ResumeEntry>
        {
            new() { LaneId = "lane", FullName = @"D:\Input\old.mkv", Status = ResumeStatus.Completed, Order = 50 },
            new() { LaneId = "lane", FullName = @"D:\Input\gone.mkv", Status = ResumeStatus.Pending, Removed = true, Order = 60 }
        };

        var entry = Arrive(queue, "Big Show - S01E01.mkv", rules);

        Assert.Equal(61, entry.Order);        // nothing waiting: the very end, as before
    }

    [Fact]
    public void TrackingScannedFiles_PlacesNewArrivalsByTheRules()
    {
        var rules = Rules(Lower("Big Show"));
        var queue = new List<ResumeEntry>();
        var scanned = new[] { "Big Show - S01E01.mkv", "Big Show - S01E02.mkv", "Heat (1995).mkv" }.Select(n => new FileInfo(@"D:\Input\" + n)).ToList();

        var added = QueueRules.TrackScannedFiles(queue, "lane", scanned, new HashSet<string>(), laneHadPending: false, rules);

        Assert.True(added);
        Assert.Equal(new[] { "Heat (1995).mkv", "Big Show - S01E01.mkv", "Big Show - S01E02.mkv" }, Names(queue));
    }

    // ---- applying to what is already waiting ----

    [Fact]
    public void ApplyingTheRulesToWaitingFiles_SortsThemByTier_KeepingTheOrderWithinEachTier()
    {
        var queue = new List<ResumeEntry>();
        foreach (var name in new[] { "Big Show - S01E01.mkv", "a.mkv", "Bluey - S01E01.mkv", "Big Show - S01E02.mkv", "b.mkv", "Bluey - S01E02.mkv" })
            Arrive(queue, name, QueuePriorityRules.None);
        var rules = Rules(Higher("Bluey"), Lower("Big Show"));

        var moved = QueueRules.ApplyPriorityToWaiting(queue, rules);

        Assert.True(moved > 0);
        Assert.Equal(new[] { "Bluey - S01E01.mkv", "Bluey - S01E02.mkv", "a.mkv", "b.mkv", "Big Show - S01E01.mkv", "Big Show - S01E02.mkv" }, Names(queue));
    }

    [Fact]
    public void ApplyingTheRules_WithNoneMatching_ChangesNothing()
    {
        var queue = new List<ResumeEntry>();
        foreach (var name in new[] { "a.mkv", "b.mkv", "c.mkv" }) Arrive(queue, name, QueuePriorityRules.None);

        var moved = QueueRules.ApplyPriorityToWaiting(queue, Rules(Lower("Big Show")));

        Assert.Equal(0, moved);
        Assert.Equal(new[] { "a.mkv", "b.mkv", "c.mkv" }, Names(queue));
    }

    [Fact]
    public void ApplyingTheRules_LeavesFilesThatAreNotWaitingAlone()
    {
        var queue = new List<ResumeEntry>
        {
            new() { LaneId = "lane", FullName = @"D:\Input\Big Show - S01E01.mkv", Status = ResumeStatus.Error, Order = 0 },
            new() { LaneId = "lane", FullName = @"D:\Input\Big Show - S01E02.mkv", Status = ResumeStatus.Pending, Order = 1 },
            new() { LaneId = "lane", FullName = @"D:\Input\a.mkv", Status = ResumeStatus.Pending, Order = 2 }
        };

        QueueRules.ApplyPriorityToWaiting(queue, Rules(Lower("Big Show")));

        Assert.Equal(0, queue[0].Order);                      // the failed file keeps its place
        Assert.Equal(2, queue[1].Order);                      // the lowered episode drops behind a.mkv
        Assert.Equal(1, queue[2].Order);
    }
}
