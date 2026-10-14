using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Compressarr.Core.Catalog;
using Compressarr.Core.FFmpeg;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Web.Tests;

/// <summary>An ffmpeg profile can name a HandBrake profile as its Dolby Vision / HDR10+ fallback. That is a need like a lane's preset: deleting
/// the HandBrake profile is refused, and renaming it takes the ffmpeg profile's fallback along (or is refused when that profile cannot be edited).</summary>
public class FallbackReferenceTests
{
    private static readonly LaneSpec Lane1 = new("lane1", "Lane One");

    private static string HbUrl(string name) => "/api/profiles/handbrake/" + Uri.EscapeDataString(name);
    private static string FfUrl(string name) => "/api/profiles/ffmpeg/" + Uri.EscapeDataString(name);

    private static async Task CreateHandBrakeProfileAsync(QueueHost host, string name)
    {
        var form = (JsonObject)(await host.Client.GetFromJsonAsync<JsonObject>(HbUrl("Compressarr SD-HD")))!["form"]!.DeepClone();
        form["name"] = name;
        (await host.Client.PostAsJsonAsync("/api/profiles/handbrake", new JsonObject { ["form"] = form })).EnsureSuccessStatusCode();
    }

    private static async Task CreateFfmpegProfileWithFallbackAsync(QueueHost host, string name, string fallback)
    {
        var profile = (JsonObject)(await host.Client.GetFromJsonAsync<JsonObject>(FfUrl("Compressarr SD-HD")))!["profile"]!.DeepClone();
        profile["name"] = name;
        profile["fallbackHandBrakePreset"] = fallback;
        (await host.Client.PostAsJsonAsync("/api/profiles/ffmpeg", profile)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Delete_IsRefused_WhileAnFfmpegProfileNamesTheProfileAsItsFallback()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        await CreateHandBrakeProfileAsync(host, "Fallback HB");
        await CreateFfmpegProfileWithFallbackAsync(host, "FF needs it", "Fallback HB");

        using var refused = await host.Client.DeleteAsync(HbUrl("Fallback HB"));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("FF needs it", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(HbUrl("Fallback HB"))).StatusCode);   // still there

        (await host.Client.DeleteAsync(FfUrl("FF needs it"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await host.Client.DeleteAsync(HbUrl("Fallback HB"))).StatusCode);  // free once nothing needs it
    }

    [Fact]
    public async Task Rename_TakesYourOwnFfmpegProfilesFallbackAlong()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        await CreateHandBrakeProfileAsync(host, "Fallback HB");
        await CreateFfmpegProfileWithFallbackAsync(host, "FF needs it", "Fallback HB");

        var form = (JsonObject)(await host.Client.GetFromJsonAsync<JsonObject>(HbUrl("Fallback HB")))!["form"]!.DeepClone();
        form["name"] = "Renamed fallback";
        (await host.Client.PutAsJsonAsync(HbUrl("Fallback HB"), new JsonObject { ["form"] = form })).EnsureSuccessStatusCode();

        var ff = (await host.Client.GetFromJsonAsync<JsonObject>(FfUrl("FF needs it")))!["profile"]!.AsObject();
        Assert.Equal("Renamed fallback", ff["fallbackHandBrakePreset"]!.GetValue<string>());
    }

    [Fact]
    public async Task Rename_IsRefusedBeforeAnythingIsWritten_WhenACatalogFfmpegProfileDependsOnTheName()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        await CreateHandBrakeProfileAsync(host, "Fallback HB");
        var ffmpeg = host.Services.GetRequiredService<IFFmpegProfileStore>();
        var catalogProfile = ffmpeg.Find("Compressarr SD-HD")!;
        catalogProfile.Name = "Catalog FF";
        catalogProfile.FallbackHandBrakePreset = "Fallback HB";
        ffmpeg.AddCatalogProfile(catalogProfile, new InstalledCatalogProfile("Catalog FF", "ffmpeg", "Catalog FF", 1, "00", "", "Compressarr Presets", "https://example.invalid", "v-test", "tester", DateTime.UtcNow));

        var form = (JsonObject)(await host.Client.GetFromJsonAsync<JsonObject>(HbUrl("Fallback HB")))!["form"]!.DeepClone();
        form["name"] = "Renamed fallback";
        using var refused = await host.Client.PutAsJsonAsync(HbUrl("Fallback HB"), new JsonObject { ["form"] = form });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Catalog FF", await refused.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync(HbUrl("Fallback HB"))).StatusCode);                 // not renamed
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(HbUrl("Renamed fallback"))).StatusCode);
    }
}
