using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Compressarr.Core.Backup;
using Compressarr.Core.Catalog;
using Compressarr.Core.Config;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Web.Tests;

/// <summary>The catalog through the real endpoints: browse, install, the catalog tag on the Profiles list, delete (refused while a lane uses it),
/// the locked editors, and the catalog travelling with exports and backups.</summary>
public class CatalogEndpointTests
{
    private static readonly LaneSpec Lane1 = new("lane1", "Lane One");

    private static async Task<JsonNode> InstallAsync(QueueHost host, params (string Family, int Version, string? ImportAs)[] items)
    {
        var body = new JsonObject { ["items"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject { ["family"] = i.Family, ["version"] = i.Version, ["importAs"] = i.ImportAs }).ToArray()) };
        using var response = await host.Client.PostAsJsonAsync("/api/catalog/install", body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
    }

    private static async Task<JsonObject?> ProfileRowAsync(QueueHost host, string engine, string name)
    {
        var list = (await host.Client.GetFromJsonAsync<JsonObject>("/api/profiles"))!;
        return list["profiles"]!.AsArray().OfType<JsonObject>().FirstOrDefault(p => p["engine"]!.GetValue<string>() == engine && p["name"]!.GetValue<string>() == name);
    }

    [Fact]
    public async Task TheSourceAndTheListing_AreServed_AndAFailureIsAnErrorMessageNotACrash()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        host.Catalog.AddHandBrake("Fam A", 1);
        host.Catalog.AddFFmpeg("Fam B", 1);

        var source = (await host.Client.GetFromJsonAsync<JsonObject>("/api/catalog/source"))!;
        var listing = (await host.Client.GetFromJsonAsync<JsonObject>("/api/catalog"))!;

        Assert.Equal("Compressarr Presets", source["name"]!.GetValue<string>());
        Assert.Equal("official", source["kind"]!.GetValue<string>());
        Assert.True(listing["ok"]!.GetValue<bool>());
        var families = listing["catalog"]!["families"]!.AsArray();
        Assert.Equal(new[] { "Fam A", "Fam B" }, families.Select(f => f!["family"]!.GetValue<string>()).ToArray());
        Assert.Equal("new", families[0]!["versions"]![0]!["status"]!.GetValue<string>());

