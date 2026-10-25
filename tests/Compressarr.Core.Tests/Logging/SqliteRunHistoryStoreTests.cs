using Compressarr.Core.Config;
using Compressarr.Core.Logging;
using Compressarr.Core.Tests.Presets;
using Microsoft.Data.Sqlite;

namespace Compressarr.Core.Tests.Logging;

/// <summary>The 3.0 run history in SQLite: the same records as the CSV, running totals kept in step, the run counter, and the CSV import.</summary>
public class SqliteRunHistoryStoreTests : AppDataTestBase
{
    private string Logs => Directory.CreateDirectory(Path.Combine(AppData, "Logs")).FullName;
    private SqliteRunHistoryStore NewStore() => new(AppPaths.GetHistoryDbPath);

    private static RunHistoryRecord Run(int day = 24, int number = 1, int files = 3, double before = 10, double after = 4, int seconds = 600, string report = "",
        int errors = 0, int? movies = 2, int? tv = 1, double? fps = null, int? hb = 3, int? ff = 0, int? pass = 0,
        Dictionary<string, int>? lanes = null, Dictionary<string, int>? profiles = null) =>
        new(2026, 10, day, before, after, files, seconds / 3600, seconds % 3600 / 60, seconds % 60, number, report, errors, 0, 0, movies, tv, fps, hb, ff, pass,
            hb is null ? null : lanes ?? new Dictionary<string, int> { ["Movies"] = files }, hb is null ? null : profiles ?? new Dictionary<string, int> { ["HB 1080"] = files });

    private static long Scalar(string sql)
    {
        using var connection = HistoryDatabase.Open(AppPaths.GetHistoryDbPath());
        return HistoryDatabase.Scalar(connection, sql);
    }

    [Fact]
    public void ARunRoundTripsWithEveryField_AndTheBreakdownKeepsKnownApartFromUnknown()
    {
        var store = NewStore();
        var lanes = new Dictionary<string, int> { ["Movies, HD"] = 2, ["TV"] = 1 };
        var profiles = new Dictionary<string, int> { ["HB 1080"] = 2 };
        store.AppendRun(Logs, Run(report: "Compressarr_2026-10-24_07-09-21_Report.html", errors: 1, fps: 23.45, hb: 2, ff: 0, pass: 1, lanes: lanes, profiles: profiles));
        store.AppendRun(Logs, Run(number: 2, hb: null, ff: null, pass: null, movies: null, tv: null));   // an older-style run: nothing recorded

        var history = store.GetHistory(Logs);

        Assert.Equal(2, history.Count);
        var first = history[0];
        Assert.Equal((2026, 10, 24, 3, 600), (first.Year, first.Month, first.Day, first.FileCount, first.ProcessMinutes * 60));
        Assert.Equal((10.0, 4.0, 1, 23.45), (first.BeginSizeGb, first.EndSizeGb, first.ErrorCount, first.AvgFps));
        Assert.Equal((2, 0, 1), (first.HandBrakeCount, first.FfmpegCount, first.PassthroughCount));
        Assert.Equal(lanes, first.LaneCounts);
        Assert.Equal(profiles, first.PresetCounts);
        Assert.Equal("Compressarr_2026-10-24_07-09-21_Report.html", first.ReportFileName);
        Assert.Null(history[1].MovieCount);          // unknown stays unknown, never 0
        Assert.Null(history[1].HandBrakeCount);
        Assert.Null(history[1].LaneCounts);
        Assert.Null(history[1].PresetCounts);
    }

    [Fact]
    public void ARunOfOnlyCopies_HasAKnownEmptyProfileList()
    {
        var store = NewStore();
        store.AppendRun(Logs, Run(files: 2, hb: 0, ff: 0, pass: 2, profiles: new Dictionary<string, int>()));

        var run = Assert.Single(store.GetHistory(Logs));

        Assert.Empty(run.PresetCounts!);
        Assert.Equal(2, run.PassthroughCount);
    }

