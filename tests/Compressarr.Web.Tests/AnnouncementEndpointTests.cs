using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Compressarr.Core.Announcements;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Web.Tests;

public class AnnouncementEndpointTests
{
    private static readonly LaneSpec Lane1 = new("hdsd", "SD-HD");

    private static string DaysAgo(int days) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-days).ToString("yyyy-MM-dd");

    /// <summary>Puts a fetched file in this host's announcement store, as the background service would after a successful check.</summary>
    private static void Seed(QueueHost host, params (string Id, int DaysAgo)[] items)
    {
        var entries = string.Join(",", items.Select(i => $$"""{ "id": "{{i.Id}}", "date": "{{DaysAgo(i.DaysAgo)}}", "title": "Title {{i.Id}}", "kind": "beta", "link": "https://github.com/Compressarr/Compressarr/releases" }"""));
        host.Services.GetRequiredService<IAnnouncementStateStore>().Update(s =>
        {
            s.CachedJson = $$"""{ "schema": 1, "announcements": [ {{entries}} ] }""";
            s.Baseline = DaysAgo(20);   // looked 20 days ago, so these are news since then
            s.LastCheckedUtc = DateTimeOffset.UtcNow;
            return 0;
        });
    }

    private static async Task<JsonNode> Get(QueueHost host) =>
        JsonNode.Parse(await host.Client.GetStringAsync("/api/announcements"))!;

    private static async Task SaveSettings(QueueHost host, bool announcementsEnabled)
    {
        var settings = JsonNode.Parse(await host.Client.GetStringAsync("/api/settings"))!.AsObject();
        settings["announcementsEnabled"] = announcementsEnabled;
        using var response = await host.Client.PutAsync("/api/settings", new StringContent(settings.ToJsonString(), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    private static int AnnouncementRequests(QueueHost host) =>
        host.Http.Requests.Count(u => u.Host == "raw.githubusercontent.com" && u.AbsolutePath.Contains("Compressarr-Announcements"));

    [Fact]
    public async Task TurningAnnouncementsOn_LooksForNewsRightAway_SoTheBellCanAppearWithoutWaitingForTheDailyCheck()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        await SaveSettings(host, announcementsEnabled: false);
        var before = AnnouncementRequests(host);

        await SaveSettings(host, announcementsEnabled: true);

        Assert.Equal(before + 1, AnnouncementRequests(host));
        Assert.True((await Get(host))["enabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task SavingWithAnnouncementsAlreadyOn_DoesNotAskAgain_AndTurningThemOffHidesTheBellAtOnce()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        await SaveSettings(host, announcementsEnabled: true);
        var afterFirst = AnnouncementRequests(host);

        await SaveSettings(host, announcementsEnabled: true);
        Assert.Equal(afterFirst, AnnouncementRequests(host));        // nothing changed, nothing asked

        await SaveSettings(host, announcementsEnabled: false);
        Assert.False((await Get(host))["enabled"]!.GetValue<bool>());   // what the bell reads right after Save
        Assert.Equal(afterFirst, AnnouncementRequests(host));        // and nothing is requested while off
    }

    private static Task<HttpResponseMessage> PostRead(QueueHost host, string json) =>
        host.Client.PostAsync("/api/announcements/read", new StringContent(json, Encoding.UTF8, "application/json"));

    [Fact]
    public async Task TheList_ShowsRecentMessagesNewestFirst_WithTheUnreadCount_AndNothingForAnOldOne()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Seed(host, ("older", 5), ("newer", 1), ("ancient", 45));

        var view = await Get(host);

        Assert.True(view["enabled"]!.GetValue<bool>());
        Assert.Equal(new[] { "newer", "older" }, view["items"]!.AsArray().Select(i => i!["id"]!.GetValue<string>()).ToArray());
        Assert.Equal(2, view["unread"]!.GetValue<int>());
        Assert.Equal("https://github.com/Compressarr/Compressarr/releases", view["items"]![0]!["link"]!.GetValue<string>());
    }

    [Fact]
    public async Task MessagesFromBeforeTheFirstLook_AreListedButNeverUnread()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Seed(host, ("before", 25), ("after", 3));    // the baseline is 20 days ago

        var view = await Get(host);

        Assert.Equal(1, view["unread"]!.GetValue<int>());
        Assert.True(view["items"]!.AsArray().Single(i => i!["id"]!.GetValue<string>() == "before")!["read"]!.GetValue<bool>());
    }

    [Fact]
    public async Task OpeningTheList_MarksEverythingRead_AndItStaysRead()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Seed(host, ("a", 2), ("b", 1));

        using var response = await PostRead(host, """{ "all": true }""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await Get(host))["unread"]!.GetValue<int>());
    }

    [Fact]
    public async Task MarkingSomeIds_LeavesTheOthersUnread()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Seed(host, ("a", 2), ("b", 1));

        using var response = await PostRead(host, """{ "ids": ["a"] }""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var view = await Get(host);
        Assert.Equal(1, view["unread"]!.GetValue<int>());
        Assert.False(view["items"]!.AsArray().Single(i => i!["id"]!.GetValue<string>() == "b")!["read"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ARequestThatSaysNothing_IsRefused()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        using var response = await PostRead(host, "{}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task WhenOff_TheBellHasNothingAndSaysSo()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Seed(host, ("a", 1));
        var settings = JsonNode.Parse(await host.Client.GetStringAsync("/api/settings"))!.AsObject();
        settings["announcementsEnabled"] = false;
        using var saved = await host.Client.PutAsync("/api/settings", new StringContent(settings.ToJsonString(), Encoding.UTF8, "application/json"));
        saved.EnsureSuccessStatusCode();

        var view = await Get(host);

        Assert.False(view["enabled"]!.GetValue<bool>());
        Assert.Empty(view["items"]!.AsArray());
        Assert.Equal(0, view["unread"]!.GetValue<int>());
    }

    [Fact]
    public async Task TheSetting_IsOnByDefault_AndSurvivesASave()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var settings = JsonNode.Parse(await host.Client.GetStringAsync("/api/settings"))!.AsObject();
        Assert.True(settings["announcementsEnabled"]!.GetValue<bool>());

        settings["announcementsEnabled"] = false;
        (await host.Client.PutAsync("/api/settings", new StringContent(settings.ToJsonString(), Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();
        Assert.False(JsonNode.Parse(await host.Client.GetStringAsync("/api/settings"))!["announcementsEnabled"]!.GetValue<bool>());

        settings["announcementsEnabled"] = true;
        (await host.Client.PutAsync("/api/settings", new StringContent(settings.ToJsonString(), Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();
        Assert.True(JsonNode.Parse(await host.Client.GetStringAsync("/api/settings"))!["announcementsEnabled"]!.GetValue<bool>());
    }
}
