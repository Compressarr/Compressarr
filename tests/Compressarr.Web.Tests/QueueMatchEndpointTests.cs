using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Web.Tests;

/// <summary>The queue card's Match / Fix match / Restore through the real endpoints and the real queue, with TMDB answered by canned JSON. (The renaming rules themselves are covered in Core.Tests.)</summary>
public class QueueMatchEndpointTests
{
    private static readonly LaneSpec Lane1 = new("hdsd", "SD-HD");

    private const string HeatJson = """{"id":949,"title":"Heat","original_title":"Heat","release_date":"1995-12-15","popularity":16.2,"overview":"A crew of thieves.","poster_path":"/heat123456789.jpg"}""";
    private const string TheHeatJson = """{"id":136795,"title":"The Heat","original_title":"The Heat","release_date":"2013-06-27","popularity":4.1,"overview":"Two cops.","poster_path":null}""";
    private const string DoctorWhoJson = """{"id":57243,"name":"Doctor Who","original_name":"Doctor Who","first_air_date":"2005-03-26","origin_country":["GB"],"popularity":1700,"overview":"A traveller."}""";

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>What TMDB says to the few questions these tests ask.</summary>
    private static HttpResponseMessage? Tmdb(Uri uri) => uri.AbsolutePath switch
    {
        "/3/search/movie" => Json($"{{\"results\":[{HeatJson},{TheHeatJson}]}}"),
        "/3/movie/949" => Json(HeatJson),
        "/3/movie/136795" => Json(TheHeatJson),
        "/3/search/tv" => Json($"{{\"results\":[{DoctorWhoJson}]}}"),
        "/3/tv/57243" => Json(DoctorWhoJson),
        "/3/tv/57243/season/1" => Json("""{"episodes":[{"episode_number":1,"name":"Rose"},{"episode_number":2,"name":"The End of the World"},{"episode_number":3,"name":"The Unquiet Dead"}]}"""),
        _ when uri.Host == "image.tmdb.org" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 }) },
        _ => null
    };

    private static async Task<JsonNode> Status(QueueHost host) => JsonNode.Parse(await host.Client.GetStringAsync("/api/run/status"))!;

    private static async Task SaveKey(QueueHost host, string key = "test-key")
    {
        var settings = JsonNode.Parse(await host.Client.GetStringAsync("/api/settings"))!.AsObject();
        settings["posters"] = new JsonObject { ["enabled"] = false, ["tmdbApiKey"] = key, ["size"] = "Medium", ["smallerImages"] = false };
        using var response = await host.Client.PutAsync("/api/settings", new StringContent(settings.ToJsonString(), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    private static async Task<JsonNode> Post(QueueHost host, string url, object body)
    {
        using var response = await host.Client.PostAsync(url, new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    /// <summary>The queue only knows a file once something has polled it, and the Unmatched badge is stamped by a renaming pass: do both by hand.</summary>
    private static async Task MarkUnmatchedAsync(QueueHost host, params string[] paths)
    {
        await host.PollAsync();
        host.Services.GetRequiredService<IResumeStateStore>().Update(AppPaths.GetResumeFilePath(), state =>
        {
            foreach (var path in paths) state.Single(e => e.FullName == path).FileBotUnmatched = true;
            return true;
        });
    }

    private static JsonNode? Row(JsonNode status, string fileName) =>
        status["upNext"]!.AsArray().FirstOrDefault(i => i!["fileName"]!.GetValue<string>() == fileName);

    [Fact]
    public async Task TheStatusSaysWhetherMatchCanWork_AKeyIsAllItNeeds()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Assert.False((await Status(host))["canMatch"]!.GetValue<bool>());

        await SaveKey(host);

        Assert.True((await Status(host))["canMatch"]!.GetValue<bool>());
    }

    [Fact]
    public async Task WithNoKey_TheDialogIsToldToGetOne_AndTmdbIsNotAsked()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var path = host.Drop("hdsd", "Heat.mkv");
        await MarkUnmatchedAsync(host, path);

        var answer = await Post(host, "/api/run/queue/match/candidates", new { laneId = "hdsd", fullName = path });

        Assert.False(answer["success"]!.GetValue<bool>());
        Assert.Contains("TMDB API key", answer["message"]!.GetValue<string>());
        Assert.DoesNotContain(host.Http.Requests, u => u.Host.Contains("themoviedb"));
    }

    [Fact]
    public async Task AnUnmatchedFile_IsMatched_FixedByRestoring_AndMatchedAgain()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        host.Http.Responder = Tmdb;
        await SaveKey(host);
        var first = host.Drop("hdsd", "Aaa.mkv");
        var path = host.Drop("hdsd", "Heat.mkv");
        await MarkUnmatchedAsync(host, path);
        Assert.True(Row(await Status(host), "Heat.mkv")!["isFileBotUnmatched"]!.GetValue<bool>());

        // The dialog: what to search for, and what TMDB found.
        var candidates = await Post(host, "/api/run/queue/match/candidates", new { laneId = "hdsd", fullName = path });
        Assert.True(candidates["success"]!.GetValue<bool>());
        Assert.Equal("Heat", candidates["suggestion"]!["title"]!.GetValue<string>());
        Assert.False(candidates["isFix"]!.GetValue<bool>());
        Assert.Equal(new[] { 949, 136795 }, candidates["candidates"]!.AsArray().Select(c => c!["id"]!.GetValue<int>()));
        Assert.Equal("A crew of thieves.", candidates["candidates"]![0]!["overview"]!.GetValue<string>());

        // Pick Heat (1995).
        var applied = await Post(host, "/api/run/queue/match/apply", new { laneId = "hdsd", fullName = path, tmdbId = 949, kind = "movie" });
        Assert.True(applied["success"]!.GetValue<bool>(), applied.ToJsonString());
        var renamed = Path.Combine(Path.GetDirectoryName(path)!, "Heat (1995).mkv");
        Assert.True(File.Exists(renamed));
        Assert.False(File.Exists(path));

        var status = await Status(host);
        Assert.Null(Row(status, "Heat.mkv"));
        var row = Row(status, "Heat (1995).mkv")!;
        Assert.False(row["isFileBotUnmatched"]!.GetValue<bool>());
        Assert.True(row["isRenamed"]!.GetValue<bool>());                                    // so the menu now offers Fix match
        Assert.Equal(new[] { "Aaa.mkv", "Heat (1995).mkv" }, (await host.PollNamesAsync()).Order());   // the same place in the list

        // Fix match: the same dialog, now knowing what the file was called, and offering the original name back.
        var again = await Post(host, "/api/run/queue/match/candidates", new { laneId = "hdsd", fullName = renamed });
        Assert.True(again["isFix"]!.GetValue<bool>());
        Assert.Equal("Heat.mkv", again["originalName"]!.GetValue<string>());
        Assert.Equal(949, again["currentTmdbId"]!.GetValue<int>());

        var restored = await Post(host, "/api/run/queue/match/restore", new { laneId = "hdsd", fullName = renamed });
        Assert.True(restored["success"]!.GetValue<bool>(), restored.ToJsonString());
        Assert.True(File.Exists(path));
        var afterRestore = Row(await Status(host), "Heat.mkv")!;
        Assert.True(afterRestore["isFileBotUnmatched"]!.GetValue<bool>());
        Assert.False(afterRestore["isRenamed"]!.GetValue<bool>());

        // And it can be matched again, to a different film this time.
        var other = await Post(host, "/api/run/queue/match/apply", new { laneId = "hdsd", fullName = path, tmdbId = 136795, kind = "movie" });
        Assert.True(other["success"]!.GetValue<bool>());
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "The Heat (2013).mkv")));
        Assert.True(File.Exists(first));
    }

    [Fact]
    public async Task AChoiceForAShow_CanCarryToItsOtherWaitingEpisodes()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        host.Http.Responder = Tmdb;
        await SaveKey(host);
        var one = host.Drop("hdsd", "Doctor.Who.S01E01.720p.mkv");
        var two = host.Drop("hdsd", "Doctor.Who.S01E02.720p.mkv");
        var three = host.Drop("hdsd", "Doctor.Who.S01E03.720p.mkv");
        await MarkUnmatchedAsync(host, one, two, three);

        var candidates = await Post(host, "/api/run/queue/match/candidates", new { laneId = "hdsd", fullName = one });
        Assert.Equal(2, candidates["otherFiles"]!.AsArray().Count);
        Assert.True(candidates["suggestion"]!["isTv"]!.GetValue<bool>());

        var applied = await Post(host, "/api/run/queue/match/apply", new { laneId = "hdsd", fullName = one, tmdbId = 57243, kind = "tv", season = 1, episodes = new[] { 1 }, applyToOthers = true });

        Assert.True(applied["success"]!.GetValue<bool>(), applied.ToJsonString());
        Assert.Equal(2, applied["othersRenamed"]!.GetValue<int>());
        Assert.Equal(new[] { "Doctor Who (2005) - S01E01 - Rose.mkv", "Doctor Who (2005) - S01E02 - The End of the World.mkv", "Doctor Who (2005) - S01E03 - The Unquiet Dead.mkv" },
            (await host.PollNamesAsync()).Order());
        Assert.All((await Status(host))["upNext"]!.AsArray(), row => Assert.False(row!["isFileBotUnmatched"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task AnUnmatchedFile_ShowsAsHeld_OnlyWhileTheSettingIsOn_AndLeaveAsIsReleasesIt()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        await SaveKey(host);
        var unmatched = host.Drop("hdsd", "mystery.mkv");
        host.Drop("hdsd", "Matched (2000).mkv");
        await MarkUnmatchedAsync(host, unmatched);
        Assert.False(Row(await Status(host), "mystery.mkv")!["isHeld"]!.GetValue<bool>());          // off: today's behaviour

        var settings = JsonNode.Parse(await host.Client.GetStringAsync("/api/settings"))!.AsObject();
        settings["holdUnmatchedFiles"] = true;
        using (var put = await host.Client.PutAsync("/api/settings", new StringContent(settings.ToJsonString(), Encoding.UTF8, "application/json"))) put.EnsureSuccessStatusCode();

        var status = await Status(host);
        Assert.True(Row(status, "mystery.mkv")!["isHeld"]!.GetValue<bool>());
        Assert.True(Row(status, "mystery.mkv")!["isFileBotUnmatched"]!.GetValue<bool>());
        Assert.False(Row(status, "Matched (2000).mkv")!["isHeld"]!.GetValue<bool>());

        await Post(host, "/api/run/queue/match/leave", new { laneId = "hdsd", fullName = unmatched });

        Assert.False(Row(await Status(host), "mystery.mkv")!["isHeld"]!.GetValue<bool>());           // released
    }

    [Fact]
    public async Task LeaveAsIsClearsTheBadge()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        await SaveKey(host);
        var path = host.Drop("hdsd", "home-video.mkv");
        await MarkUnmatchedAsync(host, path);

        var answer = await Post(host, "/api/run/queue/match/leave", new { laneId = "hdsd", fullName = path });

        Assert.True(answer["success"]!.GetValue<bool>());
        Assert.False(Row(await Status(host), "home-video.mkv")!["isFileBotUnmatched"]!.GetValue<bool>());
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ApplyingToAFileThatIsGone_IsRefusedPlainly()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        host.Http.Responder = Tmdb;
        await SaveKey(host);

        var answer = await Post(host, "/api/run/queue/match/apply", new { laneId = "hdsd", fullName = Path.Combine(host.Root, "hdsd", "Input", "gone.mkv"), tmdbId = 949, kind = "movie" });

        Assert.False(answer["success"]!.GetValue<bool>());
        Assert.Contains("no longer there", answer["message"]!.GetValue<string>());
    }

    // ---- the pictures ----

    [Fact]
    public async Task ACandidatesPictureComesThroughTheApp_FromTmdbsImageServerOnly()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        host.Http.Responder = Tmdb;

        using var response = await host.Client.GetAsync("/api/rename/poster?size=w92&path=%2Fheat123456789.jpg");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 }, await response.Content.ReadAsByteArrayAsync());
        var asked = Assert.Single(host.Http.Requests);
        Assert.Equal("https://image.tmdb.org/t/p/w92/heat123456789.jpg", asked.ToString());
    }

    [Theory]
    [InlineData("/api/rename/poster?size=w92&path=%2F..%2F..%2Fsecret.jpg")]
    [InlineData("/api/rename/poster?size=w92&path=http%3A%2F%2Fevil.example%2Fx.jpg")]
    [InlineData("/api/rename/poster?size=w92&path=%2Fheat123456789.exe")]
    [InlineData("/api/rename/poster?size=original&path=%2Fheat123456789.jpg")]
    [InlineData("/api/rename/poster?path=%2Fheat123456789.jpg")]
    [InlineData("/api/rename/poster?size=w92")]
    public async Task APictureRequestThatIsNotAPlainPosterPath_IsRefused_WithoutAskingAnyone(string url)
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        host.Http.Responder = Tmdb;

        using var response = await host.Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(host.Http.Requests);
    }
}
