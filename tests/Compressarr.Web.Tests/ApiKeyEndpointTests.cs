using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Compressarr.Core.Api;
using Compressarr.Core.Logging;
using Compressarr.Web.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Web.Tests;

/// <summary>The Automation API's front door, through the real endpoints: off until a key exists, a key in X-Api-Key or as a bearer token, wrong keys slowed
/// down, a revoked key dead at once, and the key itself shown exactly once.</summary>
public class ApiKeyEndpointTests
{
    private static readonly LaneSpec Lane1 = new("hdsd", "SD-HD");

    private static async Task<(string Key, long Id)> MakeKey(QueueHost host, string name = "Home Assistant", string scope = "read")
    {
        using var response = await host.Client.PostAsJsonAsync("/api/automation/keys", new { name, scope });
        response.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return (body["key"]!.GetValue<string>(), body["info"]!["id"]!.GetValue<long>());
    }

    private static Task<HttpResponseMessage> Whoami(QueueHost host, string? key = null, bool bearer = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/whoami");
        if (key is not null)
        {
            if (bearer) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            else request.Headers.TryAddWithoutValidation("X-Api-Key", key);
        }

        return host.Client.SendAsync(request);
    }

    private static async Task<string> ErrorCode(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!["code"]!.GetValue<string>();

    [Fact]
    public async Task WithNoKeyMade_TheApiIsOff_AndSaysWhereToMakeOne()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        using var response = await Whoami(host);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("no_keys", await ErrorCode(response));
        Assert.Contains("Settings > Automation API", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AKey_OpensTheApi_InTheHeader_OrAsABearerToken()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var (key, _) = await MakeKey(host, "Home Assistant", "control");

        using var viaHeader = await Whoami(host, key);
        using var viaBearer = await Whoami(host, key, bearer: true);

        Assert.Equal(HttpStatusCode.OK, viaHeader.StatusCode);
        Assert.Equal(HttpStatusCode.OK, viaBearer.StatusCode);
        var body = JsonNode.Parse(await viaHeader.Content.ReadAsStringAsync())!;
        Assert.Equal("Home Assistant", body["name"]!.GetValue<string>());
        Assert.Equal("control", body["scope"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(body["version"]!.GetValue<string>()));
    }

    [Fact]
    public async Task OnceAKeyExists_NoKeyAtAll_IsMissing_AndAWrongKeyIsInvalid()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        await MakeKey(host);

        using var none = await Whoami(host);
        using var wrong = await Whoami(host, "cmpk_wrong");

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal("missing_key", await ErrorCode(none));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("invalid_key", await ErrorCode(wrong));
    }

    [Fact]
    public async Task ARevokedKey_StopsWorkingAtOnce()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var (key, id) = await MakeKey(host);
        using (var before = await Whoami(host, key)) Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using var revoke = await host.Client.DeleteAsync($"/api/automation/keys/{id}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        using var after = await Whoami(host, key);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task TheKeyIsShownOnce_TheListHasOnlyItsFirstCharacters()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var (key, _) = await MakeKey(host, "Dashboard", "read");

        var listText = await host.Client.GetStringAsync("/api/automation/keys");

        Assert.DoesNotContain(key, listText);
        var item = JsonNode.Parse(listText)!["keys"]![0]!;
        Assert.Equal(key[..9], item["prefix"]!.GetValue<string>());
        Assert.Equal("Dashboard", item["name"]!.GetValue<string>());
        Assert.Equal("read", item["scope"]!.GetValue<string>());
        Assert.Null(item["lastUsedAt"]);

        using (var used = await Whoami(host, key)) used.EnsureSuccessStatusCode();
        Assert.NotNull(JsonNode.Parse(await host.Client.GetStringAsync("/api/automation/keys"))!["keys"]![0]!["lastUsedAt"]);
    }

    [Theory]
    [InlineData("", "read")]
    [InlineData("   ", "read")]
    [InlineData("ok", "admin")]
    [InlineData("ok", "")]
    public async Task ABadRequest_IsRefused_WithAMessage(string name, string scope)
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        using var response = await host.Client.PostAsJsonAsync("/api/automation/keys", new { name, scope });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(JsonNode.Parse(await response.Content.ReadAsStringAsync())!["message"]!.GetValue<string>()));
        Assert.Empty(JsonNode.Parse(await host.Client.GetStringAsync("/api/automation/keys"))!["keys"]!.AsArray());
    }

    [Fact]
    public async Task FiveWrongKeys_LockThatAddressOutForAMinute_EvenForTheRightKey()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var (key, _) = await MakeKey(host);

        for (var i = 0; i < ApiKeyThrottle.MaxFailures; i++)
        {
            using var wrong = await Whoami(host, "cmpk_wrong" + i);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var locked = await Whoami(host, key);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal("too_many_attempts", await ErrorCode(locked));
        Assert.True(locked.Headers.TryGetValues("Retry-After", out var retry) && int.Parse(retry.Single()) > 0);
    }

    [Fact]
    public async Task TheWebUisOwnEndpoints_AreNotBehindAKey()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        await MakeKey(host);

        using var status = await host.Client.GetAsync("/api/run/status");
        using var settings = await host.Client.GetAsync("/api/settings");

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
    }

    // ---- the scope check, on the filter itself (a control endpoint arrives in the next stage) ---------------------------------------------

    private sealed class OneKey : IApiKeyStore
    {
        private readonly ApiKeyInfo _info;
        public OneKey(ApiKeyScope scope) => _info = new ApiKeyInfo(1, "k", scope, "cmpk_xxxx", DateTimeOffset.UtcNow, null);
        public ApiKeyInfo? Authenticate(string key) => key == "good" ? _info : null;
        public CreatedApiKey Create(string name, ApiKeyScope scope) => throw new NotSupportedException();
        public IReadOnlyList<ApiKeyInfo> List() => new[] { _info };
        public bool Revoke(long id) => false;
    }

    private static async Task<(int Status, bool Reached)> Call(ApiKeyScope keyScope, ApiKeyScope? endpointScope)
    {
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        http.Request.Headers["X-Api-Key"] = "good";
        var metadata = endpointScope is { } need ? new EndpointMetadataCollection(new ApiScopeRequirement(need)) : EndpointMetadataCollection.Empty;
        http.SetEndpoint(new Endpoint(null, metadata, "test"));
        http.Response.Body = new MemoryStream();

        var filter = new ApiKeyFilter(new OneKey(keyScope), new ApiKeyThrottle(), new FileRunLogger());
        var reached = false;
        var result = await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(http), _ => { reached = true; return ValueTask.FromResult<object?>("ok"); });
        if (result is IResult r) await r.ExecuteAsync(http);
        return (reached ? 200 : http.Response.StatusCode, reached);
    }

    [Fact]
    public async Task AReadKey_CannotDoWhatNeedsControl_ButAControlKeyCanDoBoth()
    {
        Assert.Equal((403, false), await Call(ApiKeyScope.Read, ApiKeyScope.Control));
        Assert.Equal((200, true), await Call(ApiKeyScope.Control, ApiKeyScope.Control));
        Assert.Equal((200, true), await Call(ApiKeyScope.Control, ApiKeyScope.Read));
        Assert.Equal((200, true), await Call(ApiKeyScope.Read, ApiKeyScope.Read));
    }

    [Fact]
    public async Task AnEndpointWithNoScopeMarked_NeedsOnlyRead()
    {
        Assert.Equal((200, true), await Call(ApiKeyScope.Read, null));
    }
}
