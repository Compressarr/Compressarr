using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Notifications;
using Compressarr.Core.Reporting;

namespace Compressarr.Core.Tests.Notifications;

public class NotificationMessageRendererTests
{
    private static ReportModel SampleReport() => new()
    {
        GeneratedAt = new DateTime(2026, 9, 11),
        RunTime = TimeSpan.FromMinutes(72), // 1h 12m
        RunNumber = 47,
        RetriesSucceeded = 2,
        Today = new HistoryRollup(15, 20.0, 9.9),
        ThisMonth = new HistoryRollup(212, 300.0, 156.2),
        ThisYear = new HistoryRollup(1904, 2500.0, 1292.5),
        Lanes = new[]
        {
            new LaneReportSection
            {
                LaneDisplayName = "Movies",
                Results = new[]
                {
                    Result(26.7, 18.3, success: true, warning: "companion move failed"),
                }
            }
        }
    };

    private static ConversionResult Result(double beforeGb, double afterGb, bool success, string? warning = null, string fileName = "file.mkv") => new()
    {
        LaneId = "lane1",
        FileName = fileName,
        FullName = $@"C:\{fileName}",
        ContentType = "movie",
        BeginSizeGb = beforeGb,
        EndSizeGb = afterGb,
        Success = success,
        PostProcessWarning = warning
    };

    [Fact]
    public void Render_Standard_SubstitutesAllTokensUsed()
    {
        var report = SampleReport();
        var rendered = NotificationMessageRenderer.Render(
            NotificationMessagePresets.Templates[NotificationMessageStyle.Standard].Body,
            NotificationOutcome.Success, report, @"C:\reports\r.html");

        Assert.Equal("1 file(s) processed, 8.4 GB saved (31.46%) in 1h 12m.", rendered);
    }

    [Fact]
    public void Render_Detailed_IncludesErrorAndWarningCounts()
    {
        var report = SampleReport();
        var rendered = NotificationMessageRenderer.Render(
            NotificationMessagePresets.Templates[NotificationMessageStyle.Detailed].Body,
            NotificationOutcome.Warning, report, @"C:\reports\r.html");

        Assert.Contains("Errors: 0 - Warnings: 1", rendered);
        Assert.Contains("26.7 GB -> 18.3 GB", rendered);
    }

    [Fact]
    public void Render_Outcome_UsesFormattedOutcomeText()
    {
        var report = SampleReport();
        var rendered = NotificationMessageRenderer.Render("{outcome}", NotificationOutcome.Error, report, "");
        Assert.Equal("Error", rendered);
    }

    [Fact]
    public void Render_HistoricalRollupTokens_UseTheirOwnRollupNotThisRun()
    {
        var report = SampleReport();
        var rendered = NotificationMessageRenderer.Render(
            "{today_files}/{today_saved_gb} {month_files}/{month_saved_gb} {year_files}/{year_saved_gb}",
            NotificationOutcome.Success, report, "");

        Assert.Equal("15/10.1 212/143.8 1904/1207.5", rendered);
    }

    [Fact]
    public void Render_MissingRollup_TreatsAsZeroRatherThanThrowing()
    {
        var report = SampleReport();
        report = new ReportModel
        {
            GeneratedAt = report.GeneratedAt,
            RunTime = report.RunTime,
            RunNumber = report.RunNumber,
            Lanes = report.Lanes,
            Today = null
        };

        var rendered = NotificationMessageRenderer.Render("{today_files}/{today_saved_gb}", NotificationOutcome.Success, report, "");
        Assert.Equal("0/0", rendered);
    }

    [Fact]
    public void Render_UnknownToken_LeftAsLiteralText()
    {
        var report = SampleReport();
        var rendered = NotificationMessageRenderer.Render("Run {run_number}: {not_a_real_token}", NotificationOutcome.Success, report, "");
        Assert.Equal("Run 47: {not_a_real_token}", rendered);
    }

    [Fact]
    public void Render_ReportPathToken_UsesPassedInPath()
    {
        var report = SampleReport();
        var rendered = NotificationMessageRenderer.Render("{report_path}", NotificationOutcome.Success, report, @"C:\Compressarr\Reports\r.html");
        Assert.Equal(@"C:\Compressarr\Reports\r.html", rendered);
    }

    [Fact]
    public void Render_FileListToken_JoinsFileNamesAcrossAllLanes()
    {
        var report = new ReportModel
        {
            GeneratedAt = new DateTime(2026, 9, 11),
            RunTime = TimeSpan.Zero,
            Lanes = new[]
            {
                new LaneReportSection { LaneDisplayName = "Movies", Results = new[] { Result(10, 5, success: true, fileName: "a.mkv") } },
                new LaneReportSection { LaneDisplayName = "TV", Results = new[] { Result(10, 5, success: true, fileName: "b.mkv") } },
            }
        };

        var rendered = NotificationMessageRenderer.Render("{file_list}", NotificationOutcome.Success, report, "");

        Assert.Equal("a.mkv\nb.mkv", rendered);
    }

