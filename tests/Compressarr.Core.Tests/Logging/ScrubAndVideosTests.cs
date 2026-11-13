using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Logging;
using Compressarr.Core.Reporting;
using Compressarr.Core.Tests.Presets;
using Microsoft.Data.Sqlite;

namespace Compressarr.Core.Tests.Logging;

/// <summary>The 3.1 additions to the history database: videos counted on their own (schema v2) and files scrubbed of their identity.</summary>
public class ScrubAndVideosTests : AppDataTestBase
{
    private SqliteRunHistoryStore NewStore() => new(AppPaths.GetHistoryDbPath);
    private static readonly DateTime Ended = new(2026, 11, 13, 9, 0, 0);

    private static ConversionResult File(string name, string type = "Movie", bool success = true, string? failure = null, string? warning = null, string? arr = null, string? log = null,
        double before = 2, double after = 0.8) => new()
    {
        LaneId = "lane", HomeLaneName = "Lane", FileName = name, FullName = @"D:\Private\" + name, ContentType = type, PresetName = "HB 1080",
        BeginSizeGb = before, EndSizeGb = success ? after : 0, Success = success, FailureReason = failure, PostProcessWarning = warning, ArrStatus = arr, DetailLogFile = log,
        ErrorCode = success ? null : ReportErrorCode.EncodeFailed, StartTime = Ended.AddMinutes(-10), EndTime = Ended
    };

    private static ReportModel Report(params ConversionResult[] files) => new()
    {
        GeneratedAt = Ended, RunTime = TimeSpan.FromMinutes(10), RunNumber = 1,
        Lanes = new[] { new LaneReportSection { LaneDisplayName = "Lane", Results = files } }
    };

    private static RunHistoryRecord Record(int files, int errors = 0, int warnings = 0, int? movies = null, int? tvs = null, int? videos = null) =>
        new(2026, 11, 13, 4, 1.6, files, 0, 10, 0, RunNumber: 1, ErrorCount: errors, WarningCount: warnings, MovieCount: movies, TvCount: tvs, VideoCount: videos);

    private long Save(SqliteRunHistoryStore store, RunHistoryRecord record, ReportModel report)
    {
        var id = store.RecordRun(record);
        store.SaveReport(id, report);
        return id;
    }

    // ---- videos, the third stat ----

    [Fact]
    public void AVideoRun_IsStoredAndSummedOnItsOwn_NotWithTheMovies()
    {
        var store = NewStore();
        store.RecordRun(Record(files: 6, movies: 3, tvs: 2, videos: 1));
        store.RecordRun(Record(files: 4, movies: 1, tvs: 0, videos: 3));

        var total = store.Sum(null, null)!;
        var row = store.GetHistory("").First();

        Assert.Equal(10, total.FileCount);
        Assert.Equal(4, total.MovieCount);
        Assert.Equal(2, total.TvCount);
        Assert.Equal(4, total.VideoCount);
        Assert.Equal(total.FileCount, total.MovieCount + total.TvCount + total.VideoCount);
        Assert.Equal(1, row.VideoCount);
    }

    [Fact]
    public void ARunRecordedWithoutVideos_HasNone_AndTheStatsEndpointQueriesAgree()
    {
        var store = NewStore();
        store.RecordRun(Record(files: 5, movies: 3, tvs: 2));

        var stats = store.GetStats(null, null);
        var run = Assert.Single(store.ListRuns(null, null, 1, 10).Runs);

        Assert.Equal(0, stats.Videos);
        Assert.Equal(3, stats.Movies);
        Assert.Equal(0, run.Videos);
    }

    [Fact]
    public void VideosAppearInThePeriodStats_AndInEachRunsSummary()
    {
        var store = NewStore();
        store.RecordRun(Record(files: 4, movies: 1, tvs: 0, videos: 3));

        Assert.Equal(3, store.GetStats(null, null).Videos);
        Assert.Equal(3, Assert.Single(store.ListRuns(null, null, 1, 10).Runs).Videos);
    }

    [Fact]
    public void AnUnsplitRun_KeepsCountingItsFilesAsUnknown_AndHasNoVideos()
    {
        var store = NewStore();
        store.RecordRun(Record(files: 7));      // no split recorded

        var total = store.Sum(null, null)!;

        Assert.Equal(7, total.UnknownCount);
        Assert.Equal(0, total.VideoCount);
    }

