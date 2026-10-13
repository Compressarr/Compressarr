using System.Text.Json.Nodes;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Web.Tests;

/// <summary>"Passthrough (no re-encode)" through the real endpoints: the Lanes page and the queue are offered it, a lane that uses it validates, and the
/// Monitor's rows and status say when a file is being passed through. (The copy itself, History and the report are covered in Core.Tests.)</summary>
public class PassthroughEndpointTests
{
    private static readonly LaneSpec Lane1 = new("hdsd", "SD-HD");

    private static async Task<JsonNode> Get(QueueHost host, string url) => JsonNode.Parse(await host.Client.GetStringAsync(url))!;

    private static void SetPresets(QueueHost host, string? tv, string? movie) =>
        host.Services.GetRequiredService<IConfigStore>().Update(AppPaths.GetConfigFilePath(), config =>
        {
            var lane = config.Lanes.Single(l => l.Id == "hdsd");
            lane.TvPreset = tv ?? "";
            lane.MoviePreset = movie ?? "";
            return 0;
        });

    [Fact]
    public async Task ThePresetList_OffersPassthroughOnlyWhenAsked_ForBothEncoders()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        foreach (var engine in new[] { "", "&engine=ffmpeg" })
        {
            var plain = (await Get(host, "/api/presets?x=1" + engine)).AsArray().Select(n => n!.GetValue<string>()).ToList();
            var withPassthrough = (await Get(host, "/api/presets?includePassthrough=1" + engine)).AsArray().Select(n => n!.GetValue<string>()).ToList();

            Assert.DoesNotContain(PassthroughPreset.Name, plain);                    // the profile editors' fallback lists never see it
            Assert.Equal(PassthroughPreset.Name, withPassthrough.First());           // the Lanes page and the queue see it first
            Assert.Equal(plain, withPassthrough.Skip(1).ToList());                   // and everything else is unchanged
        }
    }

    [Fact]
    public async Task ALaneThatUsesPassthrough_ValidatesCleanly_WhileAMissingPresetStillIsFlagged()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        SetPresets(host, PassthroughPreset.Name, PassthroughPreset.Name);

        var lane = (await Get(host, "/api/lanes")).AsArray().Single(l => l!["id"]!.GetValue<string>() == "hdsd")!;
        Assert.DoesNotContain(lane["validationIssues"]!.AsArray(), i => i!["field"]!.GetValue<string>() is "tvPreset" or "moviePreset");

        SetPresets(host, PassthroughPreset.Name, "A Preset That Does Not Exist");
        lane = (await Get(host, "/api/lanes")).AsArray().Single(l => l!["id"]!.GetValue<string>() == "hdsd")!;
        var issues = lane["validationIssues"]!.AsArray().Select(i => i!["field"]!.GetValue<string>()).ToList();
        Assert.Contains("moviePreset", issues);
        Assert.DoesNotContain("tvPreset", issues);
    }

    [Fact]
    public async Task TheQueueRow_SaysPassthrough_ForAFileThatWillBeCopied_AndOnlyForThat()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        host.Drop("hdsd", "Fight Club (1999).mkv");                                  // a movie
        host.Drop("hdsd", "Breaking Bad - S05E14 - Ozymandias.mkv");                  // a TV episode
        SetPresets(host, tv: "Lane TV Preset", movie: PassthroughPreset.Name);        // only movies pass through

        var rows = (await Get(host, "/api/run/status"))["upNext"]!.AsArray();

        Assert.True(rows.Single(r => r!["fileName"]!.GetValue<string>() == "Fight Club (1999).mkv")!["isPassthrough"]!.GetValue<bool>());
        Assert.False(rows.Single(r => r!["fileName"]!.GetValue<string>().StartsWith("Breaking Bad"))!["isPassthrough"]!.GetValue<bool>());
    }

    [Fact]
    public async Task APerFileOverride_CanMakeASingleFilePassThrough()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var file = host.Drop("hdsd", "Inception (2010).mkv");

        Assert.Equal(System.Net.HttpStatusCode.OK, await host.OverridePresetAsync("hdsd", file, PassthroughPreset.Name));

        var row = Assert.Single((await Get(host, "/api/run/status"))["upNext"]!.AsArray())!;
        Assert.True(row["isPassthrough"]!.GetValue<bool>());
        Assert.Equal(PassthroughPreset.Name, row["preset"]!.GetValue<string>());
    }

    [Fact]
    public async Task ARealRunThroughTheServiceContainer_CopiesAPassthroughFile_WithoutAnyEncoderInstalled()
    {
        // Everything here is the app's real wiring (the service container, the real conversion step, the real run loop) - only the paths are
        // temporary. A passthrough lane must copy the file even though there is no HandBrakeCLI on this machine: if the container handed the
        // conversion step the HandBrake runner for the copy, HandBrake would be started (and fail) instead.
        await using var host = await QueueHost.StartAsync(Lane1);
        SetPresets(host, PassthroughPreset.Name, PassthroughPreset.Name);
        host.Services.GetRequiredService<IConfigStore>().Update(AppPaths.GetConfigFilePath(), config =>
        {
            config.Processing.DeleteAfterConvert = DeleteAfterConvertMode.Maintain;   // never touch the real Recycle Bin from a test
            return 0;
        });
        Directory.CreateDirectory(Path.Combine(host.Root, "hdsd", "Movies"));   // the library folder Compressarr files into (it never creates this one itself)
        var source = host.Drop("hdsd", "Some Film (2020).mkv");

        using var started = await host.Client.PostAsync("/api/run/run-now", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, started.StatusCode);

        string? copy = null;
        for (var i = 0; i < 300 && copy is null; i++)
        {
            copy = Directory.GetFiles(Path.Combine(host.Root, "hdsd"), "Some Film (2020).mkv", SearchOption.AllDirectories)
                .FirstOrDefault(f => !string.Equals(f, source, StringComparison.OrdinalIgnoreCase));
            if (copy is null) await Task.Delay(100);
        }

        var logLines = string.Join(" | ", (await Get(host, "/api/run/status"))["recentLogLines"]!.AsArray().Select(l => l!["text"]!.GetValue<string>()));
        Assert.True(copy is not null, "the copy was never filed. Run log: " + logLines);         // the copy was filed
        Assert.Equal("x", await File.ReadAllTextAsync(copy!));
        Assert.True(File.Exists(source));                                  // Maintain: the source is still there
        for (var i = 0; i < 100 && (await Get(host, "/api/run/status"))["isRunning"]!.GetValue<bool>(); i++) await Task.Delay(50);   // let the pass finish
    }
}
