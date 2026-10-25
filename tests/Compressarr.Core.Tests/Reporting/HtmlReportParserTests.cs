using Compressarr.Core.Conversion;
using Compressarr.Core.Reporting;

namespace Compressarr.Core.Tests.Reporting;

/// <summary>Reading the file rows back out of a report saved by an earlier version (used by the 3.0 history import).</summary>
public class HtmlReportParserTests
{
    private static readonly DateTime Ended = new(2026, 10, 24, 14, 5, 6);

    private static ConversionResult File(string name, string lane, string type = "Movie", bool success = true, bool passthrough = false, string? label = null,
        string? preset = "HB 1080", ReportErrorCode? error = null, string? warning = null, IReadOnlyList<ReportWarningCode>? warnings = null,
        string? redirectedTo = null, string? arr = null, double before = 2.5, double after = 1.25, int seconds = 3725) => new()
    {
        LaneId = lane, HomeLaneName = lane, FileName = name, FullName = name, ContentType = type, PresetName = preset, EncoderLabel = label, IsPassthrough = passthrough,
        BeginSizeGb = before, EndSizeGb = success ? after : 0, Success = success, ErrorCode = error, PostProcessWarning = warning,
        WarningCodes = warnings ?? Array.Empty<ReportWarningCode>(), RedirectedToLaneName = redirectedTo, RedirectedToLaneId = redirectedTo, ArrStatus = arr,
        StartTime = Ended.AddSeconds(-seconds), EndTime = Ended
    };

    private static string Html(params LaneReportSection[] lanes) =>
        new HtmlReportGenerator().Generate(new ReportModel { GeneratedAt = Ended, RunTime = TimeSpan.FromMinutes(30), Lanes = lanes });

    [Fact]
    public void EveryFileRow_IsReadBack_WithItsLane_Type_Tool_Profile_Sizes_Time_AndStatus()
    {
        var html = Html(
            new LaneReportSection { LaneDisplayName = "Movies & TV", Results = new[]
            {
                File("Fight Club (1999).mkv", "Movies & TV", arr: "Unmonitored"),
                File("Caddyshack (1980).mkv", "Movies & TV", label: "ffmpeg (Dolby Vision dropped)", preset: "FF 720"),
                File("Copy (2001).mkv", "Movies & TV", passthrough: true, preset: null)
            } },
            new LaneReportSection { LaneDisplayName = "Kids", Results = new[] { File("Show - S01E01.mkv", "Kids", "TV Show", label: "HandBrake (Dolby Vision fallback)") } },
            new LaneReportSection { LaneDisplayName = "Empty", Results = Array.Empty<ConversionResult>() });

        var files = HtmlReportParser.Parse(html);

        Assert.Equal(4, files.Count);
        var first = files[0];
        Assert.Equal(("Movies & TV", "Fight Club (1999).mkv", "Movie", "HB 1080", "handbrake"), (first.Lane, first.Name, first.ContentType, first.Preset, first.Engine));
        Assert.Equal((2.5, 1.25, 3725.0, true, null), (first.BeforeGb, first.AfterGb, first.Seconds, first.Success, first.ErrorCode));
        Assert.Equal("Unmonitored", first.ArrStatus);
        Assert.Equal(("ffmpeg", "FF 720"), (files[1].Engine, files[1].Preset));
        Assert.True(files[2].Passthrough);
        Assert.Equal(("passthrough", (string?)null), (files[2].Engine, files[2].Preset));
        Assert.Equal(("handbrake", "Kids", "TV Show"), (files[3].Engine, files[3].Lane, files[3].ContentType));   // a Dolby Vision fallback is HandBrake
    }

    [Fact]
    public void FailuresWarningsAndRedirects_AreReadBack_AndOnlyConvertedFilesCount()
    {
        var html = Html(new LaneReportSection { LaneDisplayName = "Movies", Results = new[]
        {
            File("failed.mkv", "Movies", success: false, error: ReportErrorCode.EncodeFailed),
            File("not moved.mkv", "Movies", success: false, error: ReportErrorCode.MoveDestinationUnavailable),
            File("warned.mkv", "Movies", warning: "companion move failed", warnings: new[] { ReportWarningCode.CompanionFilesNotMoved }),
            File("redirected.mkv", "Movies", redirectedTo: "Kids")
        } });

        var files = HtmlReportParser.Parse(html);

        Assert.False(files[0].Success);
        Assert.Equal((int)ReportErrorCode.EncodeFailed, files[0].ErrorCode);
        Assert.False(files[0].Counted);                                    // nothing was produced
        Assert.Equal((int)ReportErrorCode.MoveDestinationUnavailable, files[1].ErrorCode);
        Assert.True(files[1].Counted);                                     // converted but not moved: the history counts it
        Assert.Equal(new[] { (int)ReportWarningCode.CompanionFilesNotMoved }, files[2].WarningCodes);
        Assert.Contains("companion move failed", files[2].WarningText);
        Assert.Equal(("Kids", "Movies"), (files[3].RedirectedToLane, files[3].HomeLane));
    }

    [Fact]
    public void TheHistoryTableAndEverythingElseInTheReport_IsNotMistakenForFiles()
    {
        var html = new HtmlReportGenerator().Generate(new ReportModel
        {
            GeneratedAt = Ended, RunTime = TimeSpan.FromMinutes(1), Today = new HistoryRollup(3, 10, 4, 100), ThisMonth = new HistoryRollup(30, 100, 40, 1000), ThisYear = new HistoryRollup(300, 1000, 400, 10000),
            Lanes = new[] { new LaneReportSection { LaneDisplayName = "Movies", Results = new[] { File("one.mkv", "Movies") } } }
        });

        Assert.Single(HtmlReportParser.Parse(html));
        Assert.Empty(HtmlReportParser.Parse("<html><body>nothing here</body></html>"));
    }
}
