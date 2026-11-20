using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Web.Tests;

/// <summary>Who gets "Match..." and "Fix match..." on the queue's menu (3.2). Every waiting file in an ordinary lane does; only the dedicated Videos lane (and a row held after a failed encode) does not.
/// The rule once read the row's <c>kind</c>, which says "video" for ANY file whose name has neither a year nor an episode number - so Heat.mkv in a movie lane, the very file the strict renamer
/// leaves Unmatched, lost its Match. These run the real server and the real page script on exactly those rows.</summary>
public class QueueMatchMenuTests
{
    private static readonly LaneSpec Movies = new("hdsd", "SD-HD");
    private static readonly LaneSpec HomeVideos = new("home", "Home videos", Kind: LaneKind.Videos);

    private const string HeatJson = """{"id":949,"title":"Heat","original_title":"Heat","release_date":"1995-12-15","popularity":16.2,"overview":"A crew of thieves.","poster_path":null}""";

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage? Tmdb(Uri uri) => uri.AbsolutePath switch
    {
        "/3/search/movie" => Json($"{{\"results\":[{HeatJson}]}}"),
        "/3/movie/949" => Json(HeatJson),
        _ => null
    };

    private static string Script(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", name);
        Assert.True(File.Exists(path), $"{name} is not in the test output");
        return File.ReadAllText(path);
    }