        host.Catalog.LoadFails = new CatalogException("Could not reach the catalog: offline");
        var failed = (await host.Client.GetFromJsonAsync<JsonObject>("/api/catalog"))!;
        Assert.False(failed["ok"]!.GetValue<bool>());
        Assert.Contains("offline", failed["error"]!.GetValue<string>());
        Assert.Equal("Compressarr Presets", failed["source"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task AnInstalledProfile_ShowsOnTheProfilesList_AsACatalogProfile_AndTheListingNowSaysInstalled()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        host.Catalog.AddHandBrake("Fam A", 1);
        host.Catalog.AddFFmpeg("Fam B", 1);

        var installed = await InstallAsync(host, ("Fam A", 1, null), ("Fam B", 1, null));

        Assert.All(installed["results"]!.AsArray(), r => Assert.Equal("installed", r!["status"]!.GetValue<string>()));
        var hb = await ProfileRowAsync(host, "handbrake", "Fam A v1");
        var ff = await ProfileRowAsync(host, "ffmpeg", "Fam B v1");
        Assert.True(hb!["catalog"]!.GetValue<bool>());
        Assert.False(hb["builtIn"]!.GetValue<bool>());
        Assert.True(ff!["catalog"]!.GetValue<bool>());
        var listing = (await host.Client.GetFromJsonAsync<JsonObject>("/api/catalog"))!;
        Assert.All(listing["catalog"]!["families"]!.AsArray(), f => Assert.Equal("installed", f!["versions"]![0]!["status"]!.GetValue<string>()));
    }

    [Fact]
    public async Task NothingTicked_IsRefused_AndARenamedImportIsAUserProfile()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        host.Catalog.AddHandBrake("Fam A", 1);

        using var empty = await host.Client.PostAsJsonAsync("/api/catalog/install", new JsonObject { ["items"] = new JsonArray() });
        var renamed = await InstallAsync(host, ("Fam A", 1, "My own copy"));

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal("importedAsUser", renamed["results"]![0]!["status"]!.GetValue<string>());
        var row = await ProfileRowAsync(host, "handbrake", "My own copy");
        Assert.False(row!["catalog"]!.GetValue<bool>());
        Assert.False(row["builtIn"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ACatalogProfile_IsLocked_ButCanBeDeleted_UnlessALaneUsesIt()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        host.Catalog.AddHandBrake("Fam A", 1);
        await InstallAsync(host, ("Fam A", 1, null));

        var edit = (await host.Client.GetFromJsonAsync<JsonObject>("/api/profiles/handbrake/Fam%20A%20v1"))!;
        Assert.True(edit["builtIn"]!.GetValue<bool>());   // the editor is read-only
        Assert.True(edit["catalog"]!.GetValue<bool>());
        using var put = await host.Client.PutAsJsonAsync("/api/profiles/handbrake/Fam%20A%20v1", new JsonObject { ["form"] = edit["form"]!.DeepClone(), ["baseName"] = null });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("catalog profile", await put.Content.ReadAsStringAsync());

        // a lane uses it
        var lane = (await host.Client.GetFromJsonAsync<JsonArray>("/api/lanes"))![0]!.AsObject();
        lane["tvPreset"] = "Fam A v1";
        lane["moviePreset"] = "Fam A v1";
        (await host.Client.PutAsJsonAsync("/api/lanes/lane1", lane)).EnsureSuccessStatusCode();
        using var refused = await host.Client.DeleteAsync("/api/profiles/handbrake/Fam%20A%20v1");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Lane One", await refused.Content.ReadAsStringAsync());
        Assert.NotNull(await ProfileRowAsync(host, "handbrake", "Fam A v1"));

        // free it, then it can go
        lane["tvPreset"] = "Compressarr SD-HD";
        lane["moviePreset"] = "Compressarr SD-HD";
        (await host.Client.PutAsJsonAsync("/api/lanes/lane1", lane)).EnsureSuccessStatusCode();
        using var deleted = await host.Client.DeleteAsync("/api/profiles/handbrake/Fam%20A%20v1");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Null(await ProfileRowAsync(host, "handbrake", "Fam A v1"));
        Assert.Empty(CatalogRecordFile.Load());
    }

    [Fact]
    public async Task AnFfmpegCatalogProfile_IsLockedAndDeletableToo()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        host.Catalog.AddFFmpeg("Fam B", 1);
        await InstallAsync(host, ("Fam B", 1, null));

        var edit = (await host.Client.GetFromJsonAsync<JsonObject>("/api/profiles/ffmpeg/Fam%20B%20v1"))!;
        using var put = await host.Client.PutAsJsonAsync("/api/profiles/ffmpeg/Fam%20B%20v1", edit["profile"]);
        using var deleted = await host.Client.DeleteAsync("/api/profiles/ffmpeg/Fam%20B%20v1");

        Assert.True(edit["builtIn"]!.GetValue<bool>());
        Assert.True(edit["catalog"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Null(await ProfileRowAsync(host, "ffmpeg", "Fam B v1"));
    }

    [Fact]
    public async Task ADamagedCatalogProfile_IsNotOffered_AndTheProfilesPageSaysSo()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        host.Catalog.AddHandBrake("Fam A", 1);
        await InstallAsync(host, ("Fam A", 1, null));
        var path = AppPaths.GetCatalogHandBrakeFilePath();
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"VideoQualitySlider\": 22", "\"VideoQualitySlider\": 3"));

        var list = (await host.Client.GetFromJsonAsync<JsonObject>("/api/profiles"))!;

        Assert.Null(await ProfileRowAsync(host, "handbrake", "Fam A v1"));
        Assert.Contains("Fam A v1", list["damagedCatalog"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.EndsWith("catalog-handbrake.json", list["catalogHandBrakeFilePath"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExportCarriesTheCatalogProfiles_NotAsUserProfiles_AndImportRestoresThem()
    {
        string exported;
        await using (var host = await QueueHost.StartAsync(Lane1))
        {
            host.Catalog.AddHandBrake("Fam A", 1);
            host.Catalog.AddFFmpeg("Fam B", 1);
            await InstallAsync(host, ("Fam A", 1, null), ("Fam B", 1, null));
            exported = await host.Client.GetStringAsync("/api/settings/export");
        }

        var root = JsonNode.Parse(exported)!.AsObject();
        Assert.Empty(root["handBrakeUserProfiles"]!.AsArray());   // a catalog profile is not one of the user's own
        Assert.Empty(root["ffmpegUserProfiles"]!.AsArray());
        Assert.Single(root["catalogProfiles"]!["handbrake"]!.AsArray());
        Assert.Single(root["catalogProfiles"]!["ffmpeg"]!.AsArray());

        await using var fresh = await QueueHost.StartAsync(Lane1);
        Assert.Null(await ProfileRowAsync(fresh, "handbrake", "Fam A v1"));
        using var response = await fresh.Client.PostAsync("/api/settings/import", new StringContent(exported, System.Text.Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        var hb = await ProfileRowAsync(fresh, "handbrake", "Fam A v1");
        var ff = await ProfileRowAsync(fresh, "ffmpeg", "Fam B v1");
        Assert.True(hb!["catalog"]!.GetValue<bool>());
        Assert.True(ff!["catalog"]!.GetValue<bool>());
        Assert.Equal(2, CatalogRecordFile.Load().Count);
    }

    [Fact]
    public async Task AnImportedCatalogProfile_WhoseDefinitionNoLongerMatchesItsRecord_IsNotAdded()
    {
        string exported;
        await using (var host = await QueueHost.StartAsync(Lane1))
        {
            host.Catalog.AddHandBrake("Fam A", 1);
            await InstallAsync(host, ("Fam A", 1, null));
            exported = await host.Client.GetStringAsync("/api/settings/export");
        }
        var tampered = exported.Replace("\"VideoQualitySlider\": 22", "\"VideoQualitySlider\": 2");
        Assert.NotEqual(exported, tampered);

        await using var fresh = await QueueHost.StartAsync(Lane1);
        using var response = await fresh.Client.PostAsync("/api/settings/import", new StringContent(tampered, System.Text.Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();

        Assert.Null(await ProfileRowAsync(fresh, "handbrake", "Fam A v1"));
    }

    [Fact]
    public async Task TheAutomaticBackup_IncludesTheCatalogProfiles_AndARestoreBringsThemBack()
    {
        await using var host = await QueueHost.StartAsync(Lane1);
        host.Catalog.AddHandBrake("Fam A", 1);
        host.Catalog.AddFFmpeg("Fam B", 1);
        await InstallAsync(host, ("Fam A", 1, null), ("Fam B", 1, null));
        var folder = Path.Combine(host.Root, "backups");
        host.Services.GetRequiredService<IConfigStore>().Update(AppPaths.GetConfigFilePath(), c => { c.Backup.FolderPath = folder; return true; });
        var backups = host.Services.GetRequiredService<IBackupService>();

        var made = await backups.RunBackupAsync();

        Assert.True(made.Success, made.Error);
        using (var zip = ZipFile.OpenRead(Path.Combine(folder, made.FileName!)))
        {
            var names = zip.Entries.Select(e => e.Name).ToList();
            Assert.Contains("catalog-handbrake.json", names);
            Assert.Contains("catalog-ffmpeg.json", names);
            Assert.Contains("catalog-installed.json", names);
        }

        File.Delete(AppPaths.GetCatalogHandBrakeFilePath());
        File.Delete(AppPaths.GetCatalogFFmpegFilePath());
        File.Delete(AppPaths.GetCatalogRecordsFilePath());
        Assert.Null(await ProfileRowAsync(host, "handbrake", "Fam A v1"));

        var restored = await backups.RestoreBackupAsync(made.FileName!);

        Assert.True(restored.Success, restored.Error);
        Assert.NotNull(await ProfileRowAsync(host, "handbrake", "Fam A v1"));
        Assert.NotNull(await ProfileRowAsync(host, "ffmpeg", "Fam B v1"));
    }
}
