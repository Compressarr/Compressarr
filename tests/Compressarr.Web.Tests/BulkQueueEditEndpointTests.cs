using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Compressarr.Web.Tests;

/// <summary>"Change all" on the Monitor page's In Queue list: the preset and/or the landing lane of many waiting files in one request, through the real endpoint.</summary>
public class BulkQueueEditEndpointTests
{
    private static readonly LaneSpec Home = new("hdsd", "SD-HD");
    private static readonly LaneSpec Kids = new("kids", "Kids");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static async Task<(HttpStatusCode Status, JsonObject Body)> BulkAsync(QueueHost host, IEnumerable<QueueItem> rows, bool setPreset, string? preset, bool setDestination, string? destination)
    {
        var request = new
        {
            items = rows.Select(r => new { laneId = r.LaneId, fullName = r.FullName }).ToList(),
            setPreset,
            preset,
            setDestination,
            destinationLaneId = destination
        };
        using var response = await host.Client.PostAsJsonAsync("/api/run/queue/bulk-edit", request, Web);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.TrimStart().StartsWith('{') ? JsonNode.Parse(text)!.AsObject() : new JsonObject());
    }

    [Fact]
    public async Task ChangingThePresetOfEveryQueuedFile_SetsItOnEachRow_AndKeepsTheOrder()
    {
        await using var host = await QueueHost.StartAsync(Home, Kids);
        host.Drop("hdsd", "a.mkv");
        host.Drop("hdsd", "b.mkv");
        host.Drop("kids", "c.mkv");
        var before = await host.PollAsync();

        var (status, body) = await BulkAsync(host, before, setPreset: true, "Compressarr SD-HD", setDestination: false, null);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(3, body["changed"]!.GetValue<int>());
        var after = await host.PollAsync();
        Assert.Equal(before.Select(r => r.FullName), after.Select(r => r.FullName));      // nothing moved in the queue
        Assert.All(after, r => { Assert.True(r.IsCustomPreset); Assert.Equal("Compressarr SD-HD", r.Preset); });
    }

    [Fact]
    public async Task ChangingJustTheLandingLane_LeavesEveryPresetAlone()
    {
        await using var host = await QueueHost.StartAsync(Home, Kids);
        var one = host.Drop("hdsd", "a.mkv");
        host.Drop("hdsd", "b.mkv");
        await host.OverridePresetAsync("hdsd", one, "Compressarr SD-HD");
        var before = await host.PollAsync();

        var (status, body) = await BulkAsync(host, before, setPreset: false, null, setDestination: true, "kids");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, body["changed"]!.GetValue<int>());
        var after = await host.PollAsync();
        Assert.All(after, r => { Assert.Equal("kids", r.DestinationLaneId); Assert.Equal("hdsd", r.LaneId); });
        Assert.True(after.Single(r => r.FullName == one).IsCustomPreset);                 // the earlier per-file choice survives
        Assert.False(after.Single(r => r.FullName != one).IsCustomPreset);
    }

    [Fact]
    public async Task ClearingTheLandingLaneForEveryFile_PutsThemBackInTheirOwnLanes()
    {
        await using var host = await QueueHost.StartAsync(Home, Kids);
        host.Drop("hdsd", "a.mkv");
        host.Drop("hdsd", "b.mkv");
        await BulkAsync(host, await host.PollAsync(), setPreset: false, null, setDestination: true, "kids");

        var (status, _) = await BulkAsync(host, await host.PollAsync(), setPreset: false, null, setDestination: true, null);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.All(await host.PollAsync(), r => Assert.Null(r.DestinationLaneId));
    }

    [Fact]
    public async Task ABlankPreset_ClearsEveryFilesOverride_BackToTheLanesOwnProfile()
    {
        await using var host = await QueueHost.StartAsync(Home);
        host.Drop("hdsd", "a.mkv");
        host.Drop("hdsd", "b.mkv");
        await BulkAsync(host, await host.PollAsync(), setPreset: true, "Compressarr SD-HD", setDestination: false, null);

        var (status, _) = await BulkAsync(host, await host.PollAsync(), setPreset: true, "", setDestination: false, null);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.All(await host.PollAsync(), r => Assert.False(r.IsCustomPreset));
    }

    [Fact]
    public async Task ALandingLaneThatDoesNotExist_IsRefused_AndNothingChanges()
    {
        await using var host = await QueueHost.StartAsync(Home);
        host.Drop("hdsd", "a.mkv");
        var rows = await host.PollAsync();

        var (status, _) = await BulkAsync(host, rows, setPreset: false, null, setDestination: true, "no-such-lane");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.All(await host.PollAsync(), r => Assert.Null(r.DestinationLaneId));
    }

    [Fact]
    public async Task AProfileTheLanesEncoderDoesNotHave_IsNotApplied_AndIsCounted()
    {
        await using var host = await QueueHost.StartAsync(Home);
        host.Drop("hdsd", "a.mkv");
        host.Drop("hdsd", "b.mkv");

        var (status, body) = await BulkAsync(host, await host.PollAsync(), setPreset: true, "No Such Profile", setDestination: false, null);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(0, body["changed"]!.GetValue<int>());
        Assert.Equal(2, body["incompatiblePreset"]!.GetValue<int>());
        Assert.All(await host.PollAsync(), r => Assert.False(r.IsCustomPreset));
    }

    [Fact]
    public async Task AFileThatIsNotInTheLanesInput_IsSkipped_WhileTheOthersAreChanged()
    {
        await using var host = await QueueHost.StartAsync(Home);
        host.Drop("hdsd", "a.mkv");
        var rows = (await host.PollAsync()).ToList();
        var stray = new QueueItem(rows[0].LaneId, rows[0].LaneDisplayName, "stray.mkv", Path.Combine(host.Root, "Elsewhere", "stray.mkv"), null, false, false, false, false);
        Directory.CreateDirectory(Path.GetDirectoryName(stray.FullName)!);
        File.WriteAllText(stray.FullName, "not in this lane's Input");
        rows.Add(stray);

        var (status, body) = await BulkAsync(host, rows, setPreset: true, "Compressarr SD-HD", setDestination: false, null);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, body["changed"]!.GetValue<int>());
        Assert.Equal(1, body["skipped"]!.GetValue<int>());                // a file outside the lane's Input is never taken into the queue
    }

    [Fact]
    public async Task AskingForNothing_IsABadRequest()
    {
        await using var host = await QueueHost.StartAsync(Home);
        host.Drop("hdsd", "a.mkv");

        var (status, _) = await BulkAsync(host, await host.PollAsync(), setPreset: false, null, setDestination: false, null);

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }
}
