using System.Text.Json.Nodes;
using Compressarr.Core.Config;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Web.Tests;

/// <summary>/api/history/runs: the compact per-run list behind the History page's stat cards and charts.</summary>
public class HistoryRunsEndpointTests
{
    private static readonly LaneSpec Lane1 = new("hdsd", "SD-HD");

    private const string Header = "yyyy,mm,dd,BegSize,EndSize,FileCount,ProcessHours,ProcessMinutes,ProcessSeconds,RunNumber,ReportFileName,ErrorCount,WarningCount,RedirectCount,MovieCount,TvCount";

    private static async Task<JsonNode> Runs(QueueHost host) => JsonNode.Parse(await host.Client.GetStringAsync("/api/history/runs"))!;

    private static (long Id, Compressarr.Core.Reporting.ReportModel Model) RecordARun(QueueHost host, int number = 1)
    {
        var archive = host.Services.GetRequiredService<Compressarr.Core.Logging.IRunArchive>();
        var now = DateTime.Now;
        var model = new Compressarr.Core.Reporting.ReportModel
        {
            GeneratedAt = now, RunTime = TimeSpan.FromMinutes(5), RunNumber = number,
            Lanes = new[]
            {
                new Compressarr.Core.Reporting.LaneReportSection
                {
                    LaneDisplayName = "SD-HD",
                    Results = new[]
                    {
                        new Compressarr.Core.Conversion.ConversionResult
                        {
                            LaneId = "hdsd", HomeLaneName = "SD-HD", FileName = "Fight Club (1999).mkv", FullName = "Fight Club (1999).mkv", ContentType = "Movie",
                            PresetName = "HB 1080", BeginSizeGb = 4, EndSizeGb = 1.5, Success = true, StartTime = now.AddMinutes(-5), EndTime = now
                        }
                    }
                }
            }
        };
        var id = archive.RecordRun(new Compressarr.Core.Logging.RunHistoryRecord(now.Year, now.Month, now.Day, 4, 1.5, 1, 0, 5, 0, RunNumber: number));
        archive.SaveReport(id, model);
        return (id, model);
    }

    [Fact]
    public async Task AReportIsBuiltFromTheArchiveWhenItsIdIsOpened_AndCanBeDownloadedAsAFile()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var (id, _) = RecordARun(host);

        using var page = await host.Client.GetAsync($"/api/reports/{id}");
        using var download = await host.Client.GetAsync($"/api/reports/{id}?download=1");
        using var unknown = await host.Client.GetAsync("/api/reports/987654");

