using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Compressarr.Web.Tests;

/// <summary>Queue-priority rules through the real endpoints: saving rules, where new arrivals land, the badge data on each row, and "apply to files already waiting".</summary>
public class QueuePriorityEndpointTests
{
    private static readonly LaneSpec Home = new("hdsd", "SD-HD");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static async Task<HttpStatusCode> PutRulesAsync(QueueHost host, params object[] rules)
    {
        using var response = await host.Client.PutAsJsonAsync("/api/queue-priority", new { rules }, Web);
        return response.StatusCode;
    }

    private static object Lower(string match) => new { match, priority = "Lower" };
    private static object Higher(string match) => new { match, priority = "Higher" };

    private static async Task<JsonArray> StatusRowsAsync(QueueHost host) =>
        JsonNode.Parse(await host.Client.GetStringAsync("/api/run/status"))!["upNext"]!.AsArray();

    private static async Task<List<string>> OrderAsync(QueueHost host) => (await host.PollAsync()).Select(r => r.FileName).ToList();

    [Fact]
    public async Task TheRules_AreSavedAndComeBack()
    {
        await using var host = await QueueHost.StartAsync(Home);

        Assert.Equal(HttpStatusCode.OK, await PutRulesAsync(host, Lower("Big Show"), new { tmdbId = 1396, priority = "Higher" }));

        var saved = JsonNode.Parse(await host.Client.GetStringAsync("/api/queue-priority"))!["rules"]!.AsArray();
        Assert.Equal(2, saved.Count);
        Assert.Equal("Big Show", saved[0]!["match"]!.GetValue<string>());
        Assert.Equal("Lower", saved[0]!["priority"]!.GetValue<string>());
        Assert.Equal(1396, saved[1]!["tmdbId"]!.GetValue<int>());
        Assert.False(string.IsNullOrEmpty(saved[0]!["id"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("x", "Lower")]            // too short to be a title
    [InlineData("Big Show", "Sideways")]  // not a priority
    public async Task ABadRule_IsRefused_AndNothingIsSaved(string match, string priority)
    {
        await using var host = await QueueHost.StartAsync(Home);

        using var response = await host.Client.PutAsJsonAsync("/api/queue-priority", new { rules = new object[] { new { match, priority } } }, Web);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(JsonNode.Parse(await host.Client.GetStringAsync("/api/queue-priority"))!["rules"]!.AsArray());
    }

    [Fact]
    public async Task NewArrivals_OfALoweredShow_QueueBehindNewerContent()
    {
        await using var host = await QueueHost.StartAsync(Home);
        await PutRulesAsync(host, Lower("Big Show"));
        for (var i = 1; i <= 5; i++) host.Drop("hdsd", $"Big Show - S01E0{i}.mkv");
        host.Drop("hdsd", "Heat (1995).mkv");
        host.Drop("hdsd", "Ronin (1998).mkv");

        var order = await OrderAsync(host);

        Assert.Equal(new[] { "Heat (1995).mkv", "Ronin (1998).mkv" }, order.Take(2));
        Assert.Equal("Big Show - S01E05.mkv", order[^1]);
    }

    [Fact]
    public async Task AFileArrivingAfterTheBacklog_StillLandsAheadOfIt_AndTheOrderIsLocked()
    {
        await using var host = await QueueHost.StartAsync(Home);
        await PutRulesAsync(host, Lower("Big Show"));
        host.Drop("hdsd", "Big Show - S01E01.mkv");
        host.Drop("hdsd", "Big Show - S01E02.mkv");
        await OrderAsync(host);                                   // the backlog is tracked first

        host.Drop("hdsd", "Alien (1979).mkv");
        var order = await OrderAsync(host);

        Assert.Equal(new[] { "Alien (1979).mkv", "Big Show - S01E01.mkv", "Big Show - S01E02.mkv" }, order);
        Assert.Equal(order, await OrderAsync(host));              // and it stays that way on the next look
    }

    [Fact]
    public async Task EachRow_SaysItsPriority()
    {
        await using var host = await QueueHost.StartAsync(Home);
        await PutRulesAsync(host, Lower("Big Show"), Higher("Bluey"));
        host.Drop("hdsd", "Big Show - S01E01.mkv");
        host.Drop("hdsd", "Bluey - S01E01.mkv");
        host.Drop("hdsd", "Heat (1995).mkv");
        await OrderAsync(host);

        var rows = (await StatusRowsAsync(host)).ToDictionary(r => r!["fileName"]!.GetValue<string>(), r => r!["priority"]!.GetValue<int>());

        Assert.Equal(-1, rows["Big Show - S01E01.mkv"]);
        Assert.Equal(1, rows["Bluey - S01E01.mkv"]);
        Assert.Equal(0, rows["Heat (1995).mkv"]);
    }

    [Fact]
    public async Task AddingARuleAfterwards_ChangesNothingUntilItIsAppliedToTheWaitingFiles()
    {
        await using var host = await QueueHost.StartAsync(Home);
        host.Drop("hdsd", "Big Show - S01E01.mkv");
        host.Drop("hdsd", "Heat (1995).mkv");
        var before = await OrderAsync(host);
        await PutRulesAsync(host, Lower("Big Show"));

        var unchanged = await OrderAsync(host);
        using var apply = await host.Client.PostAsync("/api/run/queue/priority/apply", null);
        var applied = await OrderAsync(host);

        Assert.Equal(before, unchanged);                          // a new rule never reshuffles on its own
        Assert.Equal(HttpStatusCode.OK, apply.StatusCode);
        Assert.Equal(new[] { "Heat (1995).mkv", "Big Show - S01E01.mkv" }, applied);
        Assert.Equal(2, JsonNode.Parse(await apply.Content.ReadAsStringAsync())!["moved"]!.GetValue<int>());   // both swapped places
    }

    [Fact]
    public async Task ARuleMadeFromARow_UsesTheFilesTitle_AndNormalRemovesIt()
    {
        await using var host = await QueueHost.StartAsync(Home);
        var file = host.Drop("hdsd", "Big Show - S01E03.mkv");

        using var lowered = await host.Client.PostAsJsonAsync("/api/run/queue/priority-rule", new { laneId = "hdsd", fullName = file, priority = "Lower" }, Web);
        var afterLower = JsonNode.Parse(await host.Client.GetStringAsync("/api/queue-priority"))!["rules"]!.AsArray();
        using var changed = await host.Client.PostAsJsonAsync("/api/run/queue/priority-rule", new { laneId = "hdsd", fullName = file, priority = "Higher" }, Web);
        var afterHigher = JsonNode.Parse(await host.Client.GetStringAsync("/api/queue-priority"))!["rules"]!.AsArray();
        using var normal = await host.Client.PostAsJsonAsync("/api/run/queue/priority-rule", new { laneId = "hdsd", fullName = file, priority = "Normal" }, Web);
        var afterNormal = JsonNode.Parse(await host.Client.GetStringAsync("/api/queue-priority"))!["rules"]!.AsArray();

        Assert.Equal(HttpStatusCode.OK, lowered.StatusCode);
        Assert.Equal("Big Show", Assert.Single(afterLower)!["match"]!.GetValue<string>());
        Assert.Equal("Lower", afterLower[0]!["priority"]!.GetValue<string>());
        Assert.Equal("Higher", Assert.Single(afterHigher)!["priority"]!.GetValue<string>());     // the same show: one rule, changed - not a second one
        Assert.Empty(afterNormal);
    }

    [Fact]
    public async Task ARuleMadeFromARowWithATmdbTag_MatchesTheIdNotTheTitle()
    {
        await using var host = await QueueHost.StartAsync(Home);
        var file = host.Drop("hdsd", Path.Combine("Big Show {tmdb-1396}", "Big Show - S01E01.mkv"));

        await host.Client.PostAsJsonAsync("/api/run/queue/priority-rule", new { laneId = "hdsd", fullName = file, priority = "Lower" }, Web);

        var rule = Assert.Single(JsonNode.Parse(await host.Client.GetStringAsync("/api/queue-priority"))!["rules"]!.AsArray())!;
        Assert.Equal(1396, rule["tmdbId"]!.GetValue<int>());
        Assert.Equal("", rule["match"]!.GetValue<string>());
    }
}
