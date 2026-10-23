using System.Text.Json.Nodes;
using Compressarr.Core.Config;

namespace Compressarr.Web.Tests;

/// <summary>/api/history/runs: the compact per-run list behind the History page's stat cards and charts.</summary>
public class HistoryRunsEndpointTests
{
    private static readonly LaneSpec Lane1 = new("hdsd", "SD-HD");

    private const string Header = "yyyy,mm,dd,BegSize,EndSize,FileCount,ProcessHours,ProcessMinutes,ProcessSeconds,RunNumber,ReportFileName,ErrorCount,WarningCount,RedirectCount,MovieCount,TvCount";

    private static async Task<JsonNode> Runs(QueueHost host) => JsonNode.Parse(await host.Client.GetStringAsync("/api/history/runs"))!;

    [Fact]
    public async Task NoHistory_IsAnEmptyList_AndTheServersDate()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        var body = await Runs(host);

        Assert.Empty(body["runs"]!.AsArray());
        Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd"), body["today"]!.GetValue<string>());
    }

    [Fact]
    public async Task EveryRunComesBackOldestFirst_WithItsNumbers_AndTheSplitOrNullWhenUnknown()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        File.WriteAllLines(AppPaths.GetHistoryFilePath(), new[]
        {
            Header,
            "2026,09,09,10,4,3,1,2,3,1,r1.html,0,0,0,1,2",     // a known split
            "2026,09,11,8,3,2,0,2,0,3,r3.html,0,1,0,,",        // split unknown
            "2026,09,12,6.123456,2,1,0,0,30,4,r4.html,1,0,2,,"
        });

        var runs = (await Runs(host))["runs"]!.AsArray();

        Assert.Equal(new[] { 1, 3, 4 }, runs.Select(r => r![7]!.GetValue<int>()).ToArray());
        var first = runs[0]!.AsArray();
        Assert.Equal(new[] { 2026, 9, 9 }, first.Take(3).Select(v => v!.GetValue<int>()).ToArray());
        Assert.Equal((10.0, 4.0, 3, 3723), (first[3]!.GetValue<double>(), first[4]!.GetValue<double>(), first[5]!.GetValue<int>(), first[6]!.GetValue<int>()));   // 1h 2m 3s
        Assert.Equal((1, 2), (first[11]!.GetValue<int>(), first[12]!.GetValue<int>()));
        Assert.Null(runs[1]![11]);                                                    // unknown, not 0
        Assert.Null(runs[1]![12]);
        Assert.Equal((1, 0, 2), (runs[2]![8]!.GetValue<int>(), runs[2]![9]!.GetValue<int>(), runs[2]![10]!.GetValue<int>()));    // errors, warnings, redirected
        Assert.Equal(6.123, runs[2]![3]!.GetValue<double>());                          // sizes are rounded to 3 places
    }

    [Fact]
    public async Task ARunInWhichEveryFileFailedAndNothingWasProduced_IsLeftOut_LikeEveryPeriodTotal()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        File.WriteAllLines(AppPaths.GetHistoryFilePath(), new[]
        {
            Header,
            "2026,09,10,5,0,3,0,1,0,2,r2.html,3,0,0,,",          // 3 files, 3 errors, nothing produced
            "2026,09,11,8,3,3,0,2,0,3,r3.html,1,0,0,,"           // a mixed run: kept
        });

        var runs = (await Runs(host))["runs"]!.AsArray();

        Assert.Equal(3, Assert.Single(runs)![7]!.GetValue<int>());
    }

    [Fact]
    public async Task OnlyARunWhoseReportStillExists_GetsItsReportName_SoThePageNeverLinksToAMissingFile()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var reports = Path.Combine(AppPaths.GetAppDataDirectory(), "Reports");
        Directory.CreateDirectory(reports);
        File.WriteAllText(Path.Combine(reports, "r3.html"), "<html></html>");
        File.WriteAllLines(AppPaths.GetHistoryFilePath(), new[]
        {
            Header,
            "2026,09,09,10,4,3,1,2,3,1,r1.html,0,0,0,1,2",     // its report is gone
            "2026,09,11,8,3,2,0,2,0,3,r3.html,0,0,0,1,1"
        });

        var runs = (await Runs(host))["runs"]!.AsArray();

        Assert.Null(runs[0]![13]);
        Assert.Equal("r3.html", runs[1]![13]!.GetValue<string>());
    }

    [Fact]
    public async Task TheRollupsCarryTheSplitToo_SoTheTableAndTheCardsAgree()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var today = DateTime.Now;
        File.WriteAllLines(AppPaths.GetHistoryFilePath(), new[]
        {
            Header,
            $"{today.Year},{today.Month:00},{today.Day:00},10,4,5,0,5,0,1,r1.html,0,0,0,2,3",
            $"{today.Year},{today.Month:00},{today.Day:00},10,4,4,0,5,0,2,r2.html,0,0,0,,"
        });

        var history = JsonNode.Parse(await host.Client.GetStringAsync("/api/history"))!;
        var all = history["allTime"]!;

        Assert.Equal(9, all["fileCount"]!.GetValue<int>());
        Assert.Equal((2, 3, 4), (all["movieCount"]!.GetValue<int>(), all["tvCount"]!.GetValue<int>(), all["unknownCount"]!.GetValue<int>()));
    }
}