        Assert.Equal(System.Net.HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType!.MediaType);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("Fight Club (1999).mkv", html);
        Assert.Contains("Run #1:", html);
        Assert.Contains("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.EndsWith("_Report.html", download.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task ARunsReportLink_IsItsId_InTheRunsListAndTheReportsList()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var (id, _) = RecordARun(host);

        var runs = (await Runs(host))["runs"]!.AsArray();
        var reports = JsonNode.Parse(await host.Client.GetStringAsync("/api/history/reports"))!.AsArray();

        Assert.Equal(id.ToString(), runs[0]![13]!.GetValue<string>());
        Assert.Equal(id.ToString(), reports[0]!["reportFileName"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheSidebarBadges_AreCountedInOneQuery_AndOnlyForRunsNotYetViewed()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var archive = host.Services.GetRequiredService<Compressarr.Core.Logging.IRunArchive>();
        var now = DateTime.Now;
        var empty = new Compressarr.Core.Reporting.ReportModel { GeneratedAt = now, RunTime = TimeSpan.FromMinutes(1), Lanes = Array.Empty<Compressarr.Core.Reporting.LaneReportSection>() };
        foreach (var (number, errors, warnings) in new[] { (1, 1, 0), (2, 0, 2), (3, 2, 1), (4, 0, 0) })
            archive.SaveReport(archive.RecordRun(new Compressarr.Core.Logging.RunHistoryRecord(now.Year, now.Month, now.Day, 4, 1, 2, 0, 1, 0, RunNumber: number, ErrorCount: errors, WarningCount: warnings)), empty);

        var all = JsonNode.Parse(await host.Client.GetStringAsync("/api/history/badges"))!;
        (await host.Client.PostAsync("/api/history/mark-viewed", null)).EnsureSuccessStatusCode();     // the run counter is still 0 here, so nothing is "viewed" yet
        var afterView = JsonNode.Parse(await host.Client.GetStringAsync("/api/history/badges"))!;

        Assert.Equal((2, 2, 4), (all["errorRuns"]!.GetValue<int>(), all["warningRuns"]!.GetValue<int>(), all["unseenRuns"]!.GetValue<int>()));    // runs 1 and 3 had errors, 2 and 3 warnings, 4 runs are new
        Assert.Equal((2, 2, 4), (afterView["errorRuns"]!.GetValue<int>(), afterView["warningRuns"]!.GetValue<int>(), afterView["unseenRuns"]!.GetValue<int>()));
    }

    [Fact]
    public async Task TheReportsList_FollowsThePeriodButtons_WithTheSameWindowsAsTheOverview_AndReportsTheTotal()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var archive = host.Services.GetRequiredService<Compressarr.Core.Logging.IRunArchive>();
        var empty = new Compressarr.Core.Reporting.ReportModel { GeneratedAt = DateTime.Now, RunTime = TimeSpan.FromMinutes(1), Lanes = Array.Empty<Compressarr.Core.Reporting.LaneReportSection>() };
        var number = 0;
        foreach (var daysAgo in new[] { 400, 100, 20, 6, 7, 0, 0 })      // 6 days ago is inside Last 7 days, 7 days ago is not; 365+ days is beyond the year
        {
            var d = DateTime.Now.Date.AddDays(-daysAgo);
            archive.SaveReport(archive.RecordRun(new Compressarr.Core.Logging.RunHistoryRecord(d.Year, d.Month, d.Day, 4, 1, 2, 0, 1, 0, RunNumber: ++number)), empty);
        }

        async Task<(int Shown, int Total)> Period(string period)
        {
            using var response = await host.Client.GetAsync($"/api/history/reports?period={period}");
            var shown = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray().Count;
            return (shown, int.Parse(response.Headers.GetValues("X-Total-Count").Single()));
        }

        Assert.Equal((2, 2), await Period("today"));
        Assert.Equal((3, 3), await Period("d7"));        // today twice and 6 days ago
        Assert.Equal((5, 5), await Period("d30"));       // plus 7 and 20 days ago
        Assert.Equal((6, 6), await Period("year"));      // plus 100 days ago
        Assert.Equal((7, 7), await Period("all"));
        Assert.Equal(5, JsonNode.Parse(await host.Client.GetStringAsync("/api/history/reports"))!.AsArray().Count);   // no period: the retention window (30 days), as before
    }

    [Fact]
    public async Task TheReportsList_IsPaged_TwentyFiveRowsToStart_WithTheTotalAndThePageReturned()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var archive = host.Services.GetRequiredService<Compressarr.Core.Logging.IRunArchive>();
        var now = DateTime.Now;
        var empty = new Compressarr.Core.Reporting.ReportModel { GeneratedAt = now, RunTime = TimeSpan.FromMinutes(1), Lanes = Array.Empty<Compressarr.Core.Reporting.LaneReportSection>() };
        for (var n = 1; n <= 30; n++)
            archive.SaveReport(archive.RecordRun(new Compressarr.Core.Logging.RunHistoryRecord(now.Year, now.Month, now.Day, 4, 1, 2, 0, 1, 0, RunNumber: n)), empty);

        async Task<(int[] Runs, int Total, int Page)> Get(string query)
        {
            using var response = await host.Client.GetAsync("/api/history/reports?period=all" + query);
            var runs = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray().Select(e => e!["runNumber"]!.GetValue<int>()).ToArray();
            return (runs, int.Parse(response.Headers.GetValues("X-Total-Count").Single()), int.Parse(response.Headers.GetValues("X-Page").Single()));
        }

        var first = await Get("");
        var second = await Get("&page=2");
        var big = await Get("&pageSize=100");
        var beyond = await Get("&page=99&pageSize=25");

        Assert.Equal((25, 30, 1), (first.Runs.Length, first.Total, first.Page));
        Assert.Equal(new[] { 30, 29, 28 }, first.Runs.Take(3).ToArray());                // newest first
        Assert.Equal(new[] { 5, 4, 3, 2, 1 }, second.Runs);
        Assert.Equal((30, 30), (big.Runs.Length, big.Total));
        Assert.Equal(2, beyond.Page);                                                      // far past the end gives the last page
        Assert.Equal(new[] { 5, 4, 3, 2, 1 }, beyond.Runs);
    }

    [Fact]
    public async Task TheProblemsLists_ArrangeTheUnseenErrorsAndWarnings_AndTheRecentOnesSeenOrNot()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var archive = host.Services.GetRequiredService<Compressarr.Core.Logging.IRunArchive>();
        var store = host.Services.GetRequiredService<Compressarr.Core.Logging.IRunHistoryStore>();
        var now = DateTime.Now;
        var empty = new Compressarr.Core.Reporting.ReportModel { GeneratedAt = now, RunTime = TimeSpan.FromMinutes(1), Lanes = Array.Empty<Compressarr.Core.Reporting.LaneReportSection>() };
        void Run(int number, int errors, int warnings)
        {
            archive.SaveReport(archive.RecordRun(new Compressarr.Core.Logging.RunHistoryRecord(now.Year, now.Month, now.Day, 4, 1, 2, 0, 1, 0, RunNumber: number, ErrorCount: errors, WarningCount: warnings)), empty);
            store.IncrementRunCount(AppPaths.GetRunCountFilePath());
        }
        async Task<int[]> Problems(string scope) =>
            JsonNode.Parse(await host.Client.GetStringAsync("/api/history/problems?scope=" + scope))!.AsArray().Select(e => e!["runNumber"]!.GetValue<int>()).ToArray();

