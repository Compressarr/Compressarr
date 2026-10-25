using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Compressarr.Core.Config;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Web.Tests;

/// <summary>Stage 4 of the sign-in work: a saved key, token or webhook address is never sent to the page (a saved one comes back as the mask, shown as dots), the mask sent back on a save
/// means "unchanged", typing replaces and an empty box removes; the Test buttons use the saved secret for whatever address is typed; exports leave secrets out unless asked, and importing a
/// redacted file keeps what the install has.</summary>
public class SecretsEndpointTests
{
    private const string Mask = "********";
    private static readonly LaneSpec Lane1 = new("hdsd", "SD-HD");

    private static async Task<JsonObject> Settings(QueueHost host) =>
        JsonNode.Parse(await host.Client.GetStringAsync("/api/settings"))!.AsObject();

    private static async Task<HttpStatusCode> Save(QueueHost host, JsonObject settings)
    {
        using var response = await host.Client.PutAsync("/api/settings", new StringContent(settings.ToJsonString(), Encoding.UTF8, "application/json"));
        return response.StatusCode;
    }

    private static CompressarrConfig Saved(QueueHost host) =>
        host.Services.GetRequiredService<IConfigStore>().Load(AppPaths.GetConfigFilePath());

    private static async Task SetKeys(QueueHost host, string sonarrKey = "sonkey", string radarrKey = "radkey", string sonarrUrl = "http://sonarr.test:8989")
    {
        var settings = await Settings(host);
        settings["sonarr"] = new JsonObject { ["enabled"] = true, ["url"] = sonarrUrl, ["apiKey"] = sonarrKey };
        settings["radarr"] = new JsonObject { ["enabled"] = true, ["url"] = "http://radarr.test:7878", ["apiKey"] = radarrKey };
        Assert.Equal(HttpStatusCode.OK, await Save(host, settings));
    }

    private static async Task<JsonObject> Post(QueueHost host, string path, JsonObject body)
    {
        using var response = await host.Client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    // ---- the settings endpoints -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSettings_NeverCarryASavedKey_OnlyTheMask()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var before = await Settings(host);
        Assert.Equal("", before["sonarr"]!["apiKey"]!.GetValue<string>());     // nothing saved: an empty box

        await SetKeys(host);
        var settings = await Settings(host);

        Assert.Equal(Mask, settings["sonarr"]!["apiKey"]!.GetValue<string>());
        Assert.Equal(Mask, settings["radarr"]!["apiKey"]!.GetValue<string>());
        Assert.Equal("sonkey", Saved(host).Arrs.Sonarr.ApiKey);
        Assert.DoesNotContain("sonkey", settings.ToJsonString());
        Assert.DoesNotContain("radkey", settings.ToJsonString());
    }

    [Fact]
    public async Task ASaveThatSendsTheMaskBack_KeepsTheKeys_ATypedOneReplaces_AndAnEmptyBoxRemoves()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        await SetKeys(host);

        var settings = await Settings(host);                                    // what the page holds: the masks
        Assert.Equal(HttpStatusCode.OK, await Save(host, settings));
        Assert.Equal("sonkey", Saved(host).Arrs.Sonarr.ApiKey);
        Assert.Equal("radkey", Saved(host).Arrs.Radarr.ApiKey);

        settings["sonarr"]!["apiKey"] = "  newsonkey ";
        Assert.Equal(HttpStatusCode.OK, await Save(host, settings));
        Assert.Equal("newsonkey", Saved(host).Arrs.Sonarr.ApiKey);              // typed replaces (trimmed)
        Assert.Equal("radkey", Saved(host).Arrs.Radarr.ApiKey);