    private static async Task Configure(QueueHost host, bool hold)
    {
        var settings = JsonNode.Parse(await host.Client.GetStringAsync("/api/settings"))!.AsObject();
        settings["posters"] = new JsonObject { ["enabled"] = false, ["tmdbApiKey"] = "test-key", ["size"] = "Medium", ["smallerImages"] = false };
        settings["holdUnmatchedFiles"] = hold;
        using var response = await host.Client.PutAsync("/api/settings", new StringContent(settings.ToJsonString(), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    private static async Task<JsonNode> Post(QueueHost host, string url, object body)
    {
        using var response = await host.Client.PostAsync(url, new StringContent(System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static async Task<JsonNode> Status(QueueHost host) => JsonNode.Parse(await host.Client.GetStringAsync("/api/run/status"))!;

    private static JsonNode Row(JsonNode status, string fileName) => status["upNext"]!.AsArray().Single(i => i!["fileName"]!.GetValue<string>() == fileName)!;

    /// <summary>The queue only knows a file once something has polled it, and the Unmatched badge is stamped by a renaming pass: do both by hand.</summary>
    private static async Task MarkUnmatchedAsync(QueueHost host, string path)
    {
        await host.PollAsync();
        host.Services.GetRequiredService<IResumeStateStore>().Update(AppPaths.GetResumeFilePath(), state =>
        {
            state.Single(e => e.FullName == path).FileBotUnmatched = true;
            return true;
        });
    }

    /// <summary>The text of one top-level function of monitor.js (the page's own code, not a copy), found by matching its braces.</summary>
    private static string FunctionSource(string script, string name)
    {
        var start = script.IndexOf($"function {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"monitor.js has no function {name}");
        var open = script.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < script.Length; i++)
        {
            if (script[i] == '{') depth++;
            else if (script[i] == '}' && --depth == 0) return script[start..(i + 1)];
        }

        throw new InvalidOperationException($"unbalanced braces in {name}");
    }

    /// <summary>Runs <paramref name="program"/> in Node, or returns null when Node is not installed (the text checks below still guard the rule then).</summary>
    private static string? RunNode(string program)
    {
        try
        {
            using var node = Process.Start(new ProcessStartInfo("node", "-") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false) })!;
            node.StandardInput.Write(program);
            node.StandardInput.Close();
            var output = node.StandardOutput.ReadToEnd();
            var error = node.StandardError.ReadToEnd();
            node.WaitForExit();
            Assert.True(node.ExitCode == 0, error);
            return output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [Fact]
    public void ThePagesRule_NamesTheVideosLane_NotTheNameBasedKind()
    {
        var rule = FunctionSource(Script("monitor.js"), "canMatchRow");

        Assert.Contains("isVideosLane", rule);
        Assert.DoesNotContain(".kind", rule);              // "kind" is "video" for any file with no year or episode number
        Assert.Contains("isError", rule);
    }

    [Fact]
    public async Task EveryWaitingFileInAnOrdinaryLane_GetsAMatchAction_AndOnlyTheVideosLaneDoesNot()
    {
        await using var host = await QueueHost.StartAsync(Movies, HomeVideos);
        await Configure(host, hold: true);
        var heat = host.Drop("hdsd", "Heat.mkv");                                    // no year: the strict renamer leaves it Unmatched
        host.Drop("hdsd", "Beach Day.mkv");                                          // no year, and left as it is (not flagged)
        host.Drop("hdsd", "Fight Club (1999).mkv");                                  // correctly named
        host.Drop("hdsd", "Breaking Bad - S05E14 - Ozymandias.mkv");                // an episode
        host.Drop("home", "Clip.mkv");                                               // in the dedicated Videos lane
        await MarkUnmatchedAsync(host, heat);
        var rows = (await Status(host))["upNext"]!.AsArray();

        // What the server says about the two rows the reviewer named.
        var yearless = Row(await Status(host), "Heat.mkv");
        Assert.Equal("video", yearless["kind"]!.GetValue<string>());                 // the name-based kind ...
        Assert.False(yearless["isVideosLane"]!.GetValue<bool>());                    // ... in an ordinary lane
        Assert.True(yearless["isFileBotUnmatched"]!.GetValue<bool>());
        Assert.True(yearless["isHeld"]!.GetValue<bool>());
        Assert.True(Row(await Status(host), "Clip.mkv")["isVideosLane"]!.GetValue<bool>());

        // What the page does with exactly those rows: the page's own functions, run on the server's own JSON.
        var program = $$"""
            let canMatch = true;
            {{FunctionSource(Script("monitor.js"), "canMatchRow")}}
            {{FunctionSource(Script("monitor.js"), "matchMenuItem")}}
            const rows = {{rows.ToJsonString()}};
            const out = {};
            for (const r of rows) out[r.fileName] = matchMenuItem(r);
            canMatch = false;
            out['__no key__'] = matchMenuItem(rows[0]);
            out['__error row__'] = (() => { canMatch = true; return matchMenuItem({ ...rows[0], isError: true }); })();
            console.log(JSON.stringify(out));
            """;
        var result = RunNode(program);
        if (result is null) return;                                                  // no Node here: the text check above still holds the rule

        var menu = JsonNode.Parse(result)!;
        Assert.Equal("match", menu["Heat.mkv"]!["act"]!.GetValue<string>());          // the held, yearless, Unmatched movie: Match...
        Assert.Equal("Match…", menu["Heat.mkv"]!["label"]!.GetValue<string>());
        Assert.Equal("fix-match", menu["Beach Day.mkv"]!["act"]!.GetValue<string>()); // a yearless file left as it is: Fix match...
        Assert.Equal("Fix match…", menu["Beach Day.mkv"]!["label"]!.GetValue<string>());
        Assert.Equal("fix-match", menu["Fight Club (1999).mkv"]!["act"]!.GetValue<string>());
        Assert.Equal("fix-match", menu["Breaking Bad - S05E14 - Ozymandias.mkv"]!["act"]!.GetValue<string>());
        Assert.Null(menu["Clip.mkv"]);                                               // the Videos lane: never renamed
        Assert.Null(menu["__no key__"]);                                             // and nothing at all without a TMDB key
        Assert.Null(menu["__error row__"]);                                          // nor on a file held after a failed encode
    }

    [Fact]
    public async Task AHeldYearlessMovie_ReachesTheSearchDialog_AndIsMatchedSuccessfully()
    {
        await using var host = await QueueHost.StartAsync(Movies);
        host.Http.Responder = Tmdb;
        await Configure(host, hold: true);
        var path = host.Drop("hdsd", "Heat.mkv");
        await MarkUnmatchedAsync(host, path);
        Assert.True(Row(await Status(host), "Heat.mkv")["isHeld"]!.GetValue<bool>());

        var candidates = await Post(host, "/api/run/queue/match/candidates", new { laneId = "hdsd", fullName = path });
        Assert.True(candidates["success"]!.GetValue<bool>(), candidates.ToJsonString());
        Assert.Equal("Heat", candidates["suggestion"]!["title"]!.GetValue<string>());
        Assert.Equal(949, candidates["candidates"]![0]!["id"]!.GetValue<int>());

        var applied = await Post(host, "/api/run/queue/match/apply", new { laneId = "hdsd", fullName = path, tmdbId = 949, kind = "movie" });

        Assert.True(applied["success"]!.GetValue<bool>(), applied.ToJsonString());
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "Heat (1995).mkv")));
        var row = Row(await Status(host), "Heat (1995).mkv");
        Assert.False(row["isFileBotUnmatched"]!.GetValue<bool>());
        Assert.False(row["isHeld"]!.GetValue<bool>());                               // matched, so it is no longer waiting for a person
    }

    [Fact]
    public async Task AYearlessFileLeftAsIs_CanStillBeMatchedLater()
    {
        await using var host = await QueueHost.StartAsync(Movies);
        host.Http.Responder = Tmdb;
        await Configure(host, hold: false);
        var path = host.Drop("hdsd", "Heat.mkv");
        await MarkUnmatchedAsync(host, path);
        Assert.True((await Post(host, "/api/run/queue/match/leave", new { laneId = "hdsd", fullName = path }))["success"]!.GetValue<bool>());
        Assert.False(Row(await Status(host), "Heat.mkv")["isFileBotUnmatched"]!.GetValue<bool>());

        var applied = await Post(host, "/api/run/queue/match/apply", new { laneId = "hdsd", fullName = path, tmdbId = 949, kind = "movie" });

        Assert.True(applied["success"]!.GetValue<bool>(), applied.ToJsonString());
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "Heat (1995).mkv")));
    }

    [Fact]
    public async Task AVideosLanesFile_IsRefusedByTheServerToo_SoTheMenuAndTheServerAgree()
    {
        await using var host = await QueueHost.StartAsync(HomeVideos);
        host.Http.Responder = Tmdb;
        await Configure(host, hold: false);
        var path = host.Drop("home", "Heat.mkv");
        await host.PollAsync();

        var candidates = await Post(host, "/api/run/queue/match/candidates", new { laneId = "home", fullName = path });

        Assert.False(candidates["success"]!.GetValue<bool>());
        Assert.Contains("Videos lane", candidates["message"]!.GetValue<string>());
        Assert.True(File.Exists(path));
    }
}
