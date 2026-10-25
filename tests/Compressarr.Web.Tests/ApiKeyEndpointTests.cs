using System.Net;
using System.Text.Json.Nodes;
using Compressarr.Core.Api;
using Compressarr.Core.Logging;
using Compressarr.Web.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Web.Tests;

/// <summary>The Automation API's front door and its two fixed keys, through the real endpoints: a key in X-Api-Key or as a bearer token, wrong keys slowed down, a
/// regenerated key replacing the old one at once, and the keys always visible to the Settings page (as in Sonarr).</summary>
public class ApiKeyEndpointTests
{
    private static readonly LaneSpec Lane1 = new("hdsd", "SD-HD");

    private static async Task<(string Control, string Read)> Keys(QueueHost host)
    {
        var list = JsonNode.Parse(await host.Client.GetStringAsync("/api/automation/keys"))!["keys"]!.AsArray();
        return (list.Single(k => k!["scope"]!.GetValue<string>() == "control")!["key"]!.GetValue<string>(),
                list.Single(k => k!["scope"]!.GetValue<string>() == "read")!["key"]!.GetValue<string>());
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
    public async Task TheTwoKeys_ExistFromTheStart_WithTheirNames_AndAreTheSameOnEveryLoad()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        var first = JsonNode.Parse(await host.Client.GetStringAsync("/api/automation/keys"))!["keys"]!.AsArray();
        var (control, read) = await Keys(host);

        Assert.Equal(2, first.Count);
        Assert.Equal(new[] { "API Key", "Read-only API Key" }, first.Select(k => k!["name"]!.GetValue<string>()).ToArray());
        Assert.StartsWith("cmpk_", control);
        Assert.StartsWith("cmpk_", read);
        Assert.NotEqual(control, read);
        Assert.Equal((control, read), await Keys(host));
    }

    [Fact]
    public async Task AKey_OpensTheApi_InTheHeader_OrAsABearerToken_WithItsScopeAndName()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var (control, read) = await Keys(host);

        using var viaHeader = await Whoami(host, control);
        using var viaBearer = await Whoami(host, control, bearer: true);
        using var readKey = await Whoami(host, read);

        Assert.Equal(HttpStatusCode.OK, viaHeader.StatusCode);
        Assert.Equal(HttpStatusCode.OK, viaBearer.StatusCode);
        var body = JsonNode.Parse(await viaHeader.Content.ReadAsStringAsync())!;
        Assert.Equal(("API Key", "control"), (body["name"]!.GetValue<string>(), body["scope"]!.GetValue<string>()));
        Assert.False(string.IsNullOrWhiteSpace(body["version"]!.GetValue<string>()));
        var readBody = JsonNode.Parse(await readKey.Content.ReadAsStringAsync())!;
        Assert.Equal(("Read-only API Key", "read"), (readBody["name"]!.GetValue<string>(), readBody["scope"]!.GetValue<string>()));
    }

    [Fact]
    public async Task NoKeyAtAll_IsMissing_AndAWrongKeyIsInvalid()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        using var none = await Whoami(host);
        using var wrong = await Whoami(host, "cmpk_wrong");

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal("missing_key", await ErrorCode(none));
        Assert.Contains("Settings > Security", await none.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("invalid_key", await ErrorCode(wrong));
    }

    [Fact]
    public async Task RegeneratingAKey_ReplacesItAtOnce_AndLeavesTheOtherKeyAlone()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var (control, read) = await Keys(host);

        using var response = await host.Client.PostAsync("/api/automation/keys/control/regenerate", null);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var replaced = body["key"]!.GetValue<string>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("control", body["scope"]!.GetValue<string>());
        Assert.NotEqual(control, replaced);
        using (var old = await Whoami(host, control)) Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode);
        using (var fresh = await Whoami(host, replaced)) Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        using (var other = await Whoami(host, read)) Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Equal((replaced, read), await Keys(host));
    }

    [Fact]
    public async Task RegeneratingAnUnknownScope_Is404()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        using var response = await host.Client.PostAsync("/api/automation/keys/admin/regenerate", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task FiveWrongKeys_LockThatAddressOutForAMinute_EvenForTheRightKey()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var (control, _) = await Keys(host);

        for (var i = 0; i < ApiKeyThrottle.MaxFailures; i++)
        {
            using var wrong = await Whoami(host, "cmpk_wrong" + i);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        using var locked = await Whoami(host, control);
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal("too_many_attempts", await ErrorCode(locked));
        Assert.True(locked.Headers.TryGetValues("Retry-After", out var retry) && int.Parse(retry.Single()) > 0);
    }

    [Fact]
    public async Task TheWebUisOwnEndpoints_AreNotBehindAKey()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        using var status = await host.Client.GetAsync("/api/run/status");
        using var settings = await host.Client.GetAsync("/api/settings");

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
    }

    // ---- the scope check, on the filter itself ---------------------------------------------------------------------------------------------

    private sealed class OneKey : IApiKeyStore
    {
        private readonly ApiKeyInfo _info;
        public OneKey(ApiKeyScope scope) => _info = new ApiKeyInfo(ApiKeys.DisplayName(scope), scope);
        public ApiKeyInfo? Authenticate(string key) => key == "good" ? _info : null;
        public string GetKey(ApiKeyScope scope) => "good";
        public string Regenerate(ApiKeyScope scope) => "good";
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
