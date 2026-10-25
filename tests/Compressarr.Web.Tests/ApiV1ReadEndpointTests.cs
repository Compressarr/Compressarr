using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Compressarr.Core.Conversion;
using Compressarr.Core.Logging;
using Compressarr.Core.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Web.Tests;

/// <summary>The read half of the Automation API (<c>/api/v1</c>), and its contract: every response's field names are listed here, so a field cannot be renamed or
/// dropped by accident - that would break somebody's automation. New fields may be added (then they are added here too); a rename needs a /api/v2.</summary>
public class ApiV1ReadEndpointTests
{
    private static readonly LaneSpec Lane1 = new("hdsd", "SD-HD");
    private static readonly LaneSpec Lane2 = new("kids", "Kids");

    private static async Task<(QueueHost Host, string Key)> StartWithKey(params LaneSpec[] lanes)
    {
        var host = await QueueHost.StartAsync(lanes.Length == 0 ? new[] { Lane1 } : lanes);
        var keys = JsonNode.Parse(await host.Client.GetStringAsync("/api/automation/keys"))!["keys"]!.AsArray();
        return (host, keys.Single(k => k!["scope"]!.GetValue<string>() == "read")!["key"]!.GetValue<string>());
    }

    private static async Task<(HttpStatusCode Status, JsonNode? Body, HttpResponseMessage Response)> Get(QueueHost host, string key, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-Api-Key", key);
        var response = await host.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonNode? body = null;
        if (response.Content.Headers.ContentType?.MediaType == "application/json") body = JsonNode.Parse(text);
        return (response.StatusCode, body, response);
    }

    private static string[] Keys(JsonNode? node) => node!.AsObject().Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    private static string[] Sorted(params string[] names) => names.OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static ConversionResult File_(string lane, string name, bool success = true, double before = 4, double after = 1, string? preset = "HB 1080", string? label = null, bool passthrough = false, double? fps = 30) => new()
    {
        LaneId = lane.ToLowerInvariant(), HomeLaneName = lane, FileName = name, FullName = @"C:\In\" + name, ContentType = "Movie", PresetName = preset, EncoderLabel = label,
        IsPassthrough = passthrough, AvgFps = fps, Success = success, BeginSizeGb = before, EndSizeGb = success ? after : 0, StartTime = new DateTime(2026, 10, 24, 8, 0, 0),
        EndTime = new DateTime(2026, 10, 24, 8, 10, 0), FailureReason = success ? null : "boom", ErrorCode = success ? null : ReportErrorCode.EncodeFailed
    };

    /// <summary>Records a run with a stored report; returns its id.</summary>
    private static long AddRun(QueueHost host, int number, DateTime day, string lane = "Movies", int errors = 0, params ConversionResult[] files)
    {
        var archive = host.Services.GetRequiredService<IRunArchive>();
        var counted = files.Where(f => f.Success).ToList();
        var record = new RunHistoryRecord(day.Year, day.Month, day.Day, counted.Sum(f => f.BeginSizeGb), counted.Sum(f => f.EndSizeGb), counted.Count, 0, 10, 0,
            RunNumber: number, ErrorCount: errors, MovieCount: counted.Count, TvCount: 0, AvgFps: 30, HandBrakeCount: counted.Count, FfmpegCount: 0, PassthroughCount: 0,
            LaneCounts: new Dictionary<string, int> { [lane] = counted.Count }, PresetCounts: new Dictionary<string, int> { ["HB 1080"] = counted.Count });
        var id = archive.RecordRun(record);
        archive.SaveReport(id, new ReportModel
        {
            GeneratedAt = day.AddHours(9), RunTime = TimeSpan.FromMinutes(10), RunNumber = number,
            Lanes = new[] { new LaneReportSection { LaneDisplayName = lane, Results = files } }
        });
        return id;
    }

