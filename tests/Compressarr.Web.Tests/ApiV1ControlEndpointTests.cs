using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Compressarr.Core.Scheduling;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Web.Tests;

/// <summary>The control half of the Automation API: a control key is needed, the hold holds (and shows everywhere), and the schedule can be changed in part - but
/// only into a valid schedule.</summary>
public class ApiV1ControlEndpointTests
{
    private static readonly LaneSpec Lane1 = new("hdsd", "SD-HD");

    /// <summary>The key of that scope (the two fixed keys exist from the start; the name is only for the reader of the test).</summary>
    private static async Task<string> MakeKey(QueueHost host, string name, string scope)
    {
        var keys = JsonNode.Parse(await host.Client.GetStringAsync("/api/automation/keys"))!["keys"]!.AsArray();
        return keys.Single(k => k!["scope"]!.GetValue<string>() == scope)!["key"]!.GetValue<string>();
    }

    private static async Task<(HttpStatusCode Status, JsonNode? Body)> Send(QueueHost host, string key, HttpMethod method, string url, string? json = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("X-Api-Key", key);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await host.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType == "application/json" ? JsonNode.Parse(text) : null);
    }

    private static string[] Keys(JsonNode? node) => node!.AsObject().Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();

    // ---- the door -------------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("POST", "/api/v1/run")]
    [InlineData("POST", "/api/v1/monitoring/start")]
    [InlineData("POST", "/api/v1/monitoring/stop")]
    [InlineData("POST", "/api/v1/pause")]
    [InlineData("POST", "/api/v1/resume")]
    [InlineData("POST", "/api/v1/hold")]
    [InlineData("DELETE", "/api/v1/hold")]
    [InlineData("POST", "/api/v1/schedule/run-anyway")]
    [InlineData("PATCH", "/api/v1/schedule")]
    public async Task EveryControlCall_NeedsAControlKey_AReadKeyIsRefused(string method, string url)
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var readKey = await MakeKey(host, "dashboard", "read");

        var (status, body) = await Send(host, readKey, new HttpMethod(method), url, "{}");

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("insufficient_scope", body!["error"]!["code"]!.GetValue<string>());
        using var anonymous = await host.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task AControlKey_AlsoReads()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var key = await MakeKey(host, "ha", "control");

        var (status, _) = await Send(host, key, HttpMethod.Get, "/api/v1/status");

        Assert.Equal(HttpStatusCode.OK, status);
    }

    // ---- the hold -------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AHold_IsPlaced_ShowsInStatusAndTheWebUi_AndIsReleased()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var control = await MakeKey(host, "Home Assistant", "control");
        var read = await MakeKey(host, "dashboard", "read");
        var until = DateTimeOffset.Now.AddHours(2);

        var (placed, body) = await Send(host, control, HttpMethod.Post, "/api/v1/hold", $$"""{ "reason": "TV on", "until": "{{until:O}}", "suspendCurrent": true }""");

        Assert.Equal(HttpStatusCode.OK, placed);
        var hold = body!["hold"]!;
        Assert.Equal(Sorted("reason", "since", "until", "suspendCurrent", "setBy"), Keys(hold));
        Assert.Equal(("TV on", true, "API Key"), (hold["reason"]!.GetValue<string>(), hold["suspendCurrent"]!.GetValue<bool>(), hold["setBy"]!.GetValue<string>()));
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d[+-]\d\d:\d\d$", hold["since"]!.GetValue<string>());

        var (_, status) = await Send(host, read, HttpMethod.Get, "/api/v1/status");
        Assert.Equal("TV on", status!["hold"]!["reason"]!.GetValue<string>());
        Assert.True(status["schedule"]!["isHeld"]!.GetValue<bool>());        // the schedule is off, the hold alone holds
        Assert.Equal("held", status["queue"]!["etaState"]!.GetValue<string>());
        var (_, current) = await Send(host, read, HttpMethod.Get, "/api/v1/hold");
        Assert.Equal("TV on", current!["hold"]!["reason"]!.GetValue<string>());
        var ui = JsonNode.Parse(await host.Client.GetStringAsync("/api/run/status"))!;
        Assert.Equal("TV on", ui["schedule"]!["hold"]!["reason"]!.GetValue<string>());
        Assert.True(ui["schedule"]!["isHeld"]!.GetValue<bool>());

        var (released, after) = await Send(host, control, HttpMethod.Delete, "/api/v1/hold");
        Assert.Equal(HttpStatusCode.OK, released);
        Assert.True(after!["released"]!.GetValue<bool>());
        var (_, gone) = await Send(host, read, HttpMethod.Get, "/api/v1/status");
        Assert.Null(gone!["hold"]);
        Assert.False(gone["schedule"]!["isHeld"]!.GetValue<bool>());

        var (_, again) = await Send(host, control, HttpMethod.Delete, "/api/v1/hold");
        Assert.False(again!["released"]!.GetValue<bool>());   // releasing nothing is fine, and says so
    }

    private static string[] Sorted(params string[] names) => names.OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task AHoldWithNoBody_HoldsUntilReleased_AndPlacingItAgainKeepsItsStart()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var key = await MakeKey(host, "ha", "control");

        var (first, one) = await Send(host, key, HttpMethod.Post, "/api/v1/hold");
        await Task.Delay(1100);
        var (_, two) = await Send(host, key, HttpMethod.Post, "/api/v1/hold", """{ "reason": "still on" }""");

        Assert.Equal(HttpStatusCode.OK, first);
        Assert.Null(one!["hold"]!["until"]);
        Assert.Null(one["hold"]!["reason"]);
        Assert.Equal(one["hold"]!["since"]!.GetValue<string>(), two!["hold"]!["since"]!.GetValue<string>());
        Assert.Equal("still on", two["hold"]!["reason"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{ "until": "tomorrow evening" }""")]
    [InlineData("""{ "until": "2001-01-01T00:00:00Z" }""")]
    [InlineData("""{ "until": "2999-01-01T00:00:00Z" }""")]
    public async Task ABadEndTime_IsRefused_AndNothingIsHeld(string json)
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var key = await MakeKey(host, "ha", "control");

        var (status, body) = await Send(host, key, HttpMethod.Post, "/api/v1/hold", json);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad_request", body!["error"]!["code"]!.GetValue<string>());
        Assert.Null(host.Services.GetRequiredService<IExternalHold>().Current());
    }

    [Fact]
    public async Task ATooLongReason_IsRefused()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var key = await MakeKey(host, "ha", "control");

        var (status, _) = await Send(host, key, HttpMethod.Post, "/api/v1/hold", $$"""{ "reason": "{{new string('x', 201)}}" }""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task TheMonitorsReleaseButton_ReleasesAHold()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var key = await MakeKey(host, "ha", "control");
        await Send(host, key, HttpMethod.Post, "/api/v1/hold", """{ "reason": "TV on" }""");

        using var response = await host.Client.PostAsync("/api/run/release-hold", null);

        Assert.True(JsonNode.Parse(await response.Content.ReadAsStringAsync())!["released"]!.GetValue<bool>());
        Assert.Null(host.Services.GetRequiredService<IExternalHold>().Current());
        using var again = await host.Client.PostAsync("/api/run/release-hold", null);
        Assert.False(JsonNode.Parse(await again.Content.ReadAsStringAsync())!["released"]!.GetValue<bool>());
    }

    // ---- pause, resume, monitoring, run anyway -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Pause_WithNothingEncoding_Is409_AndResume_WhenNotPaused_IsFine()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var key = await MakeKey(host, "ha", "control");

        var (pause, pauseBody) = await Send(host, key, HttpMethod.Post, "/api/v1/pause");
        var (resume, resumeBody) = await Send(host, key, HttpMethod.Post, "/api/v1/resume");

        Assert.Equal(HttpStatusCode.Conflict, pause);
        Assert.Equal("not_running", pauseBody!["error"]!["code"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, resume);
        Assert.False(resumeBody!["paused"]!.GetValue<bool>());
    }

    [Fact]
    public async Task StoppingMonitoring_WhenItIsNotOn_SaysSo()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var key = await MakeKey(host, "ha", "control");

        var (status, body) = await Send(host, key, HttpMethod.Post, "/api/v1/monitoring/stop");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body!["monitoring"]!.GetValue<bool>());
        Assert.False(body["stopping"]!.GetValue<bool>());
    }

    [Fact]
    public async Task RunAnyway_AnswersWithTheScheduleState()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var key = await MakeKey(host, "ha", "control");

        var (status, body) = await Send(host, key, HttpMethod.Post, "/api/v1/schedule/run-anyway");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Sorted("enabled", "isDaytime", "priority", "onlyOffHours", "isHeld", "overrideActive", "nextChange"), Keys(body!["state"]));
    }

    // ---- the schedule -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PatchingTheSchedule_ChangesOnlyTheFieldsSent_AndTheSchedulerPageSeesIt()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var key = await MakeKey(host, "ha", "control");
        var before = JsonNode.Parse(await host.Client.GetStringAsync("/api/schedule"))!;

        var (status, body) = await Send(host, key, new HttpMethod("PATCH"), "/api/v1/schedule",
            """{ "enabled": true, "onlyEncodeOffHours": true, "dayStart": "09:30", "dayEnd": "21:00", "nightPriority": "high" }""");

        Assert.Equal(HttpStatusCode.OK, status);
        var settings = body!["settings"]!;
        Assert.Equal((true, true, "09:30", "21:00", "High"),
            (settings["enabled"]!.GetValue<bool>(), settings["onlyEncodeOffHours"]!.GetValue<bool>(), settings["dayStart"]!.GetValue<string>(), settings["dayEnd"]!.GetValue<string>(), settings["nightPriority"]!.GetValue<string>()));
        Assert.Equal(before["dayPriority"]!.GetValue<string>(), settings["dayPriority"]!.GetValue<string>());   // not sent, so not changed
        Assert.Equal(before["whenDayStarts"]!.GetValue<string>(), settings["whenDayStarts"]!.GetValue<string>());
        Assert.NotNull(body["state"]);

        var page = JsonNode.Parse(await host.Client.GetStringAsync("/api/schedule"))!;
        Assert.Equal(("09:30", "21:00", true), (page["dayStart"]!.GetValue<string>(), page["dayEnd"]!.GetValue<string>(), page["onlyEncodeOffHours"]!.GetValue<bool>()));
    }

    [Theory]
    [InlineData("""{ }""", "bad_request")]
    [InlineData("""{ "flavour": "mint" }""", "bad_request")]
    [InlineData("""{ "enabled": "yes" }""", "bad_request")]
    [InlineData("""{ "mode": "sometimes" }""", "bad_request")]
    [InlineData("""{ "nightPriority": "ludicrous" }""", "bad_request")]
    [InlineData("""{ "days": [] }""", "bad_request")]
    [InlineData("""{ "dayStart": "25:00" }""", "invalid_schedule")]
    [InlineData("""{ "enabled": true, "dayStart": "noon" }""", "invalid_schedule")]
    public async Task ABadPatch_IsRefused_AndNothingIsSaved(string json, string code)
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var key = await MakeKey(host, "ha", "control");
        var before = await host.Client.GetStringAsync("/api/schedule");

        var (status, body) = await Send(host, key, new HttpMethod("PATCH"), "/api/v1/schedule", json);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(code, body!["error"]!["code"]!.GetValue<string>());
        Assert.Equal(before, await host.Client.GetStringAsync("/api/schedule"));
    }

    [Fact]
    public async Task AnInvalidSchedule_ListsWhatIsWrong()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var key = await MakeKey(host, "ha", "control");

        var (_, body) = await Send(host, key, new HttpMethod("PATCH"), "/api/v1/schedule", """{ "dayStart": "25:00" }""");

        var issue = Assert.Single(body!["error"]!["issues"]!.AsArray())!;
        Assert.False(string.IsNullOrWhiteSpace(issue["message"]!.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(issue["field"]!.GetValue<string>()));
    }
}