    private static ReportModel ReportWith(IEnumerable<ConversionResult> results, params ReportErrorCode[] laneProblems) => new()
    {
        GeneratedAt = new DateTime(2026, 9, 11),
        RunTime = TimeSpan.Zero,
        Lanes = new[] { new LaneReportSection { LaneDisplayName = "Movies", Results = results.ToList(), LaneProblems = laneProblems } }
    };

    private static ConversionResult Failed(ReportErrorCode? code, string fileName = "f.mkv") => new()
    {
        LaneId = "lane1", FileName = fileName, FullName = @"C:\" + fileName, ContentType = "movie", BeginSizeGb = 1, EndSizeGb = 0, Success = false, ErrorCode = code
    };

    private static string Body(string template, ReportModel report) => NotificationMessageRenderer.Render(template, NotificationOutcome.Error, report, "");

    [Fact]
    public void ErrorToken_ARunWithNoErrors_IsNothing_AndItsLineIsLeftOut()
    {
        var clean = ReportWith(new[] { Result(10, 5, success: true) });

        Assert.Equal("1 file(s)\nDone", Body("{files} file(s)\nProblem: {error}\nDone", clean));
        Assert.Equal("1 file(s)", Body("{files} file(s)\n{error}", clean));
    }

    [Fact]
    public void ErrorToken_AOneLineTemplate_JustLosesTheToken_SoTheMessageIsNeverEmptied()
    {
        var clean = ReportWith(new[] { Result(10, 5, success: true) });

        Assert.Equal("Run done", Body("Run done {error}", clean));
        Assert.Equal("", Body("{error}", clean));
    }

    [Fact]
    public void ErrorToken_OneError_ReadsLikeTheReport()
    {
        var report = ReportWith(new[] { Failed(ReportErrorCode.MoveDestinationUnavailable) });

        Assert.Equal("Error 102: File move failure", Body("{error}", report));
        Assert.Equal("Problem: Error 102: File move failure", Body("Problem: {error}", report));
    }

    [Fact]
    public void ErrorToken_TheSameErrorOnSeveralFiles_AddsACount()
    {
        var report = ReportWith(new[] { Failed(ReportErrorCode.MoveDestinationUnavailable, "a.mkv"), Failed(ReportErrorCode.MoveDestinationUnavailable, "b.mkv"), Failed(ReportErrorCode.MoveDestinationUnavailable, "c.mkv") });

        Assert.Equal("Error 102: File move failure (x3)", Body("{error}", report));
    }

    [Fact]
    public void ErrorToken_SeveralKinds_ShowTheFirstTwoInCodeOrder_ThenHowManyMore()
    {
        var report = ReportWith(new[] { Failed(ReportErrorCode.MoveDestinationUnavailable), Failed(ReportErrorCode.EncodeFailed), Failed(ReportErrorCode.ProfileCannotKeepHdr), Failed(null) });

        Assert.Equal("Error 101: Encode failed; Error 102: File move failure (+2 more)", Body("{error}", report));
        Assert.Equal("Error 101: Encode failed; Error 102: File move failure", Body("{error}", ReportWith(new[] { Failed(ReportErrorCode.MoveDestinationUnavailable), Failed(ReportErrorCode.EncodeFailed) })));
    }

    [Fact]
    public void ErrorToken_AFailureWithNoCode_AndALaneThatCouldNotRun_AreBothCounted()
    {
        Assert.Equal("Error: see the report", Body("{error}", ReportWith(new[] { Failed(null) })));
        Assert.Equal("Error 113: Encoder not found", Body("{error}", ReportWith(Array.Empty<ConversionResult>(), ReportErrorCode.LaneEncoderNotAvailable)));
    }

    [Fact]
    public void ErrorToken_ASuccessfulFileWithAWarning_IsNotAnError()
    {
        var report = ReportWith(new[] { Result(10, 5, success: true, warning: "companion move failed") });

        Assert.Equal("", Body("{error}", report));
    }

    [Fact]
    public void ErrorToken_IsNotTheErrorCountToken()
    {
        var report = ReportWith(new[] { Failed(ReportErrorCode.EncodeFailed) });

        Assert.Equal("1 / Error 101: Encode failed", Body("{error_count} / {error}", report));
    }

    [Fact]
    public void ErrorToken_EveryCodeHasAShortLabel_AndNoBuiltInStyleUsesTheToken()
    {
        foreach (var code in Enum.GetValues<ReportErrorCode>())
        {
            var label = code.ShortLabel();
            Assert.NotEqual("Error", label);
            Assert.True(label.Length <= 32, label);
        }

        foreach (var (title, body) in NotificationMessagePresets.Templates.Values)
        {
            Assert.DoesNotContain("{error}", title);
            Assert.DoesNotContain("{error}", body);
        }
    }

    [Fact]
    public void Render_FileListToken_NotReferencedByAnyBuiltInPreset()
    {
        // {file_list} is the one token that can put a media filename in front of a third-party
        // service - it must stay opt-in (Custom templates only), never silently picked up by a
        // built-in style.
        foreach (var (title, body) in NotificationMessagePresets.Templates.Values)
        {
            Assert.DoesNotContain("{file_list}", title);
            Assert.DoesNotContain("{file_list}", body);
        }
    }
}