        settings = await Settings(host);
        settings["radarr"]!["apiKey"] = "";                                     // the box was emptied
        Assert.Equal(HttpStatusCode.OK, await Save(host, settings));
        Assert.Equal("", Saved(host).Arrs.Radarr.ApiKey);
        Assert.Equal("newsonkey", Saved(host).Arrs.Sonarr.ApiKey);
        Assert.Equal("", (await Settings(host))["radarr"]!["apiKey"]!.GetValue<string>());
    }

    [Fact]
    public async Task ASaveWithAnAddressChangedButTheMaskKept_KeepsTheKey()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        await SetKeys(host);
        var settings = await Settings(host);
        settings["sonarr"]!["url"] = "http://192.168.1.99:8989";                // the machine got a new address

        Assert.Equal(HttpStatusCode.OK, await Save(host, settings));

        Assert.Equal("sonkey", Saved(host).Arrs.Sonarr.ApiKey);
        Assert.Equal("http://192.168.1.99:8989", Saved(host).Arrs.Sonarr.Url);
    }

    [Fact]
    public async Task AnOlderPage_ThatSendsNoPosterSettings_LeavesTheKeysAlone()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var settings = await Settings(host);
        settings["posters"] = new JsonObject { ["enabled"] = true, ["tmdbApiKey"] = "tmdb1", ["omdbApiKey"] = "omdb1", ["size"] = "Medium", ["smallerImages"] = false, ["source"] = "Tmdb" };
        await Save(host, settings);
        settings = await Settings(host);
        settings.Remove("posters");

        Assert.Equal(HttpStatusCode.OK, await Save(host, settings));

        Assert.Equal("tmdb1", Saved(host).Posters.TmdbApiKey);
        Assert.Equal("omdb1", Saved(host).Posters.OmdbApiKey);
    }

    [Fact]
    public async Task ThePosterKeys_AreSecretsToo()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var settings = await Settings(host);
        settings["posters"] = new JsonObject { ["enabled"] = true, ["tmdbApiKey"] = "tmdb1", ["omdbApiKey"] = "omdb1", ["size"] = "Medium", ["smallerImages"] = false, ["source"] = "Tmdb" };
        await Save(host, settings);

        settings = await Settings(host);
        Assert.Equal(Mask, settings["posters"]!["tmdbApiKey"]!.GetValue<string>());
        Assert.Equal(Mask, settings["posters"]!["omdbApiKey"]!.GetValue<string>());

        settings["posters"]!["omdbApiKey"] = "";                                // emptied: removed
        await Save(host, settings);
        Assert.Equal("", Saved(host).Posters.OmdbApiKey);
        Assert.Equal("tmdb1", Saved(host).Posters.TmdbApiKey);
    }

    // ---- the Test buttons -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATestThatStillShowsTheMask_UsesTheSavedKey_ForAnyAddress()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        await SetKeys(host);

        await Post(host, "/api/arr/test", new JsonObject { ["enabled"] = true, ["url"] = "http://sonarr.test:8989/", ["apiKey"] = Mask, ["service"] = "sonarr" });
        Assert.Contains(host.Http.Requests, u => u.Host == "sonarr.test");

        var moved = await Post(host, "/api/arr/test", new JsonObject { ["enabled"] = true, ["url"] = "http://192.168.1.99:8989", ["apiKey"] = Mask, ["service"] = "sonarr" });
        Assert.Contains(host.Http.Requests, u => u.Host == "192.168.1.99");       // a new address is tested with the saved key, nothing retyped
        Assert.DoesNotContain("required", moved["message"]!.GetValue<string>());

        await Post(host, "/api/arr/test", new JsonObject { ["enabled"] = true, ["url"] = "http://other.test:8989", ["apiKey"] = "typed", ["service"] = "sonarr" });
        Assert.Contains(host.Http.Requests, u => u.Host == "other.test");
    }

    [Fact]
    public async Task ATestWithNoKeyAnywhere_SaysTheKeyIsNeeded()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        var body = await Post(host, "/api/arr/test", new JsonObject { ["enabled"] = true, ["url"] = "http://sonarr.test:8989", ["apiKey"] = "", ["service"] = "sonarr" });

        Assert.False(body["success"]!.GetValue<bool>());
        Assert.Contains("required", body["message"]!.GetValue<string>());
        Assert.Empty(host.Http.Requests);
    }

    [Fact]
    public async Task ATestOfAnEmptiedBox_HasNoKey_NotTheSavedOne()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        await SetKeys(host);

        var body = await Post(host, "/api/arr/test", new JsonObject { ["enabled"] = true, ["url"] = "http://sonarr.test:8989", ["apiKey"] = "", ["service"] = "sonarr" });   // the box was emptied

        Assert.False(body["success"]!.GetValue<bool>());
        Assert.Contains("required", body["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task APosterTestThatShowsTheMask_UsesTheSavedKeyForThatSource()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var settings = await Settings(host);
        settings["posters"] = new JsonObject { ["enabled"] = true, ["tmdbApiKey"] = "tmdb1", ["omdbApiKey"] = "omdbsaved", ["size"] = "Medium", ["smallerImages"] = false, ["source"] = "Omdb" };
        await Save(host, settings);

        await Post(host, "/api/posters/test", new JsonObject { ["source"] = "Omdb", ["apiKey"] = Mask });

        var request = host.Http.Requests.First(u => u.Host == "www.omdbapi.com");
        Assert.Contains("apikey=omdbsaved", request.Query);
    }

    // ---- channels -------------------------------------------------------------------------------------------------------------------

    private static async Task<JsonObject> AddChannel(QueueHost host, string type, JsonObject settings)
    {
        var created = await Post(host, "/api/notifications/channels", new JsonObject { ["type"] = type });
        created["settings"] = settings;
        return await PutChannel(host, created);
    }

    private static async Task<JsonObject> PutChannel(QueueHost host, JsonObject channel)
    {
        using var response = await host.Client.PutAsJsonAsync($"/api/notifications/channels/{channel["id"]!.GetValue<string>()}", channel);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private static async Task<JsonArray> Channels(QueueHost host) =>
        JsonNode.Parse(await host.Client.GetStringAsync("/api/notifications/channels"))!.AsArray();

    [Fact]
    public async Task AChannelsSecretFields_AreNeverSent_AndTheMaskKeepsThem()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var saved = await AddChannel(host, "ntfy", new JsonObject { ["server"] = "https://ntfy.example", ["topic"] = "t1", ["accessToken"] = "tk_secret" });

        Assert.Equal(Mask, saved["settings"]!["accessToken"]!.GetValue<string>());
        Assert.Equal("https://ntfy.example", saved["settings"]!["server"]!.GetValue<string>());   // the plain fields are shown
        Assert.DoesNotContain("tk_secret", (await Channels(host)).ToJsonString());

        await PutChannel(host, saved);                                                           // the mask sent back
        Assert.Equal("tk_secret", Saved(host).Notifications.Channels[0].Settings["accessToken"]);

        saved["settings"]!["accessToken"] = " tk_new ";
        saved = await PutChannel(host, saved);
        Assert.Equal("tk_new", Saved(host).Notifications.Channels[0].Settings["accessToken"]);   // typed replaces (trimmed)

        saved["settings"]!["accessToken"] = "";
        saved = await PutChannel(host, saved);
        Assert.False(Saved(host).Notifications.Channels[0].Settings.ContainsKey("accessToken")); // an emptied box removes it
        Assert.Equal("", saved["settings"]!["accessToken"]?.GetValue<string>() ?? "");
    }

    [Fact]
    public async Task ADiscordOrSlackWebhookAddress_IsASecret()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        var saved = await AddChannel(host, "discord", new JsonObject { ["url"] = "https://discord.com/api/webhooks/1/abc" });

        Assert.Equal(Mask, saved["settings"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task AChannelTest_UsesTheSavedSecretForAMaskedBox_WhateverElseWasChanged()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var saved = await AddChannel(host, "ntfy", new JsonObject { ["server"] = "https://ntfy.example", ["topic"] = "t1", ["accessToken"] = "tk_secret" });
        var id = saved["id"]!.GetValue<string>();

        await Post(host, "/api/notifications/test", new JsonObject
        {
            ["type"] = "ntfy", ["channelId"] = id, ["settings"] = new JsonObject { ["server"] = "https://ntfy.example", ["topic"] = "t1", ["accessToken"] = Mask }
        });
        Assert.Contains(host.Http.Requests, u => u.Host == "ntfy.example");

        await Post(host, "/api/notifications/test", new JsonObject
        {
            ["type"] = "ntfy", ["channelId"] = id, ["settings"] = new JsonObject { ["server"] = "https://ntfy.moved.example", ["topic"] = "t1", ["accessToken"] = Mask }
        });
        Assert.Contains(host.Http.Requests, u => u.Host == "ntfy.moved.example");   // a new address is tested without retyping the token

        var noChannel = await Post(host, "/api/notifications/test", new JsonObject
        {
            ["type"] = "ntfy", ["settings"] = new JsonObject { ["server"] = "https://ntfy.example", ["topic"] = "t1", ["accessToken"] = Mask }
        });
        Assert.NotNull(noChannel["message"]);                                          // no channel named: nothing to borrow, and the mask is never sent on as a token
    }

    // ---- export and import ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheExport_LeavesSecretsOut_UnlessAsked()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        await SetKeys(host);
        await AddChannel(host, "ntfy", new JsonObject { ["server"] = "https://ntfy.example", ["topic"] = "t1", ["accessToken"] = "tk_secret" });

        var plain = await host.Client.GetStringAsync("/api/settings/export");
        Assert.DoesNotContain("sonkey", plain);
        Assert.DoesNotContain("radkey", plain);
        Assert.DoesNotContain("tk_secret", plain);
        Assert.Contains("ntfy.example", plain);                                    // the rest of the settings are there

        var full = await host.Client.GetStringAsync("/api/settings/export?includeSecrets=true");
        Assert.Contains("sonkey", full);
        Assert.Contains("radkey", full);
        Assert.Contains("tk_secret", full);
    }

    [Fact]
    public async Task ImportingARedactedExport_KeepsTheKeysThisInstallHas()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        await SetKeys(host);
        await AddChannel(host, "ntfy", new JsonObject { ["server"] = "https://ntfy.example", ["topic"] = "t1", ["accessToken"] = "tk_secret" });
        var redacted = await host.Client.GetStringAsync("/api/settings/export");

        using var response = await host.Client.PostAsync("/api/settings/import", new StringContent(redacted, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        var after = Saved(host);
        Assert.Equal("sonkey", after.Arrs.Sonarr.ApiKey);
        Assert.Equal("radkey", after.Arrs.Radarr.ApiKey);
        Assert.Equal("tk_secret", after.Notifications.Channels[0].Settings["accessToken"]);
    }

    [Fact]
    public async Task ImportingAFullExport_BringsItsKeys()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        await SetKeys(host);
        var full = await host.Client.GetStringAsync("/api/settings/export?includeSecrets=true");
        await SetKeys(host, sonarrKey: "other", radarrKey: "other2");

        using var response = await host.Client.PostAsync("/api/settings/import", new StringContent(full, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        Assert.Equal("sonkey", Saved(host).Arrs.Sonarr.ApiKey);
        Assert.Equal("radkey", Saved(host).Arrs.Radarr.ApiKey);
    }

    // ---- the page scripts -----------------------------------------------------------------------------------------------------------

    private static string Script(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", name);
        Assert.True(File.Exists(path), $"{name} is not in the test output");
        return File.ReadAllText(path);
    }

    [Fact]
    public void TheSettingsPage_ShowsSavedKeysAsDots_HasNoClearButtons_AndOffersTheExportTick()
    {
        var script = Script("settings.js");

        Assert.Contains("wireSecretBox", script);
        Assert.Contains("showSecrets", script);
        Assert.DoesNotContain("clearSecrets", script);
        Assert.DoesNotContain("saved - type to replace", script);
        Assert.Contains("includeSecrets=true", script);
        Assert.Contains("id=\"exportSecrets\"", Script("index.html"));
        var nav = Script("nav.js");
        Assert.Contains("const SECRET_MASK = '********'", nav);
        Assert.True(nav.IndexOf("function wireSecretBox", StringComparison.Ordinal) < nav.IndexOf("(function ()", StringComparison.Ordinal), "wireSecretBox must be outside nav.js's wrapper so the page scripts can call it");
    }

    [Fact]
    public void TheChannelCards_ShowSavedSecretsAsDots_AndNameTheChannelInTests()
    {
        var script = Script("notifications.js");

        Assert.Contains("wireSecretBox", script);
        Assert.DoesNotContain("clearSecrets", script);
        Assert.DoesNotContain("saved - type to replace", script);
        Assert.Contains("channelId", script);
    }
}
