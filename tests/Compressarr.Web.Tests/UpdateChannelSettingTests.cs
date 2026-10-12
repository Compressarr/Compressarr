using System.Text;
using System.Text.Json.Nodes;
using Compressarr.Core.Config;

namespace Compressarr.Web.Tests;

public class UpdateChannelSettingTests
{
    private static readonly LaneSpec Lane1 = new("hdsd", "SD-HD");

    private static async Task<JsonObject> Settings(QueueHost host) =>
        JsonNode.Parse(await host.Client.GetStringAsync("/api/settings"))!.AsObject();

    private static async Task Save(QueueHost host, JsonObject settings)
    {
        using var response = await host.Client.PutAsync("/api/settings", new StringContent(settings.ToJsonString(), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task TheUpdateChannel_StartsAtTheDefaultForThisRelease_AndTheChoiceIsKept()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var settings = await Settings(host);
        Assert.Equal(UpdateCheckSettings.DefaultChannel.ToString(), settings["updateChannel"]!.GetValue<string>());

        settings["updateChannel"] = "Stable";
        await Save(host, settings);
        Assert.Equal("Stable", (await Settings(host))["updateChannel"]!.GetValue<string>());

        settings["updateChannel"] = "Beta";
        await Save(host, settings);
        Assert.Equal("Beta", (await Settings(host))["updateChannel"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("")]
    public async Task AnUnknownChannel_FallsBackToTheDefault(string value)
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var settings = await Settings(host);
        settings["updateChannel"] = "Stable";
        await Save(host, settings);

        settings["updateChannel"] = value;
        await Save(host, settings);

        Assert.Equal(UpdateCheckSettings.DefaultChannel.ToString(), (await Settings(host))["updateChannel"]!.GetValue<string>());
    }
}
