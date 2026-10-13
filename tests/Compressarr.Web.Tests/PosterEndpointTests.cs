using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Compressarr.Core.Config;
using Compressarr.Core.Posters;

namespace Compressarr.Web.Tests;

/// <summary>Settings > Queue Posters through the real endpoints: off by default, the settings round trip, the queue rows and the current
/// status only carry poster ids when the feature is on, and nothing reaches TMDb unless it is. (The lookup and cache rules themselves are
/// covered in Core.Tests; the test host answers every outgoing request with a recorded 404.)</summary>
public class PosterEndpointTests
{
    private static readonly LaneSpec Lane1 = new("hdsd", "SD-HD");

    private static async Task<JsonObject> Settings(QueueHost host) =>
        JsonNode.Parse(await host.Client.GetStringAsync("/api/settings"))!.AsObject();

    private static async Task Save(QueueHost host, JsonObject settings)
    {
        using var response = await host.Client.PutAsync("/api/settings", new StringContent(settings.ToJsonString(), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    private static async Task Turn(QueueHost host, bool enabled, string key = "test-key", string size = "Medium", bool smaller = false)
    {
        var settings = await Settings(host);
        settings["posters"] = new JsonObject { ["enabled"] = enabled, ["tmdbApiKey"] = key, ["size"] = size, ["smallerImages"] = smaller };
        await Save(host, settings);
    }

    /// <summary>Remembers a poster for a file name as if TMDb had already been asked, so no lookup is needed.</summary>
    private static string Seed(string fileName, string posterPath = "/abcdef12345.jpg")
    {
        var query = PosterNameParser.Parse(fileName)!;
        var directory = AppPaths.GetPostersDirectory();
        Directory.CreateDirectory(directory);
        var index = new Dictionary<string, object>
        {
            [query.Id] = new { Title = query.Title, Year = query.Year, IsTv = query.IsTv, PosterPath = posterPath, CheckedUtc = DateTimeOffset.UtcNow }
        };
        File.WriteAllText(Path.Combine(directory, "index.json"), JsonSerializer.Serialize(index));
        return query.Id;
    }

    private static async Task<JsonNode> Status(QueueHost host) => JsonNode.Parse(await host.Client.GetStringAsync("/api/run/status"))!;

    private static int TmdbRequests(QueueHost host) => host.Http.Requests.Count(u => u.Host == "api.themoviedb.org");

    [Fact]
    public async Task Posters_AreOffByDefault_TheChoicesAreKept_AndAnOlderPageDoesNotWipeThem()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        var posters = (await Settings(host))["posters"]!;
        Assert.False(posters["enabled"]!.GetValue<bool>());
        Assert.Equal("", posters["tmdbApiKey"]!.GetValue<string>());
        Assert.Equal("Medium", posters["size"]!.GetValue<string>());
        Assert.False(posters["smallerImages"]!.GetValue<bool>());

        await Turn(host, true, key: "  my-key  ", size: "Large", smaller: true);
        posters = (await Settings(host))["posters"]!;
        Assert.True(posters["enabled"]!.GetValue<bool>());
        Assert.Equal("my-key", posters["tmdbApiKey"]!.GetValue<string>());     // trimmed
        Assert.Equal("Large", posters["size"]!.GetValue<string>());
        Assert.True(posters["smallerImages"]!.GetValue<bool>());

        var older = await Settings(host);
        older.Remove("posters");                                                // a cached page from before posters existed
        await Save(host, older);
        Assert.True((await Settings(host))["posters"]!["enabled"]!.GetValue<bool>());   // untouched

        await Turn(host, true, size: "Gigantic");
        Assert.Equal("Medium", (await Settings(host))["posters"]!["size"]!.GetValue<string>());   // an unknown size falls back
    }

    [Fact]
    public async Task TurnedOnWithoutAKey_IsFlaggedOnTheKeyField()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        await Turn(host, true, key: "");

        var issues = (await Settings(host))["validationIssues"]!.AsArray();
        Assert.Contains(issues, i => i!["field"]!.GetValue<string>() == "tmdbApiKey");

        await Turn(host, true, key: "k");
        Assert.DoesNotContain((await Settings(host))["validationIssues"]!.AsArray(), i => i!["field"]!.GetValue<string>() == "tmdbApiKey");
    }

    [Fact]
    public async Task WhenOff_TheQueueHasNoPosters_AndTmdbIsNeverAsked()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        host.Drop("hdsd", "Fight Club (1999).mkv");
        Seed("Fight Club (1999).mkv");

        var status = await Status(host);

        Assert.False(status["posters"]!["enabled"]!.GetValue<bool>());
        Assert.Null(status["upNext"]![0]!["posterKey"]);
        Assert.Equal(0, TmdbRequests(host));
    }

