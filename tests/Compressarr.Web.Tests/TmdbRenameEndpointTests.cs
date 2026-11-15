using System.Text;
using System.Text.Json.Nodes;
using Compressarr.Core.Config;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Web.Tests;

/// <summary>Settings > Rename with TMDB through the real endpoints: off by default, the choices round trip next to the (untouched) FileBot ones, an older page that does not send them leaves them
/// alone, and the preview refuses to run without the TMDB key. (The matching and renaming rules themselves are covered in Core.Tests.)</summary>
public class TmdbRenameEndpointTests
{
    private static readonly LaneSpec Lane1 = new("hdsd", "SD-HD");

    private static async Task<JsonObject> Settings(QueueHost host) =>
        JsonNode.Parse(await host.Client.GetStringAsync("/api/settings"))!.AsObject();

    private static async Task Save(QueueHost host, JsonObject settings)
    {
        using var response = await host.Client.PutAsync("/api/settings", new StringContent(settings.ToJsonString(), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    private static CompressarrConfig Saved(QueueHost host) =>
        host.Services.GetRequiredService<IConfigStore>().Load(AppPaths.GetConfigFilePath());

    [Fact]
    public async Task ItIsOffByDefault_WithEnglishAndSeasonEpisodeNumbering()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        var rename = (await Settings(host))["tmdbRename"]!.AsObject();

        Assert.False(rename["enabled"]!.GetValue<bool>());
        Assert.Equal("en-US", rename["language"]!.GetValue<string>());
        Assert.Equal("S00E00", rename["numbering"]!.GetValue<string>());
        Assert.False(rename["addMovieTmdbId"]!.GetValue<bool>());
    }

    [Fact]
    public async Task TheChoicesRoundTrip()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var settings = await Settings(host);
        settings["tmdbRename"] = new JsonObject { ["enabled"] = true, ["language"] = " de-DE ", ["numbering"] = "SxE", ["addMovieTmdbId"] = true };

        await Save(host, settings);

        var rename = (await Settings(host))["tmdbRename"]!.AsObject();
        Assert.True(rename["enabled"]!.GetValue<bool>());
        Assert.Equal("de-DE", rename["language"]!.GetValue<string>());
        Assert.Equal("SxE", rename["numbering"]!.GetValue<string>());
        Assert.True(rename["addMovieTmdbId"]!.GetValue<bool>());

        var saved = Saved(host).TmdbRename;
        Assert.True(saved.Enabled);
        Assert.Equal(TvNumbering.SxE, saved.Numbering);
    }

    [Fact]
    public async Task ABlankLanguageOrAnUnknownNumberingFallsBackToTheDefaults()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var settings = await Settings(host);
        settings["tmdbRename"] = new JsonObject { ["enabled"] = true, ["language"] = "   ", ["numbering"] = "nonsense", ["addMovieTmdbId"] = false };

        await Save(host, settings);

        var saved = Saved(host).TmdbRename;
        Assert.Equal("en-US", saved.Language);
        Assert.Equal(TvNumbering.S00E00, saved.Numbering);
    }

    [Fact]
    public async Task AnOlderPageThatDoesNotSendTheRenamerLeavesItsSettingsAsTheyWere()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var settings = await Settings(host);
        settings["tmdbRename"] = new JsonObject { ["enabled"] = true, ["language"] = "fr-FR", ["numbering"] = "SxE", ["addMovieTmdbId"] = true };
        await Save(host, settings);

        var again = await Settings(host);
        again.Remove("tmdbRename");
        await Save(host, again);

        var saved = Saved(host).TmdbRename;
        Assert.True(saved.Enabled);
        Assert.Equal("fr-FR", saved.Language);
    }

    [Fact]
    public async Task TheFileBotSettingsAreIndependentOfTheRenamer()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var settings = await Settings(host);
        settings["fileBotEnabled"] = true;
        settings["fileBotCliPath"] = "C:/FileBot/filebot.exe";
        settings["tmdbRename"] = new JsonObject { ["enabled"] = false, ["language"] = "en-US", ["numbering"] = "S00E00", ["addMovieTmdbId"] = false };

        await Save(host, settings);

        var saved = Saved(host);
        Assert.True(saved.FileBot.Enabled);
        Assert.False(saved.TmdbRename.Enabled);
    }

    [Fact]
    public async Task HoldingUnmatchedFiles_IsOffByDefault_RoundTrips_AndAnOlderPageLeavesItAlone()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var settings = await Settings(host);
        Assert.False(settings["holdUnmatchedFiles"]!.GetValue<bool>());

        settings["holdUnmatchedFiles"] = true;
        await Save(host, settings);
        Assert.True((await Settings(host))["holdUnmatchedFiles"]!.GetValue<bool>());
        Assert.True(Saved(host).HoldUnmatchedFiles);

        var older = await Settings(host);
        older.Remove("holdUnmatchedFiles");                // a page that does not know the setting
        await Save(host, older);
        Assert.True(Saved(host).HoldUnmatchedFiles);

        var off = await Settings(host);
        off["holdUnmatchedFiles"] = false;
        await Save(host, off);
        Assert.False(Saved(host).HoldUnmatchedFiles);
    }

    [Fact]
    public async Task ThePreviewWithNoKeyAsksForOne_AndAsksTmdbNothing()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        using var response = await host.Client.PostAsync("/api/rename/preview", new StringContent("{}", Encoding.UTF8, "application/json"));
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.False(body["success"]!.GetValue<bool>());
        Assert.Contains("TMDB API key", body["message"]!.GetValue<string>());
        Assert.DoesNotContain(host.Http.Requests, u => u.Host.Contains("themoviedb"));
    }
}
