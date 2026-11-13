using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Compressarr.Web.Tests;

/// <summary>Videos lanes, per-lane tracking and per-lane processing settings through the real lane endpoints and the Monitor queue.</summary>
public class LaneKindAndProcessingEndpointTests
{
    private static readonly LaneSpec Home = new("hdsd", "SD-HD");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static async Task<JsonArray> LanesAsync(QueueHost host) => JsonNode.Parse(await host.Client.GetStringAsync("/api/lanes"))!.AsArray();

    private static async Task<(HttpStatusCode Status, JsonObject Body)> PutAsync(QueueHost host, JsonObject lane)
    {
        using var response = await host.Client.PutAsync($"/api/lanes/{lane["id"]!.GetValue<string>()}", new StringContent(lane.ToJsonString(), Encoding.UTF8, "application/json"));
        return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject());
    }

    // ---- per-lane processing ----

    [Fact]
    public async Task ANewLane_FollowsTheGlobalProcessingSettings()
    {
        await using var host = await QueueHost.StartAsync(Home);

        var lane = (await LanesAsync(host))[0]!.AsObject();

        Assert.True(lane["useGlobalProcessing"]!.GetValue<bool>());
        Assert.Null(lane["processing"]);
    }

    [Fact]
    public async Task ALanesOwnProcessingSettings_AreSaved_AndGoingBackToGlobalRemovesThem()
    {
        await using var host = await QueueHost.StartAsync(Home);
        var lane = (await LanesAsync(host))[0]!.AsObject();
        lane["useGlobalProcessing"] = false;
        lane["processing"] = new JsonObject
        {
            ["vidTypes"] = new JsonArray("mkv", "webm"),
            ["deleteAfterConvert"] = "Maintain",
            ["moveFiles"] = true,
            ["clearTitleMetadata"] = false,
            ["minSizeBytes"] = 1048576,
            ["companionExtensions"] = new JsonArray("srt"),
            ["unmatchedCompanionAction"] = "Maintain",
            ["onDestinationCollision"] = "Rename"
        };

        var (status, saved) = await PutAsync(host, lane);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(saved["useGlobalProcessing"]!.GetValue<bool>());
        Assert.Equal("Maintain", saved["processing"]!["deleteAfterConvert"]!.GetValue<string>());
        Assert.Equal("Rename", saved["processing"]!["onDestinationCollision"]!.GetValue<string>());
        Assert.Equal(new[] { "mkv", "webm" }, saved["processing"]!["vidTypes"]!.AsArray().Select(t => t!.GetValue<string>()).ToArray());

        var again = (await LanesAsync(host))[0]!.AsObject();
        again["useGlobalProcessing"] = true;
        again["processing"] = null;
        var (_, restored) = await PutAsync(host, again);

        Assert.True(restored["useGlobalProcessing"]!.GetValue<bool>());
        Assert.Null(restored["processing"]);
    }

    [Fact]
    public async Task ALaneWhoseOwnProcessingHasNoVideoTypes_IsFlagged()
    {
        await using var host = await QueueHost.StartAsync(Home);
        var lane = (await LanesAsync(host))[0]!.AsObject();
        lane["useGlobalProcessing"] = false;
        lane["processing"] = new JsonObject
        {
            ["vidTypes"] = new JsonArray(), ["deleteAfterConvert"] = "Recycle", ["moveFiles"] = true, ["clearTitleMetadata"] = true, ["minSizeBytes"] = 0,
            ["companionExtensions"] = new JsonArray(), ["unmatchedCompanionAction"] = "Recycle", ["onDestinationCollision"] = "Overwrite"
        };

        var (_, saved) = await PutAsync(host, lane);

        Assert.Contains(saved["validationIssues"]!.AsArray(), i => i!["field"]!.GetValue<string>() == "processing");
    }

    [Fact]
    public async Task ADuplicatedLane_CarriesItsOwnProcessingSettingsAlong()
    {
        await using var host = await QueueHost.StartAsync(Home);
        var lane = (await LanesAsync(host))[0]!.AsObject();
        lane["useGlobalProcessing"] = false;
        lane["processing"] = new JsonObject
        {
            ["vidTypes"] = new JsonArray("mkv"), ["deleteAfterConvert"] = "Maintain", ["moveFiles"] = false, ["clearTitleMetadata"] = false, ["minSizeBytes"] = 0,
            ["companionExtensions"] = new JsonArray("srt"), ["unmatchedCompanionAction"] = "Maintain", ["onDestinationCollision"] = "Skip"
        };
        lane["tracking"] = "ErrorsOnly";

        using var response = await host.Client.PostAsJsonAsync("/api/lanes/duplicate", new { displayName = "Copy", lane }, Web);
        var copy = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Skip", copy["processing"]!["onDestinationCollision"]!.GetValue<string>());
        Assert.Equal("ErrorsOnly", copy["tracking"]!.GetValue<string>());
    }

    // ---- Videos lane ----

    [Fact]
    public async Task AddingAVideosLane_StartsWithItsOwnProcessing_RenamesCollisions_AndMovesFiles()
    {
        await using var host = await QueueHost.StartAsync(Home);

        using var response = await host.Client.PostAsync("/api/lanes?kind=videos", null);
        var lane = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Videos", lane["kind"]!.GetValue<string>());
        Assert.Equal("Full", lane["tracking"]!.GetValue<string>());
        Assert.False(lane["useGlobalProcessing"]!.GetValue<bool>());
        Assert.Equal("Rename", lane["processing"]!["onDestinationCollision"]!.GetValue<string>());
        Assert.True(lane["processing"]!["moveFiles"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AnOrdinaryLane_IsStandard_WithFullTracking()
    {
        await using var host = await QueueHost.StartAsync(Home);

        var lane = (await LanesAsync(host))[0]!.AsObject();

        Assert.Equal("Standard", lane["kind"]!.GetValue<string>());
        Assert.Equal("Full", lane["tracking"]!.GetValue<string>());
    }

    [Fact]
    public async Task AVideosLane_NeedsNoTvProfileOrTvDestination_ButDoesNeedAProfile()
    {
        await using var host = await QueueHost.StartAsync(Home);
        var lane = JsonNode.Parse(await (await host.Client.PostAsync("/api/lanes?kind=videos", null)).Content.ReadAsStringAsync())!.AsObject();
        lane["enabled"] = true;
        lane["input"] = Path.Combine(host.Root, "vin");
        Directory.CreateDirectory(lane["input"]!.GetValue<string>());
        lane["output"] = Path.Combine(host.Root, "vout");
        Directory.CreateDirectory(lane["output"]!.GetValue<string>());
        lane["movieBasePath"] = Path.Combine(host.Root, "vlib");
        Directory.CreateDirectory(lane["movieBasePath"]!.GetValue<string>());
        lane["tvPreset"] = "";
        lane["tvShowBasePath"] = "";
        lane["moviePreset"] = "Compressarr SD-HD";

        var (_, ok) = await PutAsync(host, lane);
        lane["moviePreset"] = "";
        var (_, noProfile) = await PutAsync(host, lane);

        Assert.DoesNotContain(ok["validationIssues"]!.AsArray(), i => i!["field"]!.GetValue<string>() is "tvPreset" or "tvShowBasePath");
        Assert.Contains(noProfile["validationIssues"]!.AsArray(), i => i!["field"]!.GetValue<string>() == "moviePreset");
    }

    [Fact]
    public async Task AVideosLanesQueueRows_AreVideos_EvenWhenTheNameLooksLikeAnEpisode()
    {
        await using var host = await QueueHost.StartAsync(Home);
        var lane = (await LanesAsync(host))[0]!.AsObject();
        lane["kind"] = "Videos";
        await PutAsync(host, lane);
        host.Drop("hdsd", "Holiday S01E02.mkv");

        var rows = JsonNode.Parse(await host.Client.GetStringAsync("/api/run/status"))!["upNext"]!.AsArray();

        Assert.Equal("video", Assert.Single(rows)!["kind"]!.GetValue<string>());
    }

    [Fact]
    public async Task ARegularLanesQueueRows_KeepTheirNameBasedKind()
    {
        await using var host = await QueueHost.StartAsync(Home);
        host.Drop("hdsd", "Holiday S01E02.mkv");

        var rows = JsonNode.Parse(await host.Client.GetStringAsync("/api/run/status"))!["upNext"]!.AsArray();

        Assert.Equal("tv", Assert.Single(rows)!["kind"]!.GetValue<string>());
    }

    // ---- Tracking ----

    [Fact]
    public async Task ATrackingChoice_IsSavedOnTheLane()
    {
        await using var host = await QueueHost.StartAsync(Home);
        var lane = (await LanesAsync(host))[0]!.AsObject();
        lane["tracking"] = "None";

        var (status, saved) = await PutAsync(host, lane);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("None", saved["tracking"]!.GetValue<string>());
        Assert.Equal("None", (await LanesAsync(host))[0]!["tracking"]!.GetValue<string>());
    }
}