    [Fact]
    public void TheReportCountsVideosOnTheirOwn()
    {
        var report = Report(File("a (2020).mkv"), File("clip.mkv", "Video"), File("s - S01E01.mkv", "TV Show"));

        Assert.Equal(1, report.MovieCount);
        Assert.Equal(1, report.VideoCount);
        Assert.Equal(1, report.TvCount);
        Assert.Equal(report.TotalFiles, report.MovieCount + report.VideoCount + report.TvCount);
    }

    [Fact]
    public void TheCsvKeepsVideosInItsLastColumn_AndReadsAnOlderRowWithoutOne()
    {
        var csv = new CsvRunHistoryStore();
        csv.AppendRun(AppData, Record(files: 4, movies: 1, tvs: 0, videos: 3));

        var back = Assert.Single(csv.GetHistory(AppData));
        Assert.Equal(3, back.VideoCount);
        Assert.Equal("VideoCount", System.IO.File.ReadAllLines(AppPaths.GetHistoryFilePath())[0].Split(',')[^1]);

        // a row written before 3.1 has no such column: no videos, never a failure
        System.IO.File.WriteAllLines(AppPaths.GetHistoryFilePath(), new[]
        {
            "yyyy,mm,dd,BegSize,EndSize,FileCount,ProcessHours,ProcessMinutes,ProcessSeconds,RunNumber,ReportFileName,ErrorCount,WarningCount,RedirectCount,MovieCount,TvCount",
            "2026,09,09,10,4,3,0,5,0,1,,0,0,0,2,1"
        });
        var older = Assert.Single(csv.GetHistory(AppData));
        Assert.Null(older.VideoCount);
        Assert.Equal(2, older.MovieCount);
    }

    // ---- the upgrade: a version 1 database gains the new columns, nothing else moves ----