    [Fact]
    public async Task WhenOn_ARowCarriesItsPosterId_AndAFileThatCannotBeIdentifiedCarriesNone()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        host.Drop("hdsd", "Fight Club (1999).mkv");
        host.Drop("hdsd", "Family Vacation 2019.mp4");
        var id = Seed("Fight Club (1999).mkv");
        await Turn(host, true, size: "Small", smaller: true);

        var status = await Status(host);

        Assert.True(status["posters"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("Small", status["posters"]!["size"]!.GetValue<string>());
        Assert.True(status["posters"]!["smaller"]!.GetValue<bool>());
        var rows = status["upNext"]!.AsArray();
        Assert.Equal(id, rows.Single(r => r!["fileName"]!.GetValue<string>() == "Fight Club (1999).mkv")!["posterKey"]!.GetValue<string>());
        Assert.Null(rows.Single(r => r!["fileName"]!.GetValue<string>() == "Family Vacation 2019.mp4")!["posterKey"]);
        Assert.Equal(0, TmdbRequests(host));      // the one that can be identified was already known; the other is never looked up
    }

    [Fact]
    public async Task WhenOn_ATitleNotKnownYet_IsLookedUpInTheBackground_WithoutHoldingUpTheStatus()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        host.Drop("hdsd", "Inception (2010).mkv");
        await Turn(host, true);

        var status = await Status(host);            // answers at once, with no poster yet
        Assert.Null(status["upNext"]![0]!["posterKey"]);

        for (var i = 0; i < 100 && TmdbRequests(host) == 0; i++) await Task.Delay(20);
        Assert.Equal(1, TmdbRequests(host));
        Assert.Contains("query=Inception", host.Http.Requests.First(u => u.Host == "api.themoviedb.org").Query);
    }

    [Fact]
    public async Task ThePosterPicture_IsServedFromTheLocalCache_OnlyWhileOn_AndOnlyForKnownIds()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var id = Seed("Fight Club (1999).mkv", "/abcdef12345.jpg");
        var cached = Path.Combine(AppPaths.GetPostersDirectory(), "w154");
        Directory.CreateDirectory(cached);
        await File.WriteAllBytesAsync(Path.Combine(cached, "abcdef12345.jpg"), new byte[] { 0xFF, 0xD8, 0xFF, 1, 2, 3 });

        using (var off = await host.Client.GetAsync($"/api/posters/{id}?size=w154"))
        {
            Assert.Equal(HttpStatusCode.NotFound, off.StatusCode);                  // off: nothing is served
        }

        await Turn(host, true);

        using var on = await host.Client.GetAsync($"/api/posters/{id}?size=w154");
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.Equal("image/jpeg", on.Content.Headers.ContentType!.MediaType);
        Assert.Equal(6, (await on.Content.ReadAsByteArrayAsync()).Length);
        Assert.Contains("max-age", on.Headers.CacheControl!.ToString());

        using var unknown = await host.Client.GetAsync("/api/posters/0000000000000000?size=w154");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var badSize = await host.Client.GetAsync($"/api/posters/{id}?size=original");
        Assert.Equal(HttpStatusCode.NotFound, badSize.StatusCode);
        Assert.DoesNotContain(host.Http.Requests, u => u.Host == "image.tmdb.org");    // it was already cached: nothing downloaded
    }

    [Fact]
    public async Task TheTestButton_ChecksTheTypedKeyWithTmdb_AndSaysWhatHappened()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        using var response = await host.Client.PostAsync("/api/posters/test", new StringContent("""{ "apiKey": "typed-key" }""", Encoding.UTF8, "application/json"));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.False(body["success"]!.GetValue<bool>());       // the test host answers 404, so TMDb "did not accept" it
        Assert.False(string.IsNullOrWhiteSpace(body["message"]!.GetValue<string>()));
        var request = Assert.Single(host.Http.Requests);
        Assert.Equal("/3/authentication", request.AbsolutePath);
        Assert.Contains("api_key=typed-key", request.Query);
    }

    [Fact]
    public async Task TheCacheLine_AndClearButton_Work()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var id = Seed("Fight Club (1999).mkv");
        var cached = Path.Combine(AppPaths.GetPostersDirectory(), "w154");
        Directory.CreateDirectory(cached);
        await File.WriteAllBytesAsync(Path.Combine(cached, "abcdef12345.jpg"), new byte[] { 1, 2, 3, 4 });

        var info = JsonNode.Parse(await host.Client.GetStringAsync("/api/posters/info"))!;
        Assert.Equal(1, info["posters"]!.GetValue<int>());
        Assert.Equal(4, info["bytes"]!.GetValue<long>());

        using var cleared = await host.Client.PostAsync("/api/posters/clear", null);
        Assert.Equal(1, JsonNode.Parse(await cleared.Content.ReadAsStringAsync())!["removed"]!.GetValue<int>());
        Assert.Equal(0, JsonNode.Parse(await host.Client.GetStringAsync("/api/posters/info"))!["posters"]!.GetValue<int>());
        Assert.NotNull(id);
    }
}
