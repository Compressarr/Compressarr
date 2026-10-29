using System.Text.Json.Nodes;

namespace Compressarr.Web.Tests;

/// <summary>The Monitor page shows a film strip (movie) or a TV (show) beside each queued file's name and in the corner of the Current status card. The server says which
/// one a file is (the same decision that picks the lane's TV or movie preset); the drawing itself was checked in the real page, these keep the pieces from being lost.</summary>
public class QueueKindIconTests
{
    private static readonly LaneSpec Lane1 = new("hdsd", "SD-HD");

    private static string Script(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", name);
        Assert.True(File.Exists(path), $"{name} is not in the test output");
        return File.ReadAllText(path);
    }

    [Fact]
    public async Task TheQueueRows_SayWhetherAFileIsTvOrAMovie_ByItsName()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        host.Drop("hdsd", "Fight Club (1999).mkv");
        host.Drop("hdsd", "Breaking Bad - S05E14 - Ozymandias.mkv");
        host.Drop("hdsd", "Holiday Video 1920x1080.mkv");                        // a resolution is not an episode, and there is no (year)
        host.Drop("hdsd", "Beach Day.mkv");

        var rows = JsonNode.Parse(await host.Client.GetStringAsync("/api/run/status"))!["upNext"]!.AsArray();

        string Kind(string name) => rows.Single(r => r!["fileName"]!.GetValue<string>().StartsWith(name))!["kind"]!.GetValue<string>();
        Assert.Equal("movie", Kind("Fight Club"));
        Assert.Equal("tv", Kind("Breaking Bad"));
        Assert.Equal("video", Kind("Holiday Video"));
        Assert.Equal("video", Kind("Beach Day"));
    }

    [Fact]
    public void ThePage_DrawsTheIconBesideTheName_AndInTheCardCorner_AndOnlyWhileAFileIsRunning()
    {
        var script = Script("monitor.js");
        Assert.Contains("${kindIconHtml(item.kind)}${escapeHtml(item.fileName)}", script);            // inside the name's own box, so it stays glued to the name
        Assert.Contains("const showKind = s.isRunning && !s.isRenaming && !!s.fileName;", script);     // nothing on an idle card or while FileBot renames
        Assert.Contains("id=\"jobKind\"", Script("monitor.html"));
        var css = Script("styles.css");
        Assert.Contains(".kind-icon { width: 1em; height: 1em;", css);                                // the size of the text beside it
        Assert.Contains(".job-kind { position: absolute; top: 12px; right: 14px;", css);              // the card's top right corner
    }
}
