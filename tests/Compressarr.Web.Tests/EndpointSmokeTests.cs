using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Compressarr.Web.Tests;

/// <summary>
/// Routing / JSON-binding / dependency-injection smoke tests: every endpoint here is called through
/// the real host exactly as the browser calls it. These catch the class of regression the Core
/// tests structurally can't - a handler gaining a parameter that isn't registered, a request
/// record whose JSON shape no longer binds, a route that stops existing. Deliberately limited to
/// endpoints that are safe offline (no network, no external programs).
/// </summary>
public class EndpointSmokeTests
{
    private static readonly LaneSpec Lane1 = new("lane1", "Lane One");

    [Theory]
    [InlineData("/api/run/status")]
    [InlineData("/api/lanes")]
    [InlineData("/api/settings")]
    [InlineData("/api/settings/export")]
    [InlineData("/api/schedule")]
    [InlineData("/api/notifications/settings")]
    [InlineData("/api/notifications/channels")]
    [InlineData("/api/notifications/types")]
    [InlineData("/api/notifications/message-presets")]
    [InlineData("/api/history")]
    [InlineData("/api/history/reports")]
    [InlineData("/api/history/runs")]
    [InlineData("/api/history/viewed-through")]
    [InlineData("/api/donate/addresses")]
    [InlineData("/api/about")]
    public async Task OfflineGetEndpoints_ReturnValidJson(string url)
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        using var response = await host.Client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} returned {(int)response.StatusCode}: {body}");
        using var _ = JsonDocument.Parse(body);
    }

    [Fact]
    public async Task Status_HasTheFieldsTheMonitorPageReads()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        host.Drop("lane1", "a.mkv");

        using var doc = JsonDocument.Parse(await host.Client.GetStringAsync("/api/run/status"));
        var root = doc.RootElement;
        foreach (var field in new[]
        {
            "isMonitoring", "isStopping", "isRunning", "isRenaming", "isPaused", "laneDisplayName",
            "fileName", "presetName", "fileIndex", "fileTotal", "progressPercent", "recentLogLines",
            "secondsUntilNextRun", "upNext", "queueEtaText"
        })
        {
            Assert.True(root.TryGetProperty(field, out _), $"status payload is missing '{field}'");
        }

        var item = root.GetProperty("upNext")[0];
        foreach (var field in new[]
        {
            "laneId", "laneDisplayName", "fileName", "fullName", "sizeGb", "preset", "isResumed",
            "isError", "isSkipped", "isCustomPreset", "isFileBotUnmatched"
        })
        {
            Assert.True(item.TryGetProperty(field, out _), $"queue item is missing '{field}'");
        }
    }

    [Fact]
    public async Task Lanes_ListsTheConfiguredLanes()
    {
        await using var host = await QueueHost.StartAsync(Lane1, new LaneSpec("lane2", "Lane Two"));

        using var doc = JsonDocument.Parse(await host.Client.GetStringAsync("/api/lanes"));
        var ids = doc.RootElement.EnumerateArray().Select(l => l.GetProperty("id").GetString()).ToList();
        Assert.Equal(new[] { "lane1", "lane2" }, ids);
    }

    // ---- queue-control endpoints: success, unknown lane, unknown file ---------------------------

    [Fact]
    public async Task QueueControls_OnARealFile_ReturnOk()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var a = host.Drop("lane1", "a.mkv");

        Assert.Equal(HttpStatusCode.OK, await host.SkipAsync("lane1", a));
        Assert.Equal(HttpStatusCode.OK, await host.OverridePresetAsync("lane1", a, "X"));
        Assert.Equal(HttpStatusCode.OK, await host.ReorderAsync(("lane1", a)));
        Assert.Equal(HttpStatusCode.OK, await host.RemoveAsync("lane1", a));
    }

    [Fact]
    public async Task QueueControls_OnAnUnknownLane_Return404()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var a = host.Drop("lane1", "a.mkv");

        Assert.Equal(HttpStatusCode.NotFound, await host.SkipAsync("nope", a));
        Assert.Equal(HttpStatusCode.NotFound, await host.OverridePresetAsync("nope", a, "X"));
        Assert.Equal(HttpStatusCode.NotFound, await host.RemoveAsync("nope", a));
    }

    [Fact]
    public async Task QueueControls_OnAFileThatDoesNotExist_Return404()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var ghost = Path.Combine(Path.GetTempPath(), "does-not-exist.mkv");

        Assert.Equal(HttpStatusCode.NotFound, await host.SkipAsync("lane1", ghost));
        Assert.Equal(HttpStatusCode.NotFound, await host.OverridePresetAsync("lane1", ghost, "X"));
        Assert.Equal(HttpStatusCode.NotFound, await host.RemoveAsync("lane1", ghost));
        Assert.Empty(host.ResumeEntries());
    }

    [Fact]
    public async Task Reorder_SilentlySkipsItemsFromUnknownLanes()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var a = host.Drop("lane1", "a.mkv");

        Assert.Equal(HttpStatusCode.OK, await host.ReorderAsync(("nope", a), ("lane1", a)));
        Assert.Equal(new[] { "a.mkv" }, await host.PollNamesAsync());
    }

    [Fact]
    public async Task RemoveError_ClearsOnlyAnErrorEntry_AndReportsHowManyWereRemoved()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var failed = host.Drop("lane1", "failed.mkv");
        var pending = host.Drop("lane1", "pending.mkv");
        host.SeedResume(
            new Compressarr.Core.Conversion.ResumeEntry { LaneId = "lane1", FullName = failed, Status = Compressarr.Core.Conversion.ResumeStatus.Error, Order = 0 },
            new Compressarr.Core.Conversion.ResumeEntry { LaneId = "lane1", FullName = pending, Status = Compressarr.Core.Conversion.ResumeStatus.Pending, Order = 1 });

        // A Pending entry is never touched by remove-error.
        using (var noop = await host.Client.PostAsJsonAsync("/api/run/queue/remove-error", new { laneId = "lane1", fullName = pending }))
            Assert.Equal(0, JsonDocument.Parse(await noop.Content.ReadAsStringAsync()).RootElement.GetProperty("removed").GetInt32());

        using var removed = await host.Client.PostAsJsonAsync("/api/run/queue/remove-error", new { laneId = "lane1", fullName = failed });
        Assert.Equal(1, JsonDocument.Parse(await removed.Content.ReadAsStringAsync()).RootElement.GetProperty("removed").GetInt32());
        Assert.DoesNotContain(host.ResumeEntries(), e => e.FullName == failed);
        Assert.Contains(host.ResumeEntries(), e => e.FullName == pending);
    }
}
