using System.Text.Json.Nodes;
using Compressarr.Core.Arr;

namespace Compressarr.Core.Tests.Arr;

file sealed class MediaManagementArrClient : IArrClient
{
    public JsonObject Config { get; set; } = (JsonObject)JsonNode.Parse("""{"id": 1, "deleteEmptyFolders": true, "createEmptyMovieFolders": false, "recycleBin": "C:/bin", "autoUnmonitorPreviouslyDownloadedMovies": true}""")!;
    public bool IgnorePut { get; set; }
    public List<string> Calls { get; } = new();
    public JsonObject? PutBody { get; private set; }

    public Task<JsonNode?> GetAsync(string baseUrl, string apiKey, string relativePath, CancellationToken cancellationToken = default)
    {
        Calls.Add("GET " + relativePath);
        return Task.FromResult<JsonNode?>(Config.DeepClone());
    }

    public Task PutAsync(string baseUrl, string apiKey, string relativePath, JsonNode body, CancellationToken cancellationToken = default)
    {
        Calls.Add("PUT " + relativePath);
        PutBody = (JsonObject)body.DeepClone();
        if (!IgnorePut) Config = (JsonObject)body.DeepClone();
        return Task.CompletedTask;
    }

    public Task<JsonNode?> PostAsync(string baseUrl, string apiKey, string relativePath, JsonNode body, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

public class ArrMediaManagementTests
{
    [Fact]
    public async Task TheSettingIsReadAsRadarrReportsIt()
    {
        var client = new MediaManagementArrClient();

        Assert.True(await ArrMediaManagement.GetDeleteEmptyFoldersAsync(client, "http://radarr", "key"));

        client.Config["deleteEmptyFolders"] = false;
        Assert.False(await ArrMediaManagement.GetDeleteEmptyFoldersAsync(client, "http://radarr", "key"));

        client.Config.Remove("deleteEmptyFolders");
        Assert.Null(await ArrMediaManagement.GetDeleteEmptyFoldersAsync(client, "http://radarr", "key"));
    }

    [Fact]
    public async Task TurningItOff_ChangesThatOneFlag_AndWritesEverythingElseBackAsItWas()
    {
        var client = new MediaManagementArrClient();

        var changed = await ArrMediaManagement.TurnOffDeleteEmptyFoldersAsync(client, "http://radarr", "key");

        Assert.True(changed);
        Assert.Equal(new[] { "GET /api/v3/config/mediamanagement", "PUT /api/v3/config/mediamanagement/1", "GET /api/v3/config/mediamanagement" }, client.Calls);
        Assert.False(client.PutBody!["deleteEmptyFolders"]!.GetValue<bool>());
        Assert.False(client.PutBody["createEmptyMovieFolders"]!.GetValue<bool>());
        Assert.Equal("C:/bin", client.PutBody["recycleBin"]!.GetValue<string>());
        Assert.True(client.PutBody["autoUnmonitorPreviouslyDownloadedMovies"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AlreadyOff_MeansNothingIsWritten()
    {
        var client = new MediaManagementArrClient();
        client.Config["deleteEmptyFolders"] = false;

        var changed = await ArrMediaManagement.TurnOffDeleteEmptyFoldersAsync(client, "http://radarr", "key");

        Assert.False(changed);
        Assert.DoesNotContain(client.Calls, c => c.StartsWith("PUT"));
    }

    [Fact]
    public async Task ARadarrThatDoesNotReportTheSetting_IsNotTouched_AndSaysWhatToDoByHand()
    {
        var client = new MediaManagementArrClient();
        client.Config.Remove("deleteEmptyFolders");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ArrMediaManagement.TurnOffDeleteEmptyFoldersAsync(client, "http://radarr", "key"));

        Assert.Contains("Media Management", ex.Message);
        Assert.DoesNotContain(client.Calls, c => c.StartsWith("PUT"));
    }

    [Fact]
    public async Task IfRadarrStillReportsItOnAfterTheChange_ThatIsSaidNotHidden()
    {
        var client = new MediaManagementArrClient { IgnorePut = true };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ArrMediaManagement.TurnOffDeleteEmptyFoldersAsync(client, "http://radarr", "key"));

        Assert.Contains("still reports", ex.Message);
    }
}
