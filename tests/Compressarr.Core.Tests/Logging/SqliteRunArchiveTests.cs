using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Logging;
using Compressarr.Core.Reporting;
using Compressarr.Core.Tests.Presets;

namespace Compressarr.Core.Tests.Logging;

/// <summary>The run archive: what a report shows kept as rows, and the report rebuilt from them (3.0).</summary>
public class SqliteRunArchiveTests : AppDataTestBase
{
    private SqliteRunHistoryStore NewStore() => new(AppPaths.GetHistoryDbPath);
    private static readonly DateTime Ended = new(2026, 10, 24, 14, 5, 6, 789);

    private static ConversionResult File(string name, string lane = "Movies", bool success = true, bool passthrough = false, string? label = null,
        double? fps = null, string? preset = "HB 1080", ReportErrorCode? error = null, string? warning = null, IReadOnlyList<ReportWarningCode>? warnings = null,
        string? redirectedTo = null, string? detailLog = null, double before = 2, double after = 0.8, int seconds = 90, string? arr = null) => new()
    {
        LaneId = lane.ToLowerInvariant(), HomeLaneName = lane, FileName = name, FullName = @"C:\x\" + name, ContentType = name.Contains("S01") ? "TV Show" : "Movie",
        PresetName = preset, EncoderLabel = label, IsPassthrough = passthrough, AvgFps = fps, BeginSizeGb = before, EndSizeGb = success ? after : 0,
        Success = success, ErrorCode = error, PostProcessWarning = warning, WarningCodes = warnings ?? Array.Empty<ReportWarningCode>(),
        RedirectedToLaneId = redirectedTo?.ToLowerInvariant(), RedirectedToLaneName = redirectedTo, DetailLogFile = detailLog, ArrStatus = arr,
        StartTime = Ended.AddSeconds(-seconds), EndTime = Ended
    };

    private ReportModel Model(string detailLog) => new()
    {
        GeneratedAt = Ended,
        RunTime = TimeSpan.FromMinutes(72),
        RunNumber = 7,
        RetriesSucceeded = 2,
        Today = new HistoryRollup(5, 20.5, 8.25, 3600, 3, 2, 0),
        ThisMonth = new HistoryRollup(50, 200, 80.5, 36000),
        ThisYear = new HistoryRollup(500, 2000, 800, 360000),
        Lanes = new[]
        {
            new LaneReportSection { LaneDisplayName = "Movies", Results = new[]
            {
                File("Fight Club (1999).mkv", fps: 23.45, arr: "Unmonitored"),
                File("Passthrough Movie (2001).mkv", passthrough: true, label: "passthrough", fps: null),
                File("Caddyshack (1980).mkv", label: "ffmpeg", fps: 48.5, warning: "companion move failed", warnings: new[] { ReportWarningCode.CompanionFilesNotMoved }),
                File("Sesame Street (1969).mkv", redirectedTo: "Kids")
            } },
            new LaneReportSection { LaneDisplayName = "TV", Results = new[]
            {
                File("Show - S01E01.mkv", "TV", success: false, error: ReportErrorCode.EncodeFailed, detailLog: detailLog),
                File("Show - S01E02.mkv", "TV", success: true, preset: "FF 720", label: "HandBrake (Dolby Vision fallback)")
            } },
            new LaneReportSection { LaneDisplayName = "Anime", Results = Array.Empty<ConversionResult>(), LaneProblems = new[] { ReportErrorCode.LaneNoPresetConfigured } }
        }
    };

    private static RunHistoryRecord Record() => new(2026, 10, 24, 20, 8, 6, 1, 12, 0, RunNumber: 7, MovieCount: 4, TvCount: 2);

    [Fact]
    public void ASavedReport_RebuildsToExactlyTheSameHtml_IncludingProblemsLinksTagsAndRedirects()
    {
        var log = Path.Combine(AppData, "Show_HBdetails.txt");
        System.IO.File.WriteAllText(log, "log");          // the link to an encode's log only shows while the file exists
        var model = Model(log);
        var generator = new HtmlReportGenerator();
        var store = NewStore();

        var id = store.RecordRun(Record());
        store.SaveReport(id, model);
        var rebuilt = store.LoadReport(id)!;

        Assert.Equal(generator.Generate(model), generator.Generate(rebuilt));
        Assert.Contains("ERROR 106", generator.Generate(rebuilt));           // the empty lane's problem
        Assert.Contains("average speed 23.5 fps", generator.Generate(rebuilt));
    }

    [Fact]
    public void SavingTheReportAgain_ReplacesItsRows_NeverDuplicatesThem()
    {
        var store = NewStore();
        var id = store.RecordRun(Record());
        store.SaveReport(id, Model(""));
        store.SaveReport(id, Model(""));

        var rebuilt = store.LoadReport(id)!;

        Assert.Equal(3, rebuilt.Lanes.Count);
        Assert.Equal(6, rebuilt.Lanes.Sum(l => l.Results.Count));
    }

