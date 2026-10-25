using System.Text.Json.Nodes;
using Compressarr.Core.Config;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Web.Tests;

/// <summary>Through the real endpoints: marking History as viewed and running a backup record their values in the state database, and neither rewrites the
/// user's settings file any more.</summary>
public class AppStateEndpointTests
{
    private static readonly LaneSpec Lane1 = new("hdsd", "SD-HD");

    [Fact]
    public async Task MarkViewed_IsRemembered_WithoutTouchingTheSettingsFile()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var settingsBefore = File.ReadAllText(AppPaths.GetConfigFilePath());
        var stamp = File.GetLastWriteTimeUtc(AppPaths.GetConfigFilePath());
        await Task.Delay(30);

        (await host.Client.PostAsync("/api/history/mark-viewed", null)).EnsureSuccessStatusCode();

        var viewed = JsonNode.Parse(await host.Client.GetStringAsync("/api/history/viewed-through"))!;
        Assert.Equal(host.Services.GetRequiredService<IAppState>().HistoryViewedThroughRunNumber, viewed["runNumber"]!.GetValue<int>());
        Assert.Equal(settingsBefore, File.ReadAllText(AppPaths.GetConfigFilePath()));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(AppPaths.GetConfigFilePath()));
    }

    [Fact]
    public async Task ABackup_ShowsAsTheLastBackupInSettings_ButIsNotWrittenToTheSettingsFile()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var before = JsonNode.Parse(await host.Client.GetStringAsync("/api/settings"))!;
        Assert.Null(before["backupLastRunUtc"]);

        using var run = await host.Client.PostAsync("/api/backups/run", null);
        run.EnsureSuccessStatusCode();

        var after = JsonNode.Parse(await host.Client.GetStringAsync("/api/settings"))!;
        Assert.NotNull(after["backupLastRunUtc"]);
        Assert.DoesNotContain("LastRunUtc", File.ReadAllText(AppPaths.GetConfigFilePath()));
    }

    [Fact]
    public async Task ASettingsSave_DoesNotResurrectTheValues_ItNeverCarriedThem()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        using var run = await host.Client.PostAsync("/api/backups/run", null);
        run.EnsureSuccessStatusCode();
        var settings = JsonNode.Parse(await host.Client.GetStringAsync("/api/settings"))!.AsObject();

        using var save = await host.Client.PutAsync("/api/settings", new StringContent(settings.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        save.EnsureSuccessStatusCode();

        var text = File.ReadAllText(AppPaths.GetConfigFilePath());
        Assert.DoesNotContain("LastRunUtc", text);
        Assert.DoesNotContain("HistoryViewedThroughRunNumber", text);
        Assert.NotNull(JsonNode.Parse(await host.Client.GetStringAsync("/api/settings"))!["backupLastRunUtc"]);
    }
}