        Run(1, 1, 0); Run(2, 0, 0); Run(3, 0, 2);                  // an error, a clean run, a warning
        Assert.Equal(new[] { 3, 1 }, await Problems("unseen"));     // nothing has been viewed yet

        (await host.Client.PostAsync("/api/history/mark-viewed", null)).EnsureSuccessStatusCode();
        Run(4, 0, 0); Run(5, 2, 1);                                 // a clean run and a run with an error and a warning, after viewing

        Assert.Equal(new[] { 5 }, await Problems("unseen"));        // only what is new since the last view
        Assert.Equal(new[] { 5, 3, 1 }, await Problems("recent"));  // the recent ones, seen or not, newest first
    }

    [Fact]
    public async Task ExportCsv_DownloadsTheWholeHistoryInTheOldCsvLayout()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        RecordARun(host, 1);
        RecordARun(host, 2);

        using var response = await host.Client.GetAsync("/api/history/export.csv");
        var text = await response.Content.ReadAsStringAsync();
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("Compressarr_History_", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        Assert.Equal(3, lines.Length);                                                    // the header and two runs
        Assert.StartsWith("yyyy,mm,dd,BegSize", lines[0]);
        Assert.Equal(22, lines[0].Split(',').Length);
    }

    [Fact]
    public async Task ASavedReportFileInTheReportFolder_IsStillServedByName()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var folder = Path.Combine(AppPaths.GetAppDataDirectory(), "Reports");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Compressarr_2026-10-24_07-09-21_Report.html"), "<html>an old copy</html>");

        var body = await host.Client.GetStringAsync("/api/reports/Compressarr_2026-10-24_07-09-21_Report.html");

        Assert.Equal("<html>an old copy</html>", body);
    }

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
    public async Task OnlyARunWhoseReportStillExistedAtTheImport_GetsAReportLink_SoThePageNeverLinksToAMissingReport()
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

        // The first start imports the history and keeps the report files that were still there (as the run's report, linked by its id).
        Assert.Null(runs[0]![13]);
        Assert.Equal("2", runs[1]![13]!.GetValue<string>());
        using var report = await host.Client.GetAsync("/api/reports/2");
        Assert.Equal("<html></html>", await report.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ARunsEndMinute_IsItsReportsStartPlusItsEncodingTime_AndNullWhenThereIsNoTimestampToReadItFrom()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        File.WriteAllLines(AppPaths.GetHistoryFilePath(), new[]
        {
            Header,
            "2026,10,24,3,1,2,0,23,0,1,Compressarr_2026-10-24_07-09-21_Report.html,0,0,0,0,2",   // starts 07:09:21, 23 minutes -> ends 07:32
            "2026,10,24,3,1,1,1,30,0,2,Compressarr_2026-10-23_23-00-00_Report.html,0,0,0,0,1",   // starts before midnight, ends 00:30 the next day
            "2026,10,24,3,1,1,0,5,0,3,r3.html,0,0,0,,"                                           // not a timestamped name: unknown
        });

        var body = await Runs(host);
        var runs = body["runs"]!.AsArray();

        Assert.Equal(7 * 60 + 32, runs[0]![14]!.GetValue<int>());
        Assert.Equal(30, runs[1]![14]!.GetValue<int>());
        Assert.Null(runs[2]![14]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$", body["now"]!.GetValue<string>());
    }

    [Fact]
    public async Task EachRunCarriesItsSpeedEnginesLanesAndProfiles_AfterTheEarlierValues_AndNullWhenARunPredatesThem()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        File.WriteAllLines(AppPaths.GetHistoryFilePath(), new[]
        {
            Header,
            "2026,10,23,3,1,2,0,5,0,1,Compressarr_2026-10-23_08-00-00_Report.html,0,0,0,1,1",                                                    // an older row: no new columns
            "2026,10,24,3,1,5,0,5,0,2,Compressarr_2026-10-24_08-00-00_Report.html,0,0,0,4,1,23.45,3,1,1,TV:3|Movies%2C HD:2,HB 1080:3|FF 720:1"
        });

        var runs = (await Runs(host))["runs"]!.AsArray();

        var old = runs[0]!.AsArray();
        Assert.Equal(21, old.Count);
        Assert.All(old.Skip(15), v => Assert.Null(v));
        var row = runs[1]!.AsArray();
        Assert.Equal(23.45, row[15]!.GetValue<double>());
        Assert.Equal((3, 1, 1), (row[16]!.GetValue<int>(), row[17]!.GetValue<int>(), row[18]!.GetValue<int>()));
        Assert.Equal("TV", row[19]![0]![0]!.GetValue<string>());                          // biggest first, as [name, files]
        Assert.Equal(3, row[19]![0]![1]!.GetValue<int>());
        Assert.Equal("Movies, HD", row[19]![1]![0]!.GetValue<string>());                  // the escaped comma comes back as a comma
        Assert.Equal("HB 1080", row[20]![0]![0]!.GetValue<string>());
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