    [Fact]
    public void TheRunCounter_CountsUp_AndSurvivesClearingTheHistory()
    {
        var store = NewStore();
        Assert.Equal(0, store.GetRunCount(AppPaths.GetRunCountFilePath()));

        store.IncrementRunCount(AppPaths.GetRunCountFilePath());
        store.IncrementRunCount(AppPaths.GetRunCountFilePath());
        store.AppendRun(Logs, Run());
        store.ClearHistory();

        Assert.Empty(store.GetHistory(Logs));
        Assert.Equal(2, store.GetRunCount(AppPaths.GetRunCountFilePath()));
        Assert.Equal(0, Scalar("SELECT COUNT(*) FROM daily_totals"));
    }

    [Fact]
    public void TheRunningTotals_AreKeptInStepWithEachRunRecorded_AndLeaveOutAllFailedRuns()
    {
        var store = NewStore();
        store.AppendRun(Logs, Run(day: 23, number: 1, files: 3, before: 10, after: 4, seconds: 600, fps: 20, lanes: new() { ["Movies"] = 2, ["TV"] = 1 }));
        store.AppendRun(Logs, Run(day: 24, number: 2, files: 2, before: 6, after: 2, seconds: 300, fps: 40, movies: null, tv: null, lanes: new() { ["Movies"] = 2 }));
        store.AppendRun(Logs, Run(day: 24, number: 3, files: 4, before: 8, after: 0, seconds: 100, errors: 4, hb: 4));   // every file failed, nothing produced

        Assert.Equal(3, Scalar("SELECT COUNT(*) FROM runs"));
        Assert.Equal(2, Scalar("SELECT SUM(runs) FROM daily_totals"));                  // the all-failed run is in no total
        Assert.Equal(5, Scalar("SELECT SUM(files) FROM daily_totals"));
        Assert.Equal(900, Scalar("SELECT SUM(seconds) FROM daily_totals"));
        Assert.Equal(2, Scalar("SELECT unknown_files FROM daily_totals WHERE day = '2026-10-24'"));   // the run whose movie / TV split is unknown
        Assert.Equal(2, Scalar("SELECT movies FROM daily_totals WHERE day = '2026-10-23'"));
        Assert.Equal(4, Scalar("SELECT SUM(files) FROM daily_lanes WHERE lane = 'Movies'"));
        Assert.Equal(1, Scalar("SELECT files FROM daily_lanes WHERE lane = 'TV'"));
        Assert.Equal(20 * 600 + 40 * 300, Scalar("SELECT CAST(SUM(fps_weighted) AS INTEGER) FROM daily_totals"));
    }

    [Fact]
    public void RebuildingTheTotals_GivesExactlyWhatRecordingEachRunGave()
    {
        var store = NewStore();
        for (var i = 1; i <= 12; i++) store.AppendRun(Logs, Run(day: 1 + i % 5, number: i, files: 1 + i % 4, before: i, after: i / 3.0, seconds: 60 * i, fps: i % 3 == 0 ? null : 20 + i,
            lanes: new() { [i % 2 == 0 ? "Movies" : "TV"] = 1 + i % 4 }));
        var before = Snapshot();

        store.RebuildTotals();

        Assert.Equal(before, Snapshot());
    }

