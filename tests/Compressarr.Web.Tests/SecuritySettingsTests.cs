using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Compressarr.Core.Auth;
using Compressarr.Web.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Web.Tests;

/// <summary>The Settings page's Host and Security cards: Allowed Hosts through the ordinary settings call (and applying at once), and the guards in the scripts that the
/// sign-in relies on (the service worker must never cache a redirect; every page must go to the login page on a 401).</summary>
public class SecuritySettingsTests
{
    private static readonly LaneSpec Lane1 = new("hdsd", "SD-HD");

    private static async Task<JsonObject> Settings(QueueHost host) =>
        JsonNode.Parse(await host.Client.GetStringAsync("/api/settings"))!.AsObject();

    private static async Task<HttpStatusCode> Save(QueueHost host, JsonObject settings)
    {
        using var response = await host.Client.PutAsync("/api/settings", new StringContent(settings.ToJsonString(), Encoding.UTF8, "application/json"));
        return response.StatusCode;
    }

    private static async Task<HttpStatusCode> StatusWithHost(QueueHost host, string hostHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/run/status");
        request.Headers.TryAddWithoutValidation("Host", hostHeader);
        using var response = await host.Client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task AllowedHosts_StartEmpty_AreSavedWithTheSettings_AndApplyAtOnce()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var settings = await Settings(host);
        Assert.Equal("", settings["allowedHosts"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, await StatusWithHost(host, "nas.local"));                                    // an empty list still answers to a home network's names
        Assert.Equal(HttpStatusCode.BadRequest, await StatusWithHost(host, "anything.example"));                    // ...but not to a domain name (DNS rebinding)

        settings["allowedHosts"] = "  compressarr.lan, 192.168.10.91  ";
        Assert.Equal(HttpStatusCode.OK, await Save(host, settings));

        Assert.Equal("compressarr.lan, 192.168.10.91", (await Settings(host))["allowedHosts"]!.GetValue<string>());   // trimmed
        Assert.Equal(HttpStatusCode.BadRequest, await StatusWithHost(host, "evil.example"));                          // no waiting for a cache
        Assert.Equal(HttpStatusCode.OK, await StatusWithHost(host, "compressarr.lan:1212"));
        Assert.Equal(HttpStatusCode.OK, await StatusWithHost(host, "localhost:1212"));                                // the owner can always get back in

        settings["allowedHosts"] = "";
        Assert.Equal(HttpStatusCode.OK, await Save(host, settings));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusWithHost(host, "evil.example"));                          // back to the default rule
        Assert.Equal(HttpStatusCode.OK, await StatusWithHost(host, "192.168.10.91:1212"));
    }

    [Fact]
    public async Task AnOlderPage_ThatSendsNoAllowedHosts_LeavesThemAsSaved()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var settings = await Settings(host);
        settings["allowedHosts"] = "compressarr.lan";
        await Save(host, settings);

        var older = JsonNode.Parse((await Settings(host)).ToJsonString())!.AsObject();
        older.Remove("allowedHosts");
        await Save(host, older);

        Assert.Equal("compressarr.lan", (await Settings(host))["allowedHosts"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheSettingsExport_CarriesNoSignInCredentials()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        host.Services.GetRequiredService<IWebAuth>().SaveSettings(AuthMethod.Forms, AuthRequired.DisabledForLocalAddresses, false, "mark", "a long password phrase");

        var export = await host.Client.GetStringAsync("/api/settings/export");

        Assert.DoesNotContain("a long password phrase", export);
        Assert.DoesNotContain("pbkdf2", export);
        Assert.DoesNotContain("\"mark\"", export);
    }

    private static string Script(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "wwwroot", name);
        Assert.True(File.Exists(path), $"{name} is not in the test output");
        return File.ReadAllText(path);
    }

    [Fact]
    public void TheServiceWorker_NeverCachesARedirectOrAFailedAnswer_AndDoesNotFailInstallOverOneFile()
    {
        var worker = Script("sw.js");

        Assert.Contains("response.redirected", worker);          // a redirect to the sign-in page is never stored under a page's name
        Assert.Contains("response.ok", worker);
        Assert.DoesNotContain("addAll(", worker);                // addAll rejects the whole install when one request fails
        Assert.Contains("isCacheable(response)", worker);
    }

    [Fact]
    public void EveryPage_GoesToTheLoginPage_OnA401LoginRequired_AndKeepsWhereItWas()
    {
        var nav = Script("nav.js");

        Assert.Contains("window.fetch = async function", nav);
        Assert.Contains("login_required", nav);
        Assert.Contains("/login.html?returnUrl=", nav);
        Assert.Contains("id=\"signOutBtn\"", nav);
        Assert.Contains("/api/auth/logout", nav);
    }

    [Fact]
    public void TheLoginPage_UsesOnlyItsOwnScript_AndNeverTheNavScriptThatWouldRedirectItToItself()
    {
        var page = Script("login.html");

        Assert.Contains("/login.js", page);
        Assert.DoesNotContain("/nav.js", page);
        Assert.DoesNotContain("/sw.js", page);
    }

    [Fact]
    public void TheSecurityCard_HasItsFields_AndTheKeysLiveInIt()
    {
        var page = Script("index.html");

        foreach (var id in new[] { "securityCard", "authMethod", "authRequired", "authUsername", "authPassword", "authPasswordConfirm", "authCurrentPassword", "authTrustCgnat",
                                   "signOutEverywhereBtn", "apiKeyControl", "apiKeyRead", "allowedHosts" })
            Assert.Contains($"id=\"{id}\"", page);
        Assert.Contains("Disabled for Local Addresses", page);
        Assert.Contains("Forms (Login Page)", page);
        Assert.Contains("Basic (Browser Popup)", page);
    }
}
