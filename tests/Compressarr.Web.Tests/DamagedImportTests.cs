using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Compressarr.Web.Tests;

/// <summary>Importing or restoring a damaged or wrong file refuses with a plain message and changes nothing.</summary>
public class DamagedImportTests
{
    private static readonly LaneSpec Lane1 = new("hdsd", "SD-HD");

    private static Task<HttpResponseMessage> Import(QueueHost host, string body) =>
        host.Client.PostAsync("/api/settings/import", new StringContent(body, Encoding.UTF8, "application/json"));

    private static async Task<string> MessageOf(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["message"]!.GetValue<string>();

    [Fact]
    public async Task ATruncatedExport_IsRefusedInPlainWords_AndNothingChanges()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var export = await host.Client.GetStringAsync("/api/settings/export");

        foreach (var damaged in new[] { export[..(export.Length / 2)], export[..(export.Length - 5)], "", "this is not json", "null", "[]" })
        {
            using var response = await Import(host, damaged);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var message = await MessageOf(response);
            Assert.DoesNotContain("compressarr-import-", message); // no throwaway temp-file path in what the person reads
            Assert.Equal(export, await host.Client.GetStringAsync("/api/settings/export"));
        }
    }

    [Fact]
    public async Task AValidFileWithNoCompressarrSettings_IsRefused_InsteadOfResettingEverythingToDefaults()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        var export = await host.Client.GetStringAsync("/api/settings/export");

        foreach (var wrongFile in new[] { "{}", """{ "PresetList": [], "VersionMajor": 72 }""" }) // an empty object; a HandBrake presets file
        {
            using var response = await Import(host, wrongFile);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("doesn't look like a Compressarr export", await MessageOf(response));
            Assert.Equal(export, await host.Client.GetStringAsync("/api/settings/export"));
        }
    }

    [Fact]
    public async Task ADamagedBackupZip_IsRefusedInPlainWords_AndNothingChanges()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        using var made = await host.Client.PostAsync("/api/backups/run", null);
        var name = JsonNode.Parse(await made.Content.ReadAsStringAsync())!["fileName"]!.GetValue<string>();
        var zip = await host.Client.GetByteArrayAsync($"/api/backups/download?fileName={Uri.EscapeDataString(name)}");
        var folder = Directory.CreateTempSubdirectory("compressarr-damaged-backup-").FullName;
        File.WriteAllBytes(Path.Combine(folder, "truncated.zip"), zip[..(zip.Length / 2)]);
        File.WriteAllText(Path.Combine(folder, "text.zip"), "not a zip");
        var before = await host.Client.GetStringAsync("/api/settings/export");

        foreach (var file in new[] { "truncated.zip", "text.zip" })
        {
            using var response = await host.Client.PostAsync("/api/backups/restore",
                new StringContent($$"""{ "fileName": "{{file}}", "folder": {{JsonSerializer.Serialize(folder)}} }""", Encoding.UTF8, "application/json"));

            Assert.Contains("damaged or incomplete", await MessageOf(response));
            Assert.Equal(before, await host.Client.GetStringAsync("/api/settings/export"));
        }
    }
}