    private static string Snapshot()
    {
        using var connection = HistoryDatabase.Open(AppPaths.GetHistoryDbPath());
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 'T', day, runs, files, ROUND(before_gb, 6), ROUND(after_gb, 6), seconds, movies, tvs, unknown_files, errors, hb_files, ROUND(fps_weighted, 6), fps_seconds FROM daily_totals
            UNION ALL SELECT 'L', day, lane, files, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 FROM daily_lanes
            UNION ALL SELECT 'P', day, profile, files, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 FROM daily_profiles
            ORDER BY 1, 2, 3
            """;
        using var reader = command.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read()) lines.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i))));
        return string.Join("\n", lines);
    }

    // ---- the CSV import ------------------------------------------------------------------------------------------------------------------

    private const string Header = "yyyy,mm,dd,BegSize,EndSize,FileCount,ProcessHours,ProcessMinutes,ProcessSeconds,RunNumber,ReportFileName,ErrorCount,WarningCount,RedirectCount,MovieCount,TvCount,AvgFps,HandBrakeCount,FfmpegCount,PassthroughCount,LaneCounts,PresetCounts";

    [Fact]
    public void TheFirstUse_ImportsTheCsv_KeepsItRenamed_AndTakesTheCounterWithIt()
    {
        File.WriteAllLines(AppPaths.GetHistoryFilePath(), new[]
        {
            Header,
            "2026,09,09,11.3,3.051,2,1,0,0,0",                                                            // the oldest layout: 10 columns
            "2026,09,10,3.895,1.165,3,0,2,3,2,r2.html,0,0,0",                                             // 14 columns
            "2026,10,23,10,4,5,0,5,0,3,Compressarr_2026-10-23_07-00-00_Report.html,1,0,0,2,3,23.45,4,1,0,Movies%2C HD:3|TV:2,HB 1080:4|FF 720:1",
            "2026,10,24,this,is,not,a,number,row,9,x.html",                                              // damaged: skipped, the rest still imports
            "2026,10,24,1,2"                                                                              // truncated: skipped
        });
        File.WriteAllText(AppPaths.GetRunCountFilePath(), "57");

        var history = NewStore().GetHistory(Logs);

        Assert.Equal(3, history.Count);
        Assert.Equal(new[] { 2, 3, 5 }, history.Select(r => r.FileCount).ToArray());
        Assert.Equal(new[] { 0, 2, 3 }, history.Select(r => r.RunNumber).ToArray());
        Assert.Null(history[0].MovieCount);                              // old rows: unknown, not 0
        Assert.Null(history[1].HandBrakeCount);
        Assert.Equal((2, 3, 23.45), (history[2].MovieCount, history[2].TvCount, history[2].AvgFps));
        Assert.Equal(new Dictionary<string, int> { ["Movies, HD"] = 3, ["TV"] = 2 }, history[2].LaneCounts);
        Assert.Equal(57, NewStore().GetRunCount(AppPaths.GetRunCountFilePath()));       // the counter file's value, not the number of rows
        Assert.False(File.Exists(AppPaths.GetHistoryFilePath()));
        Assert.True(File.Exists(AppPaths.GetHistoryFilePath() + ".imported"));          // kept, never deleted
        Assert.True(File.Exists(AppPaths.GetRunCountFilePath() + ".imported"));
        Assert.Equal(2, Scalar("SELECT CAST(value AS INTEGER) FROM meta WHERE key = 'imported_skipped_rows'"));
        Assert.Equal(10, Scalar("SELECT SUM(files) FROM daily_totals"));               // the totals were built from what came in
    }

    private static Compressarr.Core.Conversion.ConversionResult ReportFile(string name, string lane, string type = "Movie", bool success = true, bool passthrough = false,
        string? label = null, string preset = "HB 1080", Compressarr.Core.Conversion.ReportErrorCode? error = null) => new()
    {
        LaneId = lane, HomeLaneName = lane, FileName = name, FullName = name, ContentType = type, PresetName = passthrough ? null : preset, EncoderLabel = label,
        IsPassthrough = passthrough, BeginSizeGb = 2, EndSizeGb = success ? 1 : 0, Success = success, ErrorCode = error,
        StartTime = new DateTime(2026, 10, 23, 7, 0, 0), EndTime = new DateTime(2026, 10, 23, 7, 5, 0)
    };

    [Fact]
    public void ARunWhoseReportIsStillOnDisk_KeepsItAsItWas_AndGainsTheMovieTvSplitAndTheLaneToolAndProfileBreakdown()
    {
        var reports = Directory.CreateDirectory(Path.Combine(AppData, "Reports")).FullName;
        var generator = new Compressarr.Core.Reporting.HtmlReportGenerator();
        var matching = generator.Generate(new Compressarr.Core.Reporting.ReportModel
        {
            GeneratedAt = new DateTime(2026, 10, 23, 7, 30, 0), RunTime = TimeSpan.FromMinutes(30),
            Lanes = new[]
            {
                new Compressarr.Core.Reporting.LaneReportSection { LaneDisplayName = "Movies, HD", Results = new[]
                {
                    ReportFile("a.mkv", "Movies, HD"),
                    ReportFile("b.mkv", "Movies, HD", label: "ffmpeg", preset: "FF 720"),
                    ReportFile("c.mkv", "Movies, HD", passthrough: true)
                } },
                new Compressarr.Core.Reporting.LaneReportSection { LaneDisplayName = "TV", Results = new[]
                {
                    ReportFile("d - S01E01.mkv", "TV", "TV Show", success: false, error: Compressarr.Core.Conversion.ReportErrorCode.EncodeFailed),   // not counted
                    ReportFile("d - S01E02.mkv", "TV", "TV Show")
                } }
            }
        });
        File.WriteAllText(Path.Combine(reports, "r1.html"), matching);
        File.WriteAllText(Path.Combine(reports, "r2.html"), generator.Generate(new Compressarr.Core.Reporting.ReportModel
        {
            GeneratedAt = new DateTime(2026, 10, 23, 9, 0, 0), RunTime = TimeSpan.FromMinutes(1),
            Lanes = new[] { new Compressarr.Core.Reporting.LaneReportSection { LaneDisplayName = "Movies, HD", Results = new[] { ReportFile("x.mkv", "Movies, HD") } } }
        }));
        File.WriteAllLines(AppPaths.GetHistoryFilePath(), new[]
        {
            Header,
            "2026,10,23,10,4,4,0,30,0,1,r1.html,1,0,0",     // 4 counted files: the report adds up
            "2026,10,23,10,4,9,0,1,0,2,r2.html,0,0,0",      // the report shows 1 file, the row says 9: not trusted
            "2026,10,23,10,4,2,0,1,0,3,gone.html,0,0,0"     // its report was cleaned up
        });
        var store = new SqliteRunHistoryStore(AppPaths.GetHistoryDbPath, () => Logs, () => reports);

        var history = store.GetHistory(Logs);

        var first = history[0];
        Assert.Equal((3, 1), (first.MovieCount, first.TvCount));
        Assert.Equal((2, 1, 1), (first.HandBrakeCount, first.FfmpegCount, first.PassthroughCount));
        Assert.Equal(new Dictionary<string, int> { ["Movies, HD"] = 3, ["TV"] = 1 }, first.LaneCounts);
        Assert.Equal(new Dictionary<string, int> { ["HB 1080"] = 2, ["FF 720"] = 1 }, first.PresetCounts);
        Assert.True(first.HasReport);
        Assert.Equal(File.ReadAllBytes(Path.Combine(reports, "r1.html")), store.LoadLegacyReportHtml(first.Id));     // exactly as it was saved
        Assert.Equal(5, Scalar("SELECT COUNT(*) FROM run_files WHERE run_id = " + first.Id));                         // every file row, the failed one too

        Assert.Null(history[1].HandBrakeCount);                    // does not add up: left unknown, never guessed
        Assert.Null(history[1].MovieCount);
        Assert.True(history[1].HasReport);                         // but its report is still kept and can be opened
        Assert.False(history[2].HasReport);
        Assert.Equal(2, Scalar("SELECT CAST(value AS INTEGER) FROM meta WHERE key = 'imported_reports'"));
        Assert.Equal(4, Scalar("SELECT SUM(movies + tvs) FROM daily_totals"));                                          // the totals use the enriched split
    }

    [Fact]
    public void ACsvInTheOld21LogsFolder_IsFoundAndImported_EvenWhenTheCounterIsAskedForFirst()
    {
        File.WriteAllLines(Path.Combine(Logs, "Compressarr_History.csv"), new[] { Header, "2026,09,09,11.3,3.051,2,1,0,0,4,r.html,0,0,0" });
        var store = new SqliteRunHistoryStore(AppPaths.GetHistoryDbPath, () => Logs);

        Assert.Equal(4, store.GetRunCount(AppPaths.GetRunCountFilePath()));   // no counter file: the highest run number
        Assert.Single(store.GetHistory(Logs));
        Assert.False(File.Exists(Path.Combine(Logs, "Compressarr_History.csv")));
    }

    [Fact]
    public void AFreshInstall_GetsAnEmptyDatabase_AndNothingToImport()
    {
        var store = NewStore();

        Assert.Empty(store.GetHistory(Logs));
        Assert.Equal(0, store.GetRunCount(AppPaths.GetRunCountFilePath()));
        Assert.True(File.Exists(AppPaths.GetHistoryDbPath()));
    }

    [Fact]
    public void ADatabaseThatIsAlreadyThere_IsNeverImportedOverAgain()
    {
        var store = NewStore();
        store.AppendRun(Logs, Run(number: 1));
        File.WriteAllLines(AppPaths.GetHistoryFilePath(), new[] { Header, "2026,09,09,1,1,9,0,0,1,7,r.html,0,0,0" });   // a stray CSV turning up later

        Assert.Single(NewStore().GetHistory(Logs));       // the database is the history; the stray file is not read
    }

    [Fact]
    public void ADamagedDatabaseThatWasAlreadyThere_IsNeverDeleted_AndTheCsvServesMeanwhile()
    {
        File.WriteAllText(AppPaths.GetHistoryDbPath(), "this is not a database");
        File.WriteAllLines(AppPaths.GetHistoryFilePath(), new[] { Header, "2026,09,09,1,1,2,0,0,1,7,r.html,0,0,0" });
        var store = NewStore();

        var history = store.GetHistory(Logs);

        Assert.Equal(2, Assert.Single(history).FileCount);              // served from the CSV
        Assert.NotNull(store.ImportFailure);
        Assert.Equal("this is not a database", File.ReadAllText(AppPaths.GetHistoryDbPath()));
        Assert.True(File.Exists(AppPaths.GetHistoryFilePath()));
    }

    [Fact]
    public void ADatabaseFromANewerBuild_IsRefused_NotGuessedAt()
    {
        using (var connection = HistoryDatabase.Open(AppPaths.GetHistoryDbPath()))
        {
            HistoryDatabase.Migrate(connection);
            HistoryDatabase.Execute(connection, "PRAGMA user_version = 99;");
        }
        var store = NewStore();

        store.GetHistory(Logs);

        Assert.Contains("newer", store.ImportFailure!);
    }

    // ---- backup and restore of the database ----------------------------------------------------------------------------------------------

    [Fact]
    public void ASnapshot_IsAConsistentCopyThatOpensAndPassesTheChecks()
    {
        var store = NewStore();
        store.AppendRun(Logs, Run());
        var copy = Path.Combine(AppData, "snapshot.db");

        Assert.True(HistoryDatabase.Snapshot(AppPaths.GetHistoryDbPath(), copy));
        HistoryDatabase.ValidateAndMigrate(copy);

        using var connection = HistoryDatabase.Open(copy);
        Assert.Equal(1, HistoryDatabase.Scalar(connection, "SELECT COUNT(*) FROM runs"));
    }

    [Fact]
    public void NoDatabase_MeansNoSnapshot_AndAGarbageFileIsNotAcceptedAsABackup()
    {
        Assert.False(HistoryDatabase.Snapshot(Path.Combine(AppData, "missing.db"), Path.Combine(AppData, "x.db")));

        var garbage = Path.Combine(AppData, "garbage.db");
        File.WriteAllText(garbage, "not a database at all, just text that is long enough to look like a file");
        Assert.Throws<InvalidDataException>(() => HistoryDatabase.ValidateAndMigrate(garbage));
    }
}
