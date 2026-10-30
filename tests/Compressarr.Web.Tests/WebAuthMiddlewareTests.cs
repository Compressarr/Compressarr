using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Compressarr.Core.Auth;
using Compressarr.Core.Config;
using Compressarr.Web.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Web.Tests;

/// <summary>The sign-in wall, through the real middleware and endpoints, with the request's TCP peer chosen per call (X-Test-Peer): what is free, what needs credentials, how
/// Forms and Basic answer, the cross-site and allowed-hosts checks, and the /api/auth calls.</summary>
public class WebAuthMiddlewareTests
{
    private static readonly LaneSpec Lane1 = new("hdsd", "SD-HD");
    private const string Password = "a long password phrase";
    private const string Outside = "203.0.113.5";   // not a private address
    private const string Lan = "192.168.10.50";

    // ---- helpers -------------------------------------------------------------------------------------------------------------------------

    private static async Task<HttpResponseMessage> Send(QueueHost host, HttpMethod method, string url, string peer = "127.0.0.1", string? json = null, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("X-Test-Peer", peer);
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await host.Client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> Get(QueueHost host, string url, string peer = "127.0.0.1", params (string, string)[] headers) =>
        Send(host, HttpMethod.Get, url, peer, null, headers);

    /// <summary>Sets the sign-in up (an account, a method, a rule) and makes the middleware notice at once.</summary>
    private static IWebAuth Configure(QueueHost host, AuthMethod method = AuthMethod.Forms, AuthRequired required = AuthRequired.DisabledForLocalAddresses, bool account = true, bool cgnat = false)
    {
        var auth = host.Services.GetRequiredService<IWebAuth>();
        auth.SaveSettings(method, required, cgnat, account ? "mark" : null, account ? Password : null);
        host.Services.GetRequiredService<WebAuthGate>().Invalidate();
        return auth;
    }

    private static string? SessionCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies)) return null;
        var set = cookies.FirstOrDefault(c => c.StartsWith("compressarr.session=", StringComparison.Ordinal));
        return set?[("compressarr.session=".Length)..].Split(';')[0];
    }

    private static string SetCookieLine(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("compressarr.session=", StringComparison.Ordinal));

    private static async Task<(HttpStatusCode Status, string? Token, HttpResponseMessage Response)> SignIn(QueueHost host, string peer = Outside, string password = Password, bool remember = false, string? returnUrl = null)
    {
        var body = JsonNode.Parse("{}")!.AsObject();
        body["username"] = "mark"; body["password"] = password; body["remember"] = remember;
        if (returnUrl is not null) body["returnUrl"] = returnUrl;
        var response = await Send(host, HttpMethod.Post, "/api/auth/login", peer, body.ToJsonString());
        return (response.StatusCode, SessionCookie(response), response);
    }

    private static string Basic(string user, string password) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    private static async Task<string> ErrorCode(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!["code"]!.GetValue<string>();

    // ---- the default: the local network free, nothing set up ----------------------------------------------------------------------------------------

    [Fact]
    public async Task TheDefault_LeavesTheLocalNetworkAlone_AndRefusesOutsiders_UntilAnAccountExists()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        using var local = await Get(host, "/api/run/status");
        using var lan = await Get(host, "/api/run/status", Lan);
        using var outsideApi = await Get(host, "/api/run/status", Outside);
        using var outsidePage = await Get(host, "/monitor.html", Outside);

        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        Assert.Equal(HttpStatusCode.OK, lan.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, outsideApi.StatusCode);
        Assert.Equal("login_not_set_up", await ErrorCode(outsideApi));
        Assert.Equal(HttpStatusCode.Unauthorized, outsidePage.StatusCode);
        Assert.Contains("username and password", await outsidePage.Content.ReadAsStringAsync());
        Assert.Equal("text/html", outsidePage.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task EvenWithNothingSetUp_TheLoginPagesOwnCallsAndFilesAreReachable()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        using var status = await Get(host, "/api/auth/status", Outside);
        using var login = await Get(host, "/login.html", Outside);        // the test host serves no files: 404 means "let through"
        using var css = await Get(host, "/styles.css", Outside);
        using var asset = await Get(host, "/assets/logo.png", Outside);

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, login.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, css.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, asset.StatusCode);
    }

    // ---- Forms ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Forms_AnOutsiderWithoutASession_IsSentToTheLoginPage_OrGets401ForTheApi()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Configure(host);

        using var page = await Get(host, "/history.html?period=d7", Outside);
        using var api = await Get(host, "/api/settings", Outside);
        using var post = await Send(host, HttpMethod.Post, "/api/run/release-hold", Outside);

        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Equal("/login.html?returnUrl=%2Fhistory.html%3Fperiod%3Dd7", page.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Equal("login_required", await ErrorCode(api));
        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);   // a POST is never redirected
    }

    [Fact]
    public async Task Forms_TheLocalNetwork_StillNeedsNothing()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Configure(host);

        foreach (var peer in new[] { "127.0.0.1", "::1", "192.168.10.9", "10.1.2.3", "172.20.1.1", "169.254.7.7", "fe80::1", "fd00::5" })
        {
            using var response = await Get(host, "/api/run/status", peer);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Forms_SigningIn_SetsTheCookie_AndTheCookieOpensTheDoor_UntilSignedOut()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Configure(host);

        var (status, token, response) = await SignIn(host);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotNull(token);
        var line = SetCookieLine(response);
        Assert.Contains("httponly", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", line, StringComparison.OrdinalIgnoreCase);   // no Remember me: ends with the browser
        Assert.DoesNotContain("secure", line, StringComparison.OrdinalIgnoreCase);     // plain HTTP in the test
        Assert.Equal("/", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["redirect"]!.GetValue<string>());

        using var inside = await Get(host, "/api/run/status", Outside, ("Cookie", $"compressarr.session={token}"));
        Assert.Equal(HttpStatusCode.OK, inside.StatusCode);

        using var logout = await Send(host, HttpMethod.Post, "/api/auth/logout", Outside, null, ("Cookie", $"compressarr.session={token}"));
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
        Assert.Contains("compressarr.session=;", logout.Headers.GetValues("Set-Cookie").First(), StringComparison.Ordinal);   // the cookie is cleared

        using var after = await Get(host, "/api/run/status", Outside, ("Cookie", $"compressarr.session={token}"));
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Forms_RememberMe_MakesTheCookiePersistent_AWeekAhead()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Configure(host);

        var (status, _, response) = await SignIn(host, remember: true);

        Assert.Equal(HttpStatusCode.OK, status);
        var line = SetCookieLine(response);
        Assert.Contains("expires=", line, StringComparison.OrdinalIgnoreCase);
        var expires = DateTimeOffset.Parse(line.Split(';').Select(p => p.Trim()).First(p => p.StartsWith("expires=", StringComparison.OrdinalIgnoreCase))["expires=".Length..]);
        Assert.InRange((expires - DateTimeOffset.UtcNow).TotalDays, 6.9, 7.1);
    }

    [Fact]
    public async Task Forms_AWrongPassword_Is401_AndFiveLockTheAddress_ForFiveMinutes_ButNotOthers()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Configure(host);

        for (var i = 0; i < 4; i++)
        {
            var (status, _, wrong) = await SignIn(host, password: "wrong password!!");
            Assert.Equal(HttpStatusCode.Unauthorized, status);
            Assert.Equal("wrong_credentials", await ErrorCode(wrong));
        }

        var (fifth, _, fifthResponse) = await SignIn(host, password: "wrong password!!");
        Assert.Equal(HttpStatusCode.TooManyRequests, fifth);                   // the fifth locks the address, and says so
        Assert.Equal("300", fifthResponse.Headers.GetValues("Retry-After").Single());
        var (locked, _, _) = await SignIn(host);                                // even the right password is refused during the lock
        Assert.Equal(HttpStatusCode.TooManyRequests, locked);
        var (other, token, _) = await SignIn(host, peer: "198.51.100.9");
        Assert.Equal(HttpStatusCode.OK, other);
        Assert.NotNull(token);
    }

    [Fact]
    public async Task Forms_TheLoginCallIsNotAvailable_WithNoAccount_OrWithBasic()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        var (noAccount, _, one) = await SignIn(host);
        Assert.Equal(HttpStatusCode.Conflict, noAccount);
        Assert.Equal("login_not_available", await ErrorCode(one));

        Configure(host, AuthMethod.Basic);
        var (basic, _, _) = await SignIn(host);
        Assert.Equal(HttpStatusCode.Conflict, basic);
    }

    [Theory]
    [InlineData("//evil.example/x", "/")]
    [InlineData("https://evil.example/x", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("\\\\evil.example", "/")]
    [InlineData("/ok\\bad", "/")]
    [InlineData("/login.html?returnUrl=/x", "/")]
    [InlineData("/api/settings", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("", "/")]
    [InlineData("/history.html?period=d7", "/history.html?period=d7")]
    [InlineData("/monitor.html", "/monitor.html")]
    public async Task TheReturnAddress_IsOnlyEverAPlainPathOnThisSite(string returnUrl, string expected)
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Configure(host);

        var (status, _, response) = await SignIn(host, returnUrl: returnUrl);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(expected, JsonNode.Parse(await response.Content.ReadAsStringAsync())!["redirect"]!.GetValue<string>());
    }

    // ---- Basic ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Basic_AnOutsiderIsChallenged_AndTheRightHeaderGetsIn()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Configure(host, AuthMethod.Basic);

        using var none = await Get(host, "/api/settings", Outside);
        using var page = await Get(host, "/monitor.html", Outside);
        using var wrong = await Get(host, "/api/settings", Outside, ("Authorization", Basic("mark", "wrong password!!")));
        using var right = await Get(host, "/api/settings", Outside, ("Authorization", Basic("mark", Password)));
        using var rightPage = await Get(host, "/monitor.html", Outside, ("Authorization", Basic("MARK", Password)));

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Contains("Basic", none.Headers.WwwAuthenticate.Select(h => h.Scheme));
        Assert.Equal(HttpStatusCode.Unauthorized, page.StatusCode);                          // a page gets the popup, not a redirect
        Assert.Contains("Basic", page.Headers.WwwAuthenticate.Select(h => h.Scheme));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, rightPage.StatusCode);                          // through the wall (the test host serves no files)
    }

    [Fact]
    public async Task Basic_TheLocalNetworkIsStillFree_AndFiveWrongAttemptsLockTheAddress()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Configure(host, AuthMethod.Basic);

        using var lan = await Get(host, "/api/settings", Lan);
        Assert.Equal(HttpStatusCode.OK, lan.StatusCode);

        for (var i = 0; i < 5; i++) { using var _ = await Get(host, "/api/settings", Outside, ("Authorization", Basic("mark", "wrong password!!"))); }
        using var locked = await Get(host, "/api/settings", Outside, ("Authorization", Basic("mark", Password)));
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
    }

    // ---- the rule: Enabled, Localhost, None, proxy headers, CGNAT -------------------------------------------------------------------------------------

    [Fact]
    public async Task Enabled_AsksEveryone_TheComputerItselfIncluded()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Configure(host, required: AuthRequired.Enabled);

        using var local = await Get(host, "/api/run/status", "127.0.0.1");
        using var lan = await Get(host, "/api/run/status", Lan);

        Assert.Equal(HttpStatusCode.Unauthorized, local.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, lan.StatusCode);
    }

    [Fact]
    public async Task DisabledForLocalhost_FreesOnlyThisComputer()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Configure(host, required: AuthRequired.DisabledForLocalhost);

        using var local = await Get(host, "/api/run/status", "127.0.0.1");
        using var v6 = await Get(host, "/api/run/status", "::1");
        using var lan = await Get(host, "/api/run/status", Lan);

        Assert.Equal(HttpStatusCode.OK, local.StatusCode);
        Assert.Equal(HttpStatusCode.OK, v6.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, lan.StatusCode);
    }

    [Fact]
    public async Task NoAuthentication_FreesEveryone_EvenOutsiders()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Configure(host, AuthMethod.None, AuthRequired.Enabled, account: false);

        using var response = await Get(host, "/api/run/status", Outside);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("X-Forwarded-For", "192.168.1.5")]
    [InlineData("X-Forwarded-For", "127.0.0.1")]
    [InlineData("Forwarded", "for=192.168.1.5")]
    [InlineData("X-Real-IP", "192.168.1.5")]
    [InlineData("X-Forwarded-Host", "compressarr.example.com")]
    [InlineData("X-Forwarded-Proto", "https")]
    [InlineData("CF-Connecting-IP", "192.168.1.5")]
    public async Task AnyProxyHeader_MakesALocalPeerNotLocal_SoASpoofedHeaderCanOnlyTighten(string header, string value)
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Configure(host);

        using var plain = await Get(host, "/api/run/status", "127.0.0.1");
        using var proxied = await Get(host, "/api/run/status", "127.0.0.1", (header, value));
        using var proxiedLan = await Get(host, "/api/run/status", Lan, (header, value));

        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, proxied.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, proxiedLan.StatusCode);
    }

    [Fact]
    public async Task AForwardedHeader_NeverMakesAnOutsiderLocal()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Configure(host);

        using var response = await Get(host, "/api/run/status", Outside, ("X-Forwarded-For", "192.168.1.5"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Tailscale_AddressesAreLocalOnlyWhenTheBoxIsTicked()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Configure(host);
        using (var before = await Get(host, "/api/run/status", "100.101.102.103")) Assert.Equal(HttpStatusCode.Unauthorized, before.StatusCode);

        Configure(host, cgnat: true);

        using var after = await Get(host, "/api/run/status", "100.101.102.103");
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public async Task TheApiKeysOwnDoor_IsNotAskedForALogin_ButIsStillKeyed()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Configure(host, required: AuthRequired.Enabled);
        var key = host.Services.GetRequiredService<Compressarr.Core.Api.IApiKeyStore>().GetKey(Compressarr.Core.Api.ApiKeyScope.Read);

        using var withKey = await Get(host, "/api/v1/whoami", Outside, ("X-Api-Key", key));
        using var withoutKey = await Get(host, "/api/v1/whoami", Outside);

        Assert.Equal(HttpStatusCode.OK, withKey.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, withoutKey.StatusCode);
        Assert.Equal("missing_key", await ErrorCode(withoutKey));   // not login_required
    }

    // ---- the cross-site check ------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("cross-site", false)]
    [InlineData("same-site", false)]
    [InlineData("same-origin", true)]
    [InlineData("none", true)]
    public async Task AStateChangingRequest_FromAnotherSite_IsRefused_EvenWhenTheLoginWouldHaveLetItIn(string fetchSite, bool allowed)
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        using var response = await Send(host, HttpMethod.Post, "/api/run/release-hold", "127.0.0.1", null, ("Sec-Fetch-Site", fetchSite));

        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WithoutSecFetchSite_TheOriginMustBeThisSite()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        using var evil = await Send(host, HttpMethod.Post, "/api/run/release-hold", "127.0.0.1", null, ("Origin", "http://evil.example"));
        using var same = await Send(host, HttpMethod.Post, "/api/run/release-hold", "127.0.0.1", null, ("Origin", "http://localhost"));
        using var none = await Send(host, HttpMethod.Post, "/api/run/release-hold", "127.0.0.1");   // a script or curl: no browser, no origin

        Assert.Equal(HttpStatusCode.Forbidden, evil.StatusCode);
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
    }

    [Fact]
    public async Task ReadingFromAnotherSite_IsNotBlocked_OnlyChangingIs()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        using var response = await Get(host, "/api/run/status", "127.0.0.1", ("Sec-Fetch-Site", "cross-site"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheV1Api_IsNotSubjectToTheCrossSiteCheck()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var key = host.Services.GetRequiredService<Compressarr.Core.Api.IApiKeyStore>().GetKey(Compressarr.Core.Api.ApiKeyScope.Control);

        using var response = await Send(host, HttpMethod.Post, "/api/v1/schedule/run-anyway", "127.0.0.1", null, ("X-Api-Key", key), ("Sec-Fetch-Site", "cross-site"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- allowed hosts -------------------------------------------------------------------------------------------------------------------

    private static void AllowHosts(QueueHost host, string hosts)
    {
        host.Services.GetRequiredService<IConfigStore>().Update(AppPaths.GetConfigFilePath(), c => { c.Web.AllowedHosts = hosts; return true; });
        host.Services.GetRequiredService<WebAuthGate>().Invalidate();
    }

    [Fact]
    public async Task AllowedHosts_ASetListAnswersOnlyToItsNames_AndLocalhostAlways()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        AllowHosts(host, "Compressarr.LAN, 192.168.10.91");

        using var listed = await Get(host, "/api/run/status", Lan, ("Host", "compressarr.lan:1212"));
        using var ip = await Get(host, "/api/run/status", Lan, ("Host", "192.168.10.91:1212"));
        using var rebinding = await Get(host, "/api/run/status", Lan, ("Host", "evil.example:1212"));
        using var localhost = await Get(host, "/api/run/status", "127.0.0.1", ("Host", "localhost:1212"));
        using var loopback = await Get(host, "/api/run/status", "127.0.0.1", ("Host", "127.0.0.1:1212"));
        using var v6 = await Get(host, "/api/run/status", "::1", ("Host", "[::1]:1212"));

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ip.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, rebinding.StatusCode);
        Assert.Equal(HttpStatusCode.OK, localhost.StatusCode);
        Assert.Equal(HttpStatusCode.OK, loopback.StatusCode);
        Assert.Equal(HttpStatusCode.OK, v6.StatusCode);
    }

    [Theory]
    [InlineData("192.168.10.91:1212", true)]
    [InlineData("[fe80::1]:1212", true)]
    [InlineData("nas:1212", true)]
    [InlineData("MEDIA-PC", true)]
    [InlineData("nas.local:1212", true)]
    [InlineData("server.lan", true)]
    [InlineData("compressarr.home.arpa", true)]
    [InlineData("media-pc.tail1234.ts.net:1212", true)]
    [InlineData("rebind.evil.example:1212", false)]
    [InlineData("compressarr.example.com", false)]
    [InlineData("x.local.evil.example", false)]
    public async Task AllowedHostsEmpty_AnswersToTheNamesOfAHomeNetwork_ButNotToADomainNameARebindingPageWouldUse(string hostHeader, bool answers)
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        using var response = await Get(host, "/api/run/status", Lan, ("Host", hostHeader));

        Assert.Equal(answers ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AllowedHostsEmpty_LeavesAProxiedRequestAndTheKeyedApiAlone_BecauseNeitherCarriesTheLocalFreePass()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Configure(host);

        // Through a proxy (a forwarding header) there is no free pass: it is the sign-in that decides, not the name.
        using var proxied = await Get(host, "/api/run/status", Lan, ("Host", "compressarr.example.com"), ("X-Forwarded-For", "203.0.113.9"));
        using var v1 = await Get(host, "/api/v1/whoami", Lan, ("Host", "compressarr.example.com"));
        using var plain = await Get(host, "/api/run/status", Lan, ("Host", "compressarr.example.com"));

        Assert.Equal(HttpStatusCode.Unauthorized, proxied.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, v1.StatusCode);   // reached the key check (missing_key), not the host check
        Assert.Equal(HttpStatusCode.BadRequest, plain.StatusCode);
    }

    [Fact]
    public async Task AllowedHostsEmpty_LocalhostAndLoopbackAlwaysAnswer()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        using var localhost = await Get(host, "/api/run/status", "127.0.0.1", ("Host", "localhost:1212"));
        using var v6 = await Get(host, "/api/run/status", "::1", ("Host", "[::1]:1212"));

        Assert.Equal(HttpStatusCode.OK, localhost.StatusCode);
        Assert.Equal(HttpStatusCode.OK, v6.StatusCode);
    }

    [Fact]
    public async Task AllowedHosts_AlsoGuardTheV1Api_AndTheLoginPage()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        AllowHosts(host, "compressarr.lan");

        using var v1 = await Get(host, "/api/v1/whoami", Lan, ("Host", "evil.example"));
        using var login = await Get(host, "/api/auth/status", Outside, ("Host", "evil.example"));

        Assert.Equal(HttpStatusCode.BadRequest, v1.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, login.StatusCode);
    }

    [Theory]
    [InlineData("a.example, B.example:8080 , [::1], 10.0.0.1:1212", new[] { "a.example", "b.example", "::1", "10.0.0.1" })]
    [InlineData("", new string[0])]
    [InlineData("  ,; ", new string[0])]
    [InlineData("one;two three", new[] { "one", "two", "three" })]
    public void TheHostList_IsParsedLeniently(string text, string[] expected) =>
        Assert.Equal(expected, WebAuthGate.ParseHosts(text));

    // ---- /api/auth -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Status_TellsALoginPageWhatItNeedsToKnow_ButNeverTheUsernameToAStranger()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Configure(host);

        var stranger = JsonNode.Parse(await (await Get(host, "/api/auth/status", Outside)).Content.ReadAsStringAsync())!;
        var neighbour = JsonNode.Parse(await (await Get(host, "/api/auth/status", Lan)).Content.ReadAsStringAsync())!;
        var (_, token, _) = await SignIn(host);
        var signedIn = JsonNode.Parse(await (await Get(host, "/api/auth/status", Outside, ("Cookie", $"compressarr.session={token}"))).Content.ReadAsStringAsync())!;

        Assert.Equal(new[] { "credentialsSet", "free", "method", "needsLogin", "signedIn", "username" }, stranger.AsObject().Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal((true, false, false), (stranger["needsLogin"]!.GetValue<bool>(), stranger["free"]!.GetValue<bool>(), stranger["signedIn"]!.GetValue<bool>()));
        Assert.Null(stranger["username"]);
        Assert.Equal((false, true), (neighbour["needsLogin"]!.GetValue<bool>(), neighbour["free"]!.GetValue<bool>()));
        Assert.Equal("mark", neighbour["username"]!.GetValue<string>());
        Assert.Equal((true, false), (signedIn["signedIn"]!.GetValue<bool>(), signedIn["needsLogin"]!.GetValue<bool>()));
        Assert.Equal("mark", signedIn["username"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheSecurityCardsSettings_AreBehindTheWall_AndNeverCarryThePassword()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Configure(host);

        using var stranger = await Get(host, "/api/auth/settings", Outside);
        using var local = await Get(host, "/api/auth/settings", "127.0.0.1");
        var text = await local.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, stranger.StatusCode);
        var body = JsonNode.Parse(text)!;
        Assert.Equal(new[] { "hasPassword", "method", "requestIsLocal", "required", "trustCgnat", "username" }, body.AsObject().Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.Equal(("Forms", "DisabledForLocalAddresses", "mark", true), (body["method"]!.GetValue<string>(), body["required"]!.GetValue<string>(), body["username"]!.GetValue<string>(), body["hasPassword"]!.GetValue<bool>()));
        Assert.DoesNotContain(Password, text);
        Assert.DoesNotContain("pbkdf2", text);
    }

    private static string SettingsJson(string method = "Forms", string required = "DisabledForLocalAddresses", string? username = null, string? password = null, string? confirmation = null, string? current = null, bool cgnat = false) =>
        JsonSerializerOptionsFree(new { method, required, trustCgnat = cgnat, username, password, passwordConfirmation = confirmation ?? password, currentPassword = current });

    private static string JsonSerializerOptionsFree(object value) => System.Text.Json.JsonSerializer.Serialize(value);

    [Fact]
    public async Task FromTheLocalNetwork_TheAccountCanBeSetUp_WithoutAnyCurrentPassword()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        using var response = await Send(host, HttpMethod.Put, "/api/auth/settings", Lan, SettingsJson(username: "Mark", password: Password));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal(("mark", true), (body["username"]!.GetValue<string>(), body["hasPassword"]!.GetValue<bool>()));
        var (status, token, _) = await SignIn(host);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotNull(token);
    }

    [Theory]
    [InlineData("Forms", "DisabledForLocalAddresses", "mark", "short", "short", "The password needs at least 8 characters.")]
    [InlineData("Forms", "DisabledForLocalAddresses", "mark", "a long password phrase", "another long phrase", "The two passwords are not the same.")]
    [InlineData("Forms", "DisabledForLocalAddresses", "mark", "mark", "mark", "The password needs at least 8 characters.")]
    [InlineData("Forms", "Enabled", null, null, null, "Set a username and password first")]
    [InlineData("Basic", "DisabledForLocalhost", null, null, null, "Set a username and password first")]
    [InlineData("Forms", "Sometimes", "mark", "a long password phrase", "a long password phrase", "Authentication Required must be")]
    [InlineData("Plastic", "Enabled", "mark", "a long password phrase", "a long password phrase", "Authentication must be")]
    public async Task ABadSave_IsRefusedWithAMessage_AndNothingChanges(string method, string required, string? username, string? password, string? confirmation, string message)
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var before = host.Services.GetRequiredService<IWebAuth>().GetSettings();

        using var response = await Send(host, HttpMethod.Put, "/api/auth/settings", Lan, SettingsJson(method, required, username, password, confirmation));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(message, await response.Content.ReadAsStringAsync());
        Assert.Equal(before, host.Services.GetRequiredService<IWebAuth>().GetSettings());
    }

    [Fact]
    public async Task EnabledIsAllowed_OnceThereIsAnAccount()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Configure(host);

        using var response = await Send(host, HttpMethod.Put, "/api/auth/settings", Lan, SettingsJson(required: "Enabled"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);   // no new password typed: the existing account stays
        using var local = await Get(host, "/api/run/status", "127.0.0.1");
        Assert.Equal(HttpStatusCode.Unauthorized, local.StatusCode);
    }

    [Fact]
    public async Task FromOutside_ChangingTheSettings_NeedsTheCurrentPassword()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Configure(host);
        var (_, token, _) = await SignIn(host);
        var cookie = ("Cookie", $"compressarr.session={token}");

        using var none = await Send(host, HttpMethod.Put, "/api/auth/settings", Outside, SettingsJson(required: "Enabled"), cookie);
        using var wrong = await Send(host, HttpMethod.Put, "/api/auth/settings", Outside, SettingsJson(required: "Enabled", current: "wrong password!!"), cookie);
        using var right = await Send(host, HttpMethod.Put, "/api/auth/settings", Outside, SettingsJson(required: "Enabled", current: Password), cookie);

        Assert.Equal(HttpStatusCode.Forbidden, none.StatusCode);
        Assert.Equal("current_password_required", await ErrorCode(none));
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal("wrong_current_password", await ErrorCode(wrong));
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.Equal(AuthRequired.Enabled, host.Services.GetRequiredService<IWebAuth>().GetSettings().Required);
    }

    [Fact]
    public async Task ChangingThePasswordFromOutside_KeepsThisBrowserSignedIn_ButEndsTheOthers()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Configure(host);
        var (_, mine, _) = await SignIn(host);
        var (_, other, _) = await SignIn(host, peer: "198.51.100.7");

        using var response = await Send(host, HttpMethod.Put, "/api/auth/settings", Outside, SettingsJson(username: "mark", password: "a brand new phrase", current: Password), ("Cookie", $"compressarr.session={mine}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var fresh = SessionCookie(response);
        Assert.NotNull(fresh);
        Assert.NotEqual(mine, fresh);
        using var mineNow = await Get(host, "/api/run/status", Outside, ("Cookie", $"compressarr.session={mine}"));
        using var freshNow = await Get(host, "/api/run/status", Outside, ("Cookie", $"compressarr.session={fresh}"));
        using var otherNow = await Get(host, "/api/run/status", "198.51.100.7", ("Cookie", $"compressarr.session={other}"));
        Assert.Equal(HttpStatusCode.Unauthorized, mineNow.StatusCode);
        Assert.Equal(HttpStatusCode.OK, freshNow.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, otherNow.StatusCode);
        var (oldPassword, _, _) = await SignIn(host, peer: "198.51.100.8", password: Password);
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword);
    }

    [Fact]
    public async Task SignOutEverywhere_EndsAllSessions_AndClearsTheCookie()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        Configure(host);
        var (_, a, _) = await SignIn(host);
        var (_, b, _) = await SignIn(host, peer: "198.51.100.7");

        using var response = await Send(host, HttpMethod.Post, "/api/auth/sign-out-everywhere", Outside, null, ("Cookie", $"compressarr.session={a}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, JsonNode.Parse(await response.Content.ReadAsStringAsync())!["ended"]!.GetValue<int>());
        using var aNow = await Get(host, "/api/run/status", Outside, ("Cookie", $"compressarr.session={a}"));
        using var bNow = await Get(host, "/api/run/status", "198.51.100.7", ("Cookie", $"compressarr.session={b}"));
        Assert.Equal(HttpStatusCode.Unauthorized, aNow.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, bNow.StatusCode);
    }

    // ---- the way back --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task UnderEnabled_AForgottenPassword_IsFixedByTheResetThatPutsTheLocalNetworkBackIn()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var auth = Configure(host, required: AuthRequired.Enabled);
        using (var locked = await Get(host, "/api/settings", "127.0.0.1")) Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);

        auth.ResetToLocalOnly();
        host.Services.GetRequiredService<WebAuthGate>().Invalidate();

        using var again = await Get(host, "/api/settings", "127.0.0.1");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        using var newPassword = await Send(host, HttpMethod.Put, "/api/auth/settings", "127.0.0.1", SettingsJson(username: "mark", password: "a replacement phrase"));
        Assert.Equal(HttpStatusCode.OK, newPassword.StatusCode);   // the account is kept, the password replaced from the local network with no old one
    }
}
