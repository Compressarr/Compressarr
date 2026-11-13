using Compressarr.Core.Conversion;
using Compressarr.Core.Reporting;

namespace Compressarr.Core.Tests.Reporting;

public class ReportRedactorTests
{
    private static ReportModel Model(params ConversionResult[] results) => new()
    {
        GeneratedAt = new DateTime(2026, 11, 13, 9, 0, 0),
        RunTime = TimeSpan.FromMinutes(42),
        RunNumber = 7,
        SummaryLogFilePath = @"C:\Users\someone\AppData\Roaming\Compressarr\Logs\run.log",
        Lanes = new[] { new LaneReportSection { LaneDisplayName = "Kids", Results = results } }
    };

    private static ConversionResult Result(bool success = true, string? failure = null, string? warning = null, string? arr = null, ReportErrorCode? code = null) => new()
    {
        LaneId = "kids", HomeLaneName = "Kids", FileName = "Home Alone (1990).mkv", FullName = @"D:\Private\Home Alone (1990).mkv", NewFileName = "Home Alone (1990).mkv",
        ContentType = "Movie", PresetName = "HB 1080", BeginSizeGb = 4, EndSizeGb = 1.5, Success = success,
        DetailLogFile = @"C:\logs\HBdetails.txt", StartTime = new DateTime(2026, 11, 13, 8, 0, 0), EndTime = new DateTime(2026, 11, 13, 8, 30, 0),
        FailureReason = failure, PostProcessWarning = warning, ArrStatus = arr, ErrorCode = code, AvgFps = 55
    };

    [Fact]
    public void EveryNamePathAndMessage_BecomesRemoved()
    {
        var redacted = ReportRedactor.Redact(Model(Result(success: false, failure: @"No space on D:\Private", arr: "Radarr: Home Alone", code: ReportErrorCode.MoveFailedOther)));

        var r = Assert.Single(redacted.Lanes[0].Results);
        Assert.Equal("Removed", r.FileName);
        Assert.Equal("Removed", r.FullName);
        Assert.Null(r.NewFileName);
        Assert.Null(r.DetailLogFile);
        Assert.Equal("Removed", r.FailureReason);
        Assert.Equal("Removed", r.ArrStatus);
        Assert.Null(redacted.SummaryLogFilePath);
    }

    [Fact]
    public void CountsSizesCodesAndProfiles_Survive()
    {
        var original = Model(Result(success: false, failure: "x", code: ReportErrorCode.MoveFailedOther), Result());

        var redacted = ReportRedactor.Redact(original);

        Assert.Equal(original.TotalFiles, redacted.TotalFiles);
        Assert.Equal(original.ErrorCount, redacted.ErrorCount);
        Assert.Equal(original.TotalBeforeGb, redacted.TotalBeforeGb);
        Assert.Equal(original.TotalAfterGb, redacted.TotalAfterGb);
        Assert.Equal(7, redacted.RunNumber);
        Assert.Equal("HB 1080", redacted.Lanes[0].Results[0].PresetName);
        Assert.Equal(ReportErrorCode.MoveFailedOther, redacted.Lanes[0].Results[0].ErrorCode);
        Assert.Equal(55, redacted.Lanes[0].Results[0].AvgFps);
    }

    [Fact]
    public void AMissingMessage_StaysMissing_NotInventedAsRemoved()
    {
        var r = Assert.Single(ReportRedactor.Redact(Model(Result())).Lanes[0].Results);

        Assert.Null(r.FailureReason);
        Assert.Null(r.PostProcessWarning);
        Assert.Null(r.ArrStatus);
    }

    [Fact]
    public void TheOriginalReport_IsNeverChanged()
    {
        var original = Model(Result(warning: "companion failed for Home Alone"));

        _ = ReportRedactor.Redact(original);

        var r = Assert.Single(original.Lanes[0].Results);
        Assert.Equal("Home Alone (1990).mkv", r.FileName);
        Assert.Equal("companion failed for Home Alone", r.PostProcessWarning);
        Assert.NotNull(original.SummaryLogFilePath);
    }

    [Fact]
    public void TheGeneratedPage_HasRoomForTheToolbar_AndHidesTheCloseButtonWhenPrinted()
    {
        var html = new HtmlReportGenerator().Generate(Model(Result()));

        Assert.Contains(HtmlReportGenerator.ToolbarMarker, html);
        Assert.Contains("@media print", html);
    }
}
