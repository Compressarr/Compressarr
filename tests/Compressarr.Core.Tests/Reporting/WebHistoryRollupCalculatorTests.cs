using Compressarr.Core.Logging;
using Compressarr.Core.Reporting;

namespace Compressarr.Core.Tests.Reporting;

file sealed class FakeRunHistoryStore : IRunHistoryStore
{
    public List<RunHistoryRecord> Records = new();
    public void AppendRun(string logFilePath, RunHistoryRecord record) => Records.Add(record);
    public IReadOnlyList<RunHistoryRecord> GetHistory(string logFilePath) => Records;
    public int GetRunCount(string runCountPath) => Records.Count;
    public void IncrementRunCount(string runCountPath) { }
}

public class WebHistoryRollupCalculatorTests
{
    private static RunHistoryRecord RecordOn(DateTime date, int fileCount = 1, double beforeGb = 10, double afterGb = 4, int processHours = 0, int processMinutes = 0, int processSeconds = 0) =>
        new(date.Year, date.Month, date.Day, beforeGb, afterGb, fileCount, processHours, processMinutes, processSeconds);

    [Fact]
    public void Now_IsReadFromTheClockEachTime_NotCapturedWhenTheCalculatorIsCreated()
    {
        // The calculator is a singleton for the life of the app - a clock read once at construction left
        // "Today" showing the day the app started.
        var before = DateTime.Now;
        var calc = new WebHistoryRollupCalculator(new FakeRunHistoryStore());

        System.Threading.Thread.Sleep(50);

        Assert.True(calc.Now >= before.AddMilliseconds(40));
    }

    [Fact]
    public void Calculate_CarriesTheMovieAndTvSplit_AndCountsFilesOfRunsWithoutOneAsUnknown()
    {
        var now = new DateTime(2026, 1, 10);
        var store = new FakeRunHistoryStore
        {
            Records =
            {
                RecordOn(now, fileCount: 5) with { MovieCount = 2, TvCount = 3 },
                RecordOn(now, fileCount: 4) with { MovieCount = 0, TvCount = 4 },
                RecordOn(now.AddDays(-20), fileCount: 7),                              // an older run: no split recorded
                RecordOn(now.AddDays(-400), fileCount: 6)
            }
        };
        var calc = new WebHistoryRollupCalculator(store) { Now = now };

        var result = calc.Calculate("any-path");

        Assert.Equal((9, 2, 7, 0), (result.Today.FileCount, result.Today.MovieCount, result.Today.TvCount, result.Today.UnknownCount));
        Assert.Equal((16, 2, 7, 7), (result.Last30Days.FileCount, result.Last30Days.MovieCount, result.Last30Days.TvCount, result.Last30Days.UnknownCount));
        Assert.Equal((22, 2, 7, 13), (result.AllTime.FileCount, result.AllTime.MovieCount, result.AllTime.TvCount, result.AllTime.UnknownCount));
    }