    [Fact]
    public void ARunWithoutAStoredReport_HasNoneToLoad_AndALegacyBlobComesBackAsItWas()
    {
        var store = NewStore();
        var plain = store.RecordRun(Record());
        Assert.Null(store.LoadReport(plain));
        Assert.Null(store.LoadLegacyReportHtml(plain));
        Assert.Null(store.LoadReport(999));

        var legacy = "<html>an old report</html>"u8.ToArray();
        using (var connection = HistoryDatabase.Open(AppPaths.GetHistoryDbPath()))
        using (var command = connection.CreateCommand())
        {
            using var packed = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(packed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(legacy);
            command.CommandText = "UPDATE runs SET legacy_report = @blob, has_report = 1 WHERE id = @id";
            command.Parameters.AddWithValue("@blob", packed.ToArray());
            command.Parameters.AddWithValue("@id", plain);
            command.ExecuteNonQuery();
        }

        Assert.Equal(legacy, store.LoadLegacyReportHtml(plain));
    }

    [Fact]
    public void APassWithOnlyAReport_IsStoredWithoutAHistoryRowOrAnyTotal_AndRebuilds()
    {
        var store = NewStore();
        var model = new ReportModel
        {
            GeneratedAt = Ended, RunTime = TimeSpan.FromSeconds(30), RunNumber = 0,
            Lanes = new[] { new LaneReportSection { LaneDisplayName = "Broken", Results = Array.Empty<ConversionResult>(), LaneProblems = new[] { ReportErrorCode.LaneNoPresetConfigured } } }
        };

        var id = store.RecordPass(model);

        Assert.True(id > 0);
        Assert.Empty(store.GetHistory(""));
        Assert.Empty(store.ListReports(DateTime.MinValue));
        Assert.Contains("ERROR 106", new HtmlReportGenerator().Generate(store.LoadReport(id)!));
        using var connection = HistoryDatabase.Open(AppPaths.GetHistoryDbPath());
        Assert.Equal(0, HistoryDatabase.Scalar(connection, "SELECT COUNT(*) FROM daily_totals"));
    }

    [Fact]
    public void TheReportList_IsNewestFirst_LimitedBySince_AndEachEntryCarriesTheRunIdAsItsLink()
    {
        var store = NewStore();
        var ids = new List<long>();
        foreach (var day in new[] { 10, 20, 24 })
        {
            var id = store.RecordRun(new RunHistoryRecord(2026, 10, day, 10, 4, 3, 0, 5, 0, RunNumber: day, ErrorCount: day == 20 ? 1 : 0));
            store.SaveReport(id, Model(""));
            ids.Add(id);
        }
        store.RecordRun(new RunHistoryRecord(2026, 10, 25, 10, 4, 3, 0, 5, 0, RunNumber: 25));   // no stored report: not listed

        var all = store.ListReports(new DateTime(2026, 10, 1));
        var recent = store.ListReports(new DateTime(2026, 10, 15));

        Assert.Equal(new[] { 24, 20, 10 }, all.Select(e => e.RunNumber).ToArray());
        Assert.Equal(new[] { 24, 20 }, recent.Select(e => e.RunNumber).ToArray());
        Assert.Equal(ids[2].ToString(), all[0].ReportFileName);
        Assert.Equal(1, all[1].ErrorCount);
        Assert.Equal(60.0, all[0].SavedPercent);
    }

    [Fact]
    public void TheFileSamples_AreTheSuccessfulEncodesOfTheRecentRuns_WithoutCopiesOrFailures()
    {
        var store = NewStore();
        var id = store.RecordRun(Record());
        store.SaveReport(id, Model(""));

        var samples = store.RecentFileSamples(5);

        // fight club, caddyshack, sesame street and Show S01E02 succeeded and were encoded; the copy and the failed encode are left out
        Assert.Equal(4, samples.Count);
        Assert.All(samples, s => Assert.True(s.Seconds > 0 && s.BeforeGb > 0));
        Assert.Contains(samples, s => s.Preset == "FF 720");
    }

    [Fact]
    public void TheStartingSpeedEstimate_ComesFromTheStoredFiles_AndFallsBackToTheReportFilesWhenThereAreNone()
    {
        var store = NewStore();
        var fallback = new FixedEstimator();
        var estimator = new ArchiveThroughputEstimator(store, fallback);

        Assert.Same(fallback.Result, estimator.Estimate("reports"));           // nothing stored yet

        var id = store.RecordRun(Record());
        store.SaveReport(id, new ReportModel
        {
            GeneratedAt = Ended, RunTime = TimeSpan.FromMinutes(10),
            Lanes = new[] { new LaneReportSection { LaneDisplayName = "Movies", Results = new[]
            {
                File("a.mkv", before: 6, seconds: 600, preset: "HB 1080"),         // 6 GB in 10 minutes
                File("b.mkv", before: 3, seconds: 300, preset: "HB 1080"),         // 3 GB in 5 minutes
                File("tiny.mkv", before: 0.01, seconds: 1)                         // below the minimum: ignored
            } } }
        });

        var estimate = estimator.Estimate("reports");

        Assert.Equal(0.6, estimate.GlobalRateGbPerMinute!.Value, 6);
        Assert.Equal(0.6, estimate.RateGbPerMinuteByPreset["hb 1080"], 6);        // case-insensitive, like the file-based one
    }

    private sealed class FixedEstimator : IHistoryThroughputEstimator
    {
        public HistoricalThroughput Result { get; } = new(new Dictionary<string, double>(), null);
        public HistoricalThroughput Estimate(string reportPath) => Result;
    }
}
