using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Compressarr.Web.Tests;

/// <summary>The Radarr column's "Delete empty folders" check and button. The test host answers every outgoing request with a 404, which stands in
/// for a Radarr that cannot be read: the endpoints must say so in a sentence, never throw an empty server error.</summary>
public class RadarrFolderSettingEndpointTests
{
    private static readonly LaneSpec Lane1 = new("lane1", "Lane One");

    private static async Task<JsonObject> PostAsync(QueueHost host, string path, string url, string key)
    {
        using var response = await host.Client.PostAsJsonAsync(path, new JsonObject { ["enabled"] = true, ["url"] = url, ["apiKey"] = key });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    [Theory]
    [InlineData("/api/arr/radarr/delete-empty-folders/check")]
    [InlineData("/api/arr/radarr/delete-empty-folders/disable")]
    public async Task WithoutAUrlAndKey_TheAnswerSaysBothAreNeeded(string path)
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        var body = await PostAsync(host, path, "", "");

        Assert.False(body["success"]!.GetValue<bool>());
        Assert.Contains("required", body["message"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("/api/arr/radarr/delete-empty-folders/check")]
    [InlineData("/api/arr/radarr/delete-empty-folders/disable")]
    public async Task ARadarrThatCannotBeRead_GivesASentence_NotAServerError(string path)
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        var body = await PostAsync(host, path, "http://radarr.test:7878", "abc123");

        Assert.False(body["success"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(body["message"]!.GetValue<string>()));
        Assert.Contains(host.Http.Requests, u => u.AbsolutePath == "/api/v3/config/mediamanagement");   // it asked Radarr for exactly that, nothing else
    }
}