    // ---- the door ----------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/api/v1/status")]
    [InlineData("/api/v1/queue")]
    [InlineData("/api/v1/lanes")]
    [InlineData("/api/v1/schedule")]
    [InlineData("/api/v1/runs")]
    [InlineData("/api/v1/runs/1")]
    [InlineData("/api/v1/stats")]
    [InlineData("/api/v1/reports/1")]
    public async Task EveryReadEndpoint_NeedsAKey_AndAReadKeyIsEnough(string url)
    {
        var (host, key) = await StartWithKey();
        await using var _ = host;

        using var without = await host.Client.GetAsync(url);
        var with = await Get(host, key, url);

        Assert.Equal(HttpStatusCode.Unauthorized, without.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, with.Status);
        Assert.NotEqual(HttpStatusCode.Forbidden, with.Status);
    }

    // ---- status ------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Status_HasTheseFields_WhenIdle()
    {
        var (host, key) = await StartWithKey();
        await using var _ = host;

        var (status, body, _) = await Get(host, key, "/api/v1/status");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Sorted("version", "monitoring", "stopping", "running", "paused", "runStartedAt", "current", "secondsUntilNextRun", "cpuPercent", "schedule", "hold", "queue"), Keys(body));
        Assert.Null(body!["hold"]);
        Assert.Equal(Sorted("enabled", "isDaytime", "priority", "onlyOffHours", "isHeld", "overrideActive", "nextChange"), Keys(body!["schedule"]));
        Assert.Equal(Sorted("pending", "skipped", "errors", "etaState", "eta"), Keys(body["queue"]));
        Assert.False(body["running"]!.GetValue<bool>());
        Assert.Null(body["current"]);
        Assert.Equal("none", body["queue"]!["etaState"]!.GetValue<string>());
        Assert.Equal(0, body["queue"]!["pending"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(body["version"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Status_CountsTheQueue_ByState()
    {
        var (host, key) = await StartWithKey();
        await using var _ = host;
        var a = host.Drop("hdsd", "a.mkv");
        host.Drop("hdsd", "b.mkv");
        host.Drop("hdsd", "c.mkv");
        await host.PollAsync();
        await host.SkipAsync("hdsd", a);

        var (_, body, _) = await Get(host, key, "/api/v1/status");

        Assert.Equal(2, body!["queue"]!["pending"]!.GetValue<int>());
        Assert.Equal(1, body["queue"]!["skipped"]!.GetValue<int>());
        Assert.Equal(0, body["queue"]!["errors"]!.GetValue<int>());
    }

    // ---- queue and lanes ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Queue_HasTheseFields_InProcessingOrder_AndFiltersAndPages()
    {
        var (host, key) = await StartWithKey(Lane1, Lane2);
        await using var _ = host;
        var a = host.Drop("hdsd", "a.mkv");
        host.Drop("hdsd", "b.mkv");
        host.Drop("kids", "k.mkv");
        await host.PollAsync();
        await host.SkipAsync("hdsd", a);

        var (status, all, _) = await Get(host, key, "/api/v1/queue");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Sorted("total", "page", "pageSize", "items"), Keys(all));
        Assert.Equal(3, all!["total"]!.GetValue<int>());
        var items = all["items"]!.AsArray();
        Assert.Equal(Sorted("position", "laneId", "lane", "file", "status", "preset", "engine", "sizeGb", "passthrough", "resumed", "unmatched", "landsInLane"), Keys(items[0]));
        Assert.Equal(new[] { 1, 2, 3 }, items.Select(i => i!["position"]!.GetValue<int>()).ToArray());

        var (_, kids, _) = await Get(host, key, "/api/v1/queue?lane=Kids");
        Assert.Equal("k.mkv", Assert.Single(kids!["items"]!.AsArray())!["file"]!.GetValue<string>());
        Assert.Equal(3, kids["items"]![0]!["position"]!.GetValue<int>());   // its place in the whole queue, not in the filter

        var (_, byId, _) = await Get(host, key, "/api/v1/queue?lane=hdsd&status=skipped");
        Assert.Equal("a.mkv", Assert.Single(byId!["items"]!.AsArray())!["file"]!.GetValue<string>());
        Assert.Equal("skipped", byId["items"]![0]!["status"]!.GetValue<string>());

        var (_, paged, _) = await Get(host, key, "/api/v1/queue?pageSize=2&page=2");
        Assert.Equal((3, 2, 2, 1), (paged!["total"]!.GetValue<int>(), paged["page"]!.GetValue<int>(), paged["pageSize"]!.GetValue<int>(), paged["items"]!.AsArray().Count));

        var (bad, badBody, _) = await Get(host, key, "/api/v1/queue?status=nonsense");
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        Assert.Equal("bad_request", badBody!["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Lanes_HaveTheseFields_AndTheirQueueCounts()
    {
        var (host, key) = await StartWithKey(Lane1, Lane2);
        await using var _ = host;
        host.Drop("hdsd", "a.mkv");
        host.Drop("hdsd", "b.mkv");
        host.Drop("kids", "k.mkv");
        await host.PollAsync();

        var (status, body, _) = await Get(host, key, "/api/v1/lanes");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Sorted("lanes"), Keys(body));
        var lanes = body!["lanes"]!.AsArray();
        Assert.Equal(Sorted("id", "name", "enabled", "engine", "tvPreset", "moviePreset", "queue"), Keys(lanes[0]));
        Assert.Equal(Sorted("pending", "skipped", "errors"), Keys(lanes[0]!["queue"]));
        Assert.Equal(2, lanes.Single(l => l!["id"]!.GetValue<string>() == "hdsd")!["queue"]!["pending"]!.GetValue<int>());
        Assert.Equal(1, lanes.Single(l => l!["id"]!.GetValue<string>() == "kids")!["queue"]!["pending"]!.GetValue<int>());
        Assert.Equal("handbrake", lanes[0]!["engine"]!.GetValue<string>());
    }

    [Fact]
    public async Task Schedule_HasTheseFields()
    {
        var (host, key) = await StartWithKey();
        await using var _ = host;

        var (status, body, _) = await Get(host, key, "/api/v1/schedule");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Sorted("settings", "state"), Keys(body));
        Assert.Equal(Sorted("enabled", "mode", "dayStart", "dayEnd", "weekendDayStart", "weekendDayEnd", "days", "dayPriority", "nightPriority", "onlyEncodeOffHours", "whenDayStarts"), Keys(body!["settings"]));
        Assert.Equal(Sorted("day", "start", "end"), Keys(body["settings"]!["days"]![0]));
        Assert.Equal(7, body["settings"]!["days"]!.AsArray().Count);
        Assert.Equal(Sorted("enabled", "isDaytime", "priority", "onlyOffHours", "isHeld", "overrideActive", "nextChange"), Keys(body["state"]));
    }

    // ---- runs, one run, stats, reports -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Runs_HaveTheseFields_NewestFirst_AndFollowThePeriodAndPaging()
    {
        var (host, key) = await StartWithKey();
        await using var _ = host;
        var today = DateTime.Now.Date;
        var oldId = AddRun(host, 1, today.AddDays(-40), "Movies", 0, File_("Movies", "old.mkv"));
        var midId = AddRun(host, 2, today.AddDays(-3), "Movies", 1, File_("Movies", "mid.mkv"), File_("Movies", "bad.mkv", success: false));
        var newId = AddRun(host, 3, today, "Movies", 0, File_("Movies", "new.mkv", before: 10, after: 4));

        var (status, all, _) = await Get(host, key, "/api/v1/runs");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Sorted("total", "page", "pageSize", "runs"), Keys(all));
        var runs = all!["runs"]!.AsArray();
        Assert.Equal(Sorted("id", "number", "endedAt", "durationSeconds", "files", "beforeGb", "afterGb", "savedGb", "errors", "warnings", "redirects", "movies", "tvEpisodes",
            "avgFps", "engines", "counted", "hasReport", "reportUrl"), Keys(runs[0]));
        Assert.Equal(Sorted("handbrake", "ffmpeg", "passthrough"), Keys(runs[0]!["engines"]));
        Assert.Equal(new[] { newId, midId, oldId }, runs.Select(r => r!["id"]!.GetValue<long>()).ToArray());
        Assert.Equal(6, runs[0]!["savedGb"]!.GetValue<double>());
        Assert.Equal(600, runs[0]!["durationSeconds"]!.GetValue<int>());
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d[+-]\d\d:\d\d$", runs[0]!["endedAt"]!.GetValue<string>());
        Assert.Equal($"/api/v1/reports/{newId}", runs[0]!["reportUrl"]!.GetValue<string>());

        var (_, d7, _) = await Get(host, key, "/api/v1/runs?period=d7");
        Assert.Equal(2, d7!["total"]!.GetValue<int>());
        var (_, d30, _) = await Get(host, key, "/api/v1/runs?period=today");
        Assert.Equal(newId, Assert.Single(d30!["runs"]!.AsArray())!["id"]!.GetValue<long>());
        var (_, range, _) = await Get(host, key, $"/api/v1/runs?from={today.AddDays(-5):yyyy-MM-dd}&to={today.AddDays(-1):yyyy-MM-dd}");
        Assert.Equal(midId, Assert.Single(range!["runs"]!.AsArray())!["id"]!.GetValue<long>());
        var (_, paged, _) = await Get(host, key, "/api/v1/runs?pageSize=2&page=2");
        Assert.Equal((3, 2, 1), (paged!["total"]!.GetValue<int>(), paged["page"]!.GetValue<int>(), paged["runs"]!.AsArray().Count));
        Assert.Equal(oldId, paged["runs"]![0]!["id"]!.GetValue<long>());

        foreach (var bad in new[] { "/api/v1/runs?period=forever", "/api/v1/runs?from=yesterday", "/api/v1/runs?to=2026-13-40" })
        {
            var (code, body, _) = await Get(host, key, bad);
            Assert.Equal(HttpStatusCode.BadRequest, code);
            Assert.Equal("bad_request", body!["error"]!["code"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task OneRun_HasTheseFields_WithItsLanesAndFiles_And404ForAnUnknownOne()
    {
        var (host, key) = await StartWithKey();
        await using var _ = host;
        var id = AddRun(host, 7, DateTime.Now.Date, "Movies", 1, File_("Movies", "good.mkv"), File_("Movies", "bad.mkv", success: false));

        var (status, body, _) = await Get(host, key, $"/api/v1/runs/{id}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Sorted("run", "lanes"), Keys(body));
        Assert.Equal(7, body!["run"]!["number"]!.GetValue<int>());
        var lane = Assert.Single(body["lanes"]!.AsArray())!;
        Assert.Equal(Sorted("name", "problems", "files"), Keys(lane));
        Assert.Equal("Movies", lane["name"]!.GetValue<string>());
        var files = lane["files"]!.AsArray();
        Assert.Equal(2, files.Count);
        Assert.Equal(Sorted("name", "contentType", "preset", "encoder", "engine", "passthrough", "beforeGb", "afterGb", "seconds", "startedAt", "endedAt", "success", "counted", "avgFps",
            "errorCode", "failureReason", "warning", "arrStatus", "redirectedToLane", "homeLane", "diskFull"), Keys(files[0]));
        var good = files.Single(f => f!["name"]!.GetValue<string>() == "good.mkv")!;
        Assert.True(good["success"]!.GetValue<bool>());
        Assert.Equal("handbrake", good["engine"]!.GetValue<string>());
        Assert.Equal(600, good["seconds"]!.GetValue<double>());
        var bad = files.Single(f => f!["name"]!.GetValue<string>() == "bad.mkv")!;
        Assert.False(bad["success"]!.GetValue<bool>());
        Assert.Equal("boom", bad["failureReason"]!.GetValue<string>());
        Assert.NotNull(bad["errorCode"]);
        Assert.DoesNotContain("detailLog", body.ToJsonString());

        var (missing, error, _) = await Get(host, key, "/api/v1/runs/99999");
        Assert.Equal(HttpStatusCode.NotFound, missing);
        Assert.Equal("not_found", error!["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task Stats_HaveTheseFields_AndSumTheCountedRunsOfThePeriod()
    {
        var (host, key) = await StartWithKey();
        await using var _ = host;
        var today = DateTime.Now.Date;
        AddRun(host, 1, today.AddDays(-40), "Movies", 0, File_("Movies", "old.mkv", before: 8, after: 2));
        AddRun(host, 2, today.AddDays(-2), "Kids", 0, File_("Kids", "a.mkv", before: 4, after: 1), File_("Kids", "b.mkv", before: 6, after: 3));

        var (status, all, _) = await Get(host, key, "/api/v1/stats");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Sorted("period", "from", "to", "runs", "files", "beforeGb", "afterGb", "savedGb", "seconds", "movies", "tvEpisodes", "splitUnknown", "errors", "warnings",
            "redirects", "avgFps", "engines", "lanes", "profiles"), Keys(all));
        Assert.Equal(Sorted("handbrake", "ffmpeg", "passthrough"), Keys(all!["engines"]));
        Assert.Equal("all", all["period"]!.GetValue<string>());
        Assert.Equal((2, 3), (all["runs"]!.GetValue<int>(), all["files"]!.GetValue<int>()));
        Assert.Equal((18, 6, 12), (all["beforeGb"]!.GetValue<double>(), all["afterGb"]!.GetValue<double>(), all["savedGb"]!.GetValue<double>()));
        var lane = all["lanes"]!.AsArray()[0]!;
        Assert.Equal(Sorted("name", "files"), Keys(lane));
        Assert.Equal(("Kids", 2), (lane["name"]!.GetValue<string>(), lane["files"]!.GetValue<int>()));   // biggest first
        Assert.Equal(Sorted("name", "files"), Keys(all["profiles"]!.AsArray()[0]));

        var (_, d7, _) = await Get(host, key, "/api/v1/stats?period=d7");
        Assert.Equal((1, 2, 10), (d7!["runs"]!.GetValue<int>(), d7["files"]!.GetValue<int>(), d7["beforeGb"]!.GetValue<int>()));
        Assert.Equal(today.AddDays(-6).ToString("yyyy-MM-dd"), d7["from"]!.GetValue<string>());
        Assert.Null(d7["to"]);

        var (bad, _, _) = await Get(host, key, "/api/v1/stats?period=forever");
        Assert.Equal(HttpStatusCode.BadRequest, bad);
    }

    [Fact]
    public async Task Stats_OfAnEmptyHistory_AreZeroes_NotAnError()
    {
        var (host, key) = await StartWithKey();
        await using var _ = host;

        var (status, body, _) = await Get(host, key, "/api/v1/stats?period=today");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal((0, 0), (body!["runs"]!.GetValue<int>(), body["files"]!.GetValue<int>()));
        Assert.Null(body["avgFps"]);
        Assert.Empty(body["lanes"]!.AsArray());
    }

    [Fact]
    public async Task Reports_AreTheSameHtmlAsTheWebUi_AndDownloadable_And404ForAnUnknownRun()
    {
        var (host, key) = await StartWithKey();
        await using var _ = host;
        var id = AddRun(host, 1, DateTime.Now.Date, "Movies", 0, File_("Movies", "good.mkv"));

        var (status, _, response) = await Get(host, key, $"/api/v1/reports/{id}");
        var html = await response.Content.ReadAsStringAsync();
        var viaUi = await host.Client.GetStringAsync($"/api/reports/{id}");
        var (_, _, download) = await Get(host, key, $"/api/v1/reports/{id}?download=1");
        var (missing, error, _) = await Get(host, key, "/api/v1/reports/99999");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("good.mkv", html);
        Assert.Equal(viaUi, html);
        Assert.NotNull(download.Content.Headers.ContentDisposition?.FileName ?? download.Content.Headers.ContentDisposition?.FileNameStar);
        Assert.Equal(HttpStatusCode.NotFound, missing);
        Assert.Equal("not_found", error!["error"]!["code"]!.GetValue<string>());
    }
}
