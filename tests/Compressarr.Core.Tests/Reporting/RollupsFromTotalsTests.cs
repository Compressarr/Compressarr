using Compressarr.Core.Config;
using Compressarr.Core.Logging;
using Compressarr.Core.Reporting;
using Compressarr.Core.Tests.Presets;

namespace Compressarr.Core.Tests.Reporting;

/// <summary>The period figures read from the running totals must be exactly what adding up every run gives.</summary>
public class RollupsFromTotalsTests : AppDataTestBase
{
    private sealed class ListStore : IRunHistoryStore
    {
        private readonly List<RunHistoryRecord> _runs;
        public ListStore(List<RunHistoryRecord> runs) => _runs = runs;
        public void AppendRun(string legacyLogFolder, RunHistoryRecord record) => _runs.Add(record);
        public IReadOnlyList<RunHistoryRecord> GetHistory(string legacyLogFolder) => _runs;
        public int GetRunCount(string runCountPath) => 0;
        public void IncrementRunCount(string runCountPath) { }
    }

    private static List<RunHistoryRecord> RunsAround(DateTime now)
    {
        var runs = new List<RunHistoryRecord>();
        var number = 0;
        // today, yesterday, a few days back, this month, last month, this year, last year, years ago - several runs a day, some with an unknown split
        foreach (var daysAgo in new[] { 0, 0, 0, 1, 2, 5, 6, 7, 8, 20, 29, 30, 31, 45, 90, 200, 364, 365, 366, 400, 800 })
        {
            var d = now.Date.AddDays(-daysAgo);
            number++;
            var known = number % 3 != 0;
            runs.Add(new RunHistoryRecord(d.Year, d.Month, d.Day, 5 + number, 2 + number / 3.0, 1 + number % 4, number % 2, number % 60, number % 59, number,
                MovieCount: known ? 1 : null, TvCount: known ? number % 4 : null));
        }
        runs.Add(new RunHistoryRecord(now.Year, now.Month, now.Day, 50, 0, 6, 0, 1, 0, 99, "r.html", ErrorCount: 6));   // every file failed: in no total
        return runs;
    }

    [Fact]
    public void TheWebWindows_FromTheTotals_EqualSummingTheRuns_AcrossEveryBoundary()
    {
        var now = new DateTime(2026, 10, 24, 15, 30, 0);
        var runs = RunsAround(now);
        var store = new SqliteRunHistoryStore(AppPaths.GetHistoryDbPath);
        foreach (var r in runs) store.AppendRun("", r);

        var fromTotals = new WebHistoryRollupCalculator(store) { Now = now }.Calculate("");
        var summed = new WebHistoryRollupCalculator(new ListStore(runs)) { Now = now }.Calculate("");

        Assert.Equal(summed, fromTotals);
        Assert.True(fromTotals.AllTime.FileCount > fromTotals.LastYear.FileCount);        // the data really does cross the boundaries
        Assert.True(fromTotals.LastYear.FileCount > fromTotals.Last30Days.FileCount);
        Assert.True(fromTotals.Last30Days.FileCount > fromTotals.Last7Days.FileCount);
    }

    [Fact]
    public void TheReportsCalendarFigures_FromTheTotals_EqualSummingTheRuns()
    {
        var now = DateTime.Now;
        var runs = RunsAround(now);
        var store = new SqliteRunHistoryStore(AppPaths.GetHistoryDbPath);
        foreach (var r in runs) store.AppendRun("", r);

        var fromTotals = new HistoryRollupCalculator(store).Calculate("");
        var summed = new HistoryRollupCalculator(new ListStore(runs)).Calculate("");

        Assert.Equal(summed, fromTotals);
    }

    [Fact]
    public void WithNoRuns_TheTotalsGiveZeroRows_NotNothing()
    {
        var store = new SqliteRunHistoryStore(AppPaths.GetHistoryDbPath);

        var rollups = new WebHistoryRollupCalculator(store).Calculate("");

        Assert.Equal(new HistoryRollup(0, 0, 0, 0, 0, 0, 0), rollups.AllTime);
        Assert.Equal(rollups.Today, rollups.AllTime);
    }
}
