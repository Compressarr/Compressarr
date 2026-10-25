using System.Text;
using System.Text.Json.Nodes;
using Compressarr.Core.Config;

namespace Compressarr.Web.Tests;

/// <summary>The Reports card's two own "days" settings: how long saved report copies are kept, and how far back unseen errors are flagged. A blank box
/// (null) means "same as the log retention", which is what the single shared setting meant before.</summary>
public class ReportRetentionSettingsTests
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
    public async Task BothStartBlank_AndANumberIsKept_AndBlankGoesBackToBlank()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var settings = await Settings(host);
        Assert.Null(settings["reportCopyRetentionDays"]);
        Assert.Null(settings["unseenErrorDays"]);

        settings["reportCopyRetentionDays"] = 7;
        settings["unseenErrorDays"] = 0;
        await Save(host, settings);
        var saved = await Settings(host);
        Assert.Equal(7, saved["reportCopyRetentionDays"]!.GetValue<int>());
        Assert.Equal(0, saved["unseenErrorDays"]!.GetValue<int>());

        saved["reportCopyRetentionDays"] = null;
        saved["unseenErrorDays"] = null;
        await Save(host, saved);
        var blank = await Settings(host);
        Assert.Null(blank["reportCopyRetentionDays"]);
        Assert.Null(blank["unseenErrorDays"]);
    }

    [Fact]
    public async Task ANegativeNumber_IsNotADayCount_SoItReadsAsBlank()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var settings = await Settings(host);
        settings["reportCopyRetentionDays"] = -5;
        settings["unseenErrorDays"] = -1;

        await Save(host, settings);

        var after = await Settings(host);
        Assert.Null(after["reportCopyRetentionDays"]);
        Assert.Null(after["unseenErrorDays"]);
    }

    [Fact]
    public async Task TheLogRetention_StillSavesOnItsOwn()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var settings = await Settings(host);
        settings["retentionDays"] = 45;
        settings["reportCopyRetentionDays"] = 10;

        await Save(host, settings);

        var after = await Settings(host);
        Assert.Equal(45, after["retentionDays"]!.GetValue<int>());
        Assert.Equal(10, after["reportCopyRetentionDays"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(null, 90, 90)]
    [InlineData(7, 90, 7)]
    [InlineData(0, 90, 0)]
    public void ABlankSetting_FollowsTheLogRetention(int? own, int logDays, int expected)
    {
        var report = new ReportSettings { CopyRetentionDays = own, UnseenErrorDays = own };

        Assert.Equal(expected, report.EffectiveCopyRetentionDays(logDays));
        Assert.Equal(expected, report.EffectiveUnseenErrorDays(logDays));
    }
}