    [Fact]
    public void Calculate_RunsInWhichEveryFileFailed_AreSkipped_ButMixedRunsAreKept()
    {
        var now = new DateTime(2026, 1, 2);
        var store = new FakeRunHistoryStore
        {
            Records =
            {
                RecordOn(now, fileCount: 1, beforeGb: 0.5, afterGb: 0) with { ErrorCount = 1 }, // old-style failed row: 100% "saved"
                RecordOn(now, fileCount: 3, beforeGb: 9, afterGb: 3) with { ErrorCount = 1 },    // mixed: the other files really encoded
                RecordOn(now, fileCount: 2, beforeGb: 4, afterGb: 1)
            }
        };
        var calc = new WebHistoryRollupCalculator(store) { Now = now };

        var result = calc.Calculate("any-path");

        Assert.Equal(5, result.Today.FileCount);
        Assert.Equal(13, result.Today.BeforeGb);
        Assert.Equal(4, result.AllTime.AfterGb);
    }

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, Compressarr.Core.Conversion.ReportErrorCode.EncodeFailed, false)]
    [InlineData(false, Compressarr.Core.Conversion.ReportErrorCode.NoPresetConfigured, false)]
    [InlineData(false, Compressarr.Core.Conversion.ReportErrorCode.MoveDestinationUnavailable, true)]
    [InlineData(false, Compressarr.Core.Conversion.ReportErrorCode.MoveDiskFull, true)]
    [InlineData(false, Compressarr.Core.Conversion.ReportErrorCode.MoveFailedOther, true)]
    public void ConversionResult_CountsTowardTotals_OnlyWhenAnEncodeWasProduced(bool success, Compressarr.Core.Conversion.ReportErrorCode? code, bool expected)
    {
        var result = new Compressarr.Core.Conversion.ConversionResult
        {
            LaneId = "l", FileName = "f.mkv", FullName = "f.mkv", ContentType = "Movie", Success = success, ErrorCode = code
        };

        Assert.Equal(expected, result.CountsTowardTotals);
    }

    [Fact]
    public void Calculate_EmptyHistory_AllBucketsZero()
    {
        var store = new FakeRunHistoryStore();
        var calc = new WebHistoryRollupCalculator(store) { Now = new DateTime(2026, 1, 2) };

        var result = calc.Calculate("any-path");

        foreach (var bucket in new[] { result.Today, result.Last7Days, result.Last30Days, result.LastYear, result.AllTime })
        {
            Assert.Equal(0, bucket.FileCount);
            Assert.Equal(0, bucket.BeforeGb);
            Assert.Equal(0, bucket.AfterGb);
        }
    }

    [Fact]
    public void Calculate_AllTime_IncludesEverythingRegardlessOfDate()
    {
        var now = new DateTime(2026, 1, 2);
        var store = new FakeRunHistoryStore
        {
            Records = { RecordOn(now), RecordOn(now.AddYears(-5)), RecordOn(now.AddDays(-1000)) }
        };
        var calc = new WebHistoryRollupCalculator(store) { Now = now };

        var result = calc.Calculate("any-path");

        Assert.Equal(3, result.AllTime.FileCount);
    }

    [Fact]
    public void Calculate_RecordEightDaysOld_ExcludedFromLast7Days_ButInLast30AndYearAndAllTime()
    {
        var now = new DateTime(2026, 3, 15);
        var store = new FakeRunHistoryStore { Records = { RecordOn(now.AddDays(-8)) } };
        var calc = new WebHistoryRollupCalculator(store) { Now = now };

        var result = calc.Calculate("any-path");

        Assert.Equal(0, result.Last7Days.FileCount);
        Assert.Equal(1, result.Last30Days.FileCount);
        Assert.Equal(1, result.LastYear.FileCount);
        Assert.Equal(1, result.AllTime.FileCount);
    }

    [Fact]
    public void Calculate_RecordThirtyOneDaysOld_ExcludedFromLast7AndLast30_ButInLastYearAndAllTime()
    {
        var now = new DateTime(2026, 3, 15);
        var store = new FakeRunHistoryStore { Records = { RecordOn(now.AddDays(-31)) } };
        var calc = new WebHistoryRollupCalculator(store) { Now = now };

        var result = calc.Calculate("any-path");

        Assert.Equal(0, result.Last7Days.FileCount);
        Assert.Equal(0, result.Last30Days.FileCount);
        Assert.Equal(1, result.LastYear.FileCount);
        Assert.Equal(1, result.AllTime.FileCount);
    }

    [Fact]
    public void Calculate_RecordOverAYearOld_OnlyInAllTime()
    {
        var now = new DateTime(2026, 3, 15);
        var store = new FakeRunHistoryStore { Records = { RecordOn(now.AddDays(-366)) } };
        var calc = new WebHistoryRollupCalculator(store) { Now = now };

        var result = calc.Calculate("any-path");

        Assert.Equal(0, result.Last7Days.FileCount);
        Assert.Equal(0, result.Last30Days.FileCount);
        Assert.Equal(0, result.LastYear.FileCount);
        Assert.Equal(1, result.AllTime.FileCount);
    }

    [Fact]
    public void Calculate_RecordTodayIncludedInEveryBucket()
    {
        var now = new DateTime(2026, 3, 15);
        var store = new FakeRunHistoryStore { Records = { RecordOn(now) } };
        var calc = new WebHistoryRollupCalculator(store) { Now = now };

        var result = calc.Calculate("any-path");

        Assert.Equal(1, result.Today.FileCount);
        Assert.Equal(1, result.Last7Days.FileCount);
        Assert.Equal(1, result.Last30Days.FileCount);
        Assert.Equal(1, result.LastYear.FileCount);
        Assert.Equal(1, result.AllTime.FileCount);
    }

    [Fact]
    public void Calculate_CalendarBoundaryCrossing_DecemberRunStillCountsInLast7Days_WhenNowIsEarlyJanuary()
    {
        // The case that actually distinguishes "rolling" from the old calendar-aligned
        // HistoryRollupCalculator: a naive Year==now.Year && Month==now.Month filter would wrongly
        // exclude this, since Dec 28 is a different month AND year than Jan 2.
        var now = new DateTime(2026, 1, 2);
        var store = new FakeRunHistoryStore { Records = { RecordOn(new DateTime(2025, 12, 28)) } };
        var calc = new WebHistoryRollupCalculator(store) { Now = now };

        var result = calc.Calculate("any-path");

        Assert.Equal(1, result.Last7Days.FileCount);
    }

    [Fact]
    public void Calculate_SumsFileCountAndSizes_AcrossMultipleRecordsInBucket()
    {
        var now = new DateTime(2026, 3, 15);
        var store = new FakeRunHistoryStore
        {
            Records =
            {
                RecordOn(now, fileCount: 2, beforeGb: 10, afterGb: 4),
                RecordOn(now.AddDays(-1), fileCount: 3, beforeGb: 20, afterGb: 8)
            }
        };
        var calc = new WebHistoryRollupCalculator(store) { Now = now };

        var result = calc.Calculate("any-path");

        Assert.Equal(5, result.Last7Days.FileCount);
        Assert.Equal(30, result.Last7Days.BeforeGb);
        Assert.Equal(12, result.Last7Days.AfterGb);
    }

    [Fact]
    public void Calculate_SumsProcessTime_AcrossMultipleRecordsInBucket()
    {
        var now = new DateTime(2026, 3, 15);
        var store = new FakeRunHistoryStore
        {
            Records =
            {
                RecordOn(now, processHours: 1, processMinutes: 20, processSeconds: 30),
                RecordOn(now.AddDays(-1), processHours: 0, processMinutes: 45, processSeconds: 50)
            }
        };
        var calc = new WebHistoryRollupCalculator(store) { Now = now };

        var result = calc.Calculate("any-path");

        // 1h20m30s + 45m50s = 2h06m20s = 7580s
        Assert.Equal(7580, result.Last7Days.TotalTimeSeconds);
    }
}