    [Fact]
    public void ADatabaseFromBefore31_IsUpgradedInPlace_WithItsRunsAndTotalsIntact()
    {
        var path = AppPaths.GetHistoryDbPath();
        // A database exactly as 3.0 created it: schema v1 and one recorded run.
        using (var connection = HistoryDatabase.Open(path))
        {
            var v1 = typeof(HistoryDatabase).GetField("SchemaV1", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!.ToString()!;
            HistoryDatabase.Execute(connection, v1);
            HistoryDatabase.Execute(connection, "PRAGMA user_version = 1;");
            HistoryDatabase.Execute(connection, """
                INSERT INTO runs (id, kind, counted, run_number, ended_date, seconds, files, before_gb, after_gb, movies, tvs, has_report) VALUES (1, 'run', 1, 1, '2026-10-01', 600, 3, 6, 2, 2, 1, 0);
                INSERT INTO daily_totals (day, runs, files, before_gb, after_gb, seconds, movies, tvs) VALUES ('2026-10-01', 1, 3, 6, 2, 600, 2, 1);
                """);
        }

        var store = NewStore();
        var total = store.Sum(null, null)!;          // the first use migrates

        Assert.Equal(3, total.FileCount);
        Assert.Equal(2, total.MovieCount);
        Assert.Equal(1, total.TvCount);
        Assert.Equal(0, total.VideoCount);
        using var check = HistoryDatabase.Open(path);
        Assert.Equal(HistoryDatabase.CurrentVersion, (int)HistoryDatabase.Scalar(check, "PRAGMA user_version;"));
        Assert.Equal(1, HistoryDatabase.Scalar(check, "SELECT COUNT(*) FROM runs;"));
    }

    // ---- the scrub ----

    [Fact]
    public void AScrubbedFile_LosesItsNameAndMessages_ButKeepsEveryNumber()
    {
        var store = NewStore();
        var log = System.IO.Path.Combine(AppData, "HBdetails.txt");
        var id = Save(store, Record(files: 1, errors: 1), Report(File("Emma Birthday.mkv", success: false, failure: @"No space on D:\Private", arr: "Radarr: Emma", log: log, before: 3)));
        var fileId = store.LoadReport(id)!.Lanes[0].Results[0].ArchiveRowId!.Value;

        var detailLog = store.ScrubFile(id, fileId);

        var file = Assert.Single(store.LoadReport(id)!.Lanes[0].Results);
        Assert.Equal(log, detailLog);                          // handed back so the caller can delete that file
        Assert.Equal("Removed", file.FileName);
        Assert.Equal("Removed", file.FailureReason);
        Assert.Equal("Removed", file.ArrStatus);
        Assert.Null(file.DetailLogFile);
        Assert.True(file.IsScrubbed);
        Assert.Equal(3, file.BeginSizeGb);                      // the numbers stay
        Assert.Equal(ReportErrorCode.EncodeFailed, file.ErrorCode);
        Assert.Equal("HB 1080", file.PresetName);
    }

    [Fact]
    public void Scrubbing_NeverMovesATotalOrALaneOrAProfileCount()
    {
        var store = NewStore();
        var id = Save(store, Record(files: 2, errors: 1, movies: 2), Report(File("a (2020).mkv"), File("b (2021).mkv", success: false, failure: "x")));
        var before = store.Sum(null, null)!;
        var statsBefore = store.GetStats(null, null);
        var fileId = store.LoadReport(id)!.Lanes[0].Results[1].ArchiveRowId!.Value;

        store.ScrubFile(id, fileId);

        Assert.Equal(before, store.Sum(null, null));
        Assert.Equal(statsBefore.Files, store.GetStats(null, null).Files);
        Assert.Equal(statsBefore.Errors, store.GetStats(null, null).Errors);
        Assert.Equal(statsBefore.BeforeGb, store.GetStats(null, null).BeforeGb);
        Assert.Equal(1, Assert.Single(store.GetHistory("")).RunNumber);       // no run number lost
    }

    [Fact]
    public void ARunWhoseProblemFilesAreAllScrubbed_DropsOutOfTheUnseenErrorsAndTheBadges()
    {
        var store = NewStore();
        var id = Save(store, Record(files: 2, errors: 1, warnings: 1),
            Report(File("a.mkv", success: false, failure: "x"), File("b.mkv", warning: "companion failed"), File("c.mkv")));
        Assert.Single(store.ListProblemReports(null, new DateTime(2026, 1, 1), 50));
        Assert.Equal((1, 1, 1), store.CountUnseen(0, new DateTime(2026, 1, 1)));
        var results = store.LoadReport(id)!.Lanes[0].Results;

        store.ScrubFile(id, results.Single(r => r.FileName == "a.mkv").ArchiveRowId!.Value);
        Assert.Single(store.ListProblemReports(null, new DateTime(2026, 1, 1), 50));      // one problem file is still open
        store.ScrubFile(id, results.Single(r => r.FileName == "b.mkv").ArchiveRowId!.Value);

        Assert.Empty(store.ListProblemReports(null, new DateTime(2026, 1, 1), 50));
        var counts = store.CountUnseen(0, new DateTime(2026, 1, 1));
        Assert.Equal(0, counts.ErrorRuns);
        Assert.Equal(0, counts.WarningRuns);
        Assert.Equal(1, counts.UnseenRuns);                                                 // the run itself is still there
    }

    [Fact]
    public void ARunWithNoStoredFiles_KeepsItsProblemFlag()
    {
        var store = NewStore();
        var id = store.RecordRun(Record(files: 1, errors: 1));
        store.SaveReport(id, Report());                         // a report with no file rows (an imported run, say)

        Assert.Single(store.ListProblemReports(null, new DateTime(2026, 1, 1), 50));
    }

    [Fact]
    public void ScrubbingAnUnknownFileOrTheWrongRun_ChangesNothing()
    {
        var store = NewStore();
        var id = Save(store, Record(files: 1), Report(File("a.mkv")));
        var fileId = store.LoadReport(id)!.Lanes[0].Results[0].ArchiveRowId!.Value;

        Assert.Null(store.ScrubFile(id, fileId + 999));
        Assert.Null(store.ScrubFile(id + 999, fileId));
        Assert.Equal("a.mkv", store.LoadReport(id)!.Lanes[0].Results[0].FileName);
    }

    [Fact]
    public void AScrubbedFile_HasNoTrashCanOnTheReportPage_AndAnUnscrubbedOneDoes()
    {
        var store = NewStore();
        var id = Save(store, Record(files: 2), Report(File("a.mkv"), File("b.mkv")));
        var results = store.LoadReport(id)!.Lanes[0].Results;
        store.ScrubFile(id, results[0].ArchiveRowId!.Value);

        var html = new HtmlReportGenerator().Generate(store.LoadReport(id)!);

        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(html, "data-fid=").Count);
        Assert.Contains($"data-fid=\"{results[1].ArchiveRowId}\"", html);
        Assert.DoesNotContain("a.mkv", html);
    }

    [Fact]
    public void AScrubbedFile_StaysScrubbed_AfterTheReportIsLoadedAgain()
    {
        var store = NewStore();
        var id = Save(store, Record(files: 1), Report(File("a.mkv")));
        store.ScrubFile(id, store.LoadReport(id)!.Lanes[0].Results[0].ArchiveRowId!.Value);

        var again = NewStore().LoadReport(id)!.Lanes[0].Results[0];

        Assert.True(again.IsScrubbed);
        Assert.Equal("Removed", again.FileName);
    }
}
