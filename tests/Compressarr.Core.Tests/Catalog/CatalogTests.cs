using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Compressarr.Core.Catalog;
using Compressarr.Core.Config;
using Compressarr.Core.FFmpeg;
using Compressarr.Core.Presets;
using Compressarr.Core.Tests.Presets;

namespace Compressarr.Core.Tests.Catalog;

/// <summary>A catalog served from memory: files by their catalog path, with the checksums computed from the bytes (or overridden to test a tampered file).</summary>
internal sealed class FakeCatalog : ICatalogClient
{
    public Dictionary<string, byte[]> Files { get; } = new();
    public CatalogDocument Document { get; } = new() { Schema = 1, Catalog = new CatalogInfo { Name = "Compressarr Presets", Maintainer = "tester", Url = "https://example.invalid/presets" }, Updated = "2026-10-11" };
    public string Release { get; set; } = "v-test";
    public Exception? LoadFails { get; set; }
    public CatalogSource Source { get; } = new("official", "Compressarr Presets", "https://example.invalid/presets", CatalogSourceKind.Official);

    public Task<CatalogListing> LoadAsync(CancellationToken cancellationToken) =>
        LoadFails is null ? Task.FromResult(new CatalogListing(Source, Document, Release)) : Task.FromException<CatalogListing>(LoadFails);

    public Task<byte[]> DownloadAsync(CatalogListing listing, CatalogEntry entry, CancellationToken cancellationToken) =>
        Files.TryGetValue(entry.File, out var bytes) ? Task.FromResult(bytes) : throw new CatalogException("not found");

    public static string HandBrakeFile(string name, string encoder = "x265_10bit", int profiles = 1)
    {
        var leaves = new JsonArray();
        for (var i = 0; i < profiles; i++)
        {
            leaves.Add(new JsonObject
            {
                ["PresetName"] = i == 0 ? name : name + " extra",
                ["PresetDescription"] = "A test profile.",
                ["FileFormat"] = "av_mkv",
                ["Folder"] = false,
                ["Type"] = 1,
                ["VideoEncoder"] = encoder,
                ["VideoQualitySlider"] = 22
            });
        }

        return new JsonObject { ["PresetList"] = leaves, ["VersionMajor"] = 72, ["VersionMicro"] = 0, ["VersionMinor"] = 0 }.ToJsonString();
    }

    public static string FFmpegFile(string name, string codec = "av1_nvenc")
    {
        var profile = new FFmpegProfile { Name = name, Description = "A test profile.", VideoCodec = codec, Preset = "p5", PixFmt = "p010le", Quality = 34, AudioCodec = "eac3", AudioBitrateKbps = 640 };
        return JsonSerializer.Serialize(profile, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    }

    public CatalogEntry Add(string family, int version, string engine, string content, string? sha = null, string minApp = "2.2.0", string? deprecated = null)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var entry = new CatalogEntry
        {
            Family = family, Version = version, Engine = engine, File = $"profiles/{engine}/{family.Replace(' ', '-')}-v{version}.json",
            Sha256 = sha ?? CatalogHash.OfBytes(bytes), MinAppVersion = minApp, Description = "d", Changelog = "c", Published = "2026-10-11", Author = "tester", Deprecated = deprecated,
            Requires = new List<string> { "x265" }
        };
        Files[entry.File] = bytes;
        Document.Profiles.Add(entry);
        return entry;
    }
}

public class CatalogStoreTests : AppDataTestBase
{
    private static InstalledCatalogProfile Record(string name, string engine, int version = 1) =>
        new(name, engine, name.Replace($" v{version}", ""), version, "00", "", "Compressarr Presets", "https://example.invalid", "v-test", "tester", DateTime.UtcNow);

    private static JsonObject HbDefinition(string name, int quality = 22) =>
        (JsonObject)HandBrakePresetFile.GetLeaves(JsonNode.Parse(FakeCatalog.HandBrakeFile(name)))[0].Definition.DeepClone().AsObject().Also(o => o["VideoQualitySlider"] = quality);

    [Fact]
    public void ACatalogHandBrakeProfile_IsListedAsCatalog_LockedLikeABuiltIn_AndGivenToHandBrake()
    {
        var store = new HandBrakeProfileStore();
        store.AddCatalogProfile(HbDefinition("Cat One v1"), Record("Cat One v1", "handbrake"));

        var all = new HandBrakeProfileStore().GetAll(); // a fresh instance reads it back from disk
        var profile = Assert.Single(all, p => p.Name == "Cat One v1");

        Assert.True(profile.IsCatalog);
        Assert.False(profile.IsBuiltIn);
        Assert.Empty(store.DamagedCatalogProfiles);
        Assert.True(HandBrakePresetFile.TryRead(AppPaths.GetHandBrakeActivePresetsFilePath(), out var active, out _));
        Assert.Contains(active!["PresetList"]!.AsArray(), f => f!["PresetName"]!.GetValue<string>() == "Compressarr (catalog)");
        Assert.Contains(HandBrakePresetFile.GetLeaves(active), l => l.Name == "Cat One v1"); // HandBrake can run it
    }

    [Fact]
    public void ACatalogProfile_EditedBehindTheAppsBack_IsNotOffered_AndIsReportedDamaged()
    {
        var store = new HandBrakeProfileStore();
        store.AddCatalogProfile(HbDefinition("Cat One v1"), Record("Cat One v1", "handbrake"));
        store.AddCatalogProfile(HbDefinition("Cat Two v1"), Record("Cat Two v1", "handbrake"));
        var path = AppPaths.GetCatalogHandBrakeFilePath();
        var text = File.ReadAllText(path);
        var at = text.IndexOf("\"VideoQualitySlider\": 22", StringComparison.Ordinal); // only the first profile's copy
        File.WriteAllText(path, text.Remove(at, "\"VideoQualitySlider\": 22".Length).Insert(at, "\"VideoQualitySlider\": 5"));

        var reread = new HandBrakeProfileStore();

        Assert.DoesNotContain(reread.GetAll(), p => p.Name == "Cat One v1");
        Assert.Contains("Cat One v1", reread.DamagedCatalogProfiles);

        reread.RemoveCatalogProfile("Cat One v1"); // delete-and-add-again fixes it
        Assert.Empty(new HandBrakeProfileStore().DamagedCatalogProfiles);
    }

    [Fact]
    public void AddingAnotherCatalogProfile_KeepsOneThatReadsAsDamaged_AndRefusesItsName()
    {
        var store = new HandBrakeProfileStore();
        store.AddCatalogProfile(HbDefinition("Cat One v1"), Record("Cat One v1", "handbrake"));
        var path = AppPaths.GetCatalogHandBrakeFilePath();
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"VideoQualitySlider\": 22", "\"VideoQualitySlider\": 5"));

        var reread = new HandBrakeProfileStore();
        reread.AddCatalogProfile(HbDefinition("Cat Two v1"), Record("Cat Two v1", "handbrake"));

        var after = new HandBrakeProfileStore();
        Assert.Contains("Cat One v1", after.DamagedCatalogProfiles);                       // still there for the user to delete, not silently dropped
        Assert.Contains(after.GetAll(), p => p.Name == "Cat Two v1" && p.IsCatalog);
        Assert.Throws<InvalidOperationException>(() => after.AddCatalogProfile(HbDefinition("Cat One v1"), Record("Cat One v1", "handbrake")));
    }

    [Fact]
    public void ACatalogFileThatCannotBeRead_IsReported_SoTheProfilesDoNotJustVanish()
    {
        Assert.Null(new HandBrakeProfileStore().CatalogFileError);
        Assert.Null(new FFmpegProfileStore().CatalogFileError);
        new HandBrakeProfileStore().AddCatalogProfile(HbDefinition("Cat One v1"), Record("Cat One v1", "handbrake"));
        File.WriteAllText(AppPaths.GetCatalogHandBrakeFilePath(), "{ not json");
        File.WriteAllText(AppPaths.GetCatalogFFmpegFilePath(), "{ not json");

        var hb = new HandBrakeProfileStore();
        var ff = new FFmpegProfileStore();

        Assert.Contains("could not be read", hb.CatalogFileError);
        Assert.Contains(AppPaths.GetCatalogHandBrakeFilePath(), hb.CatalogFileError);
        Assert.Contains("could not be read", ff.CatalogFileError);
        Assert.DoesNotContain(hb.GetAll(), p => p.IsCatalog);
    }

    [Fact]
    public void ACatalogFileThatCannotBeRead_IsNeverOverwrittenByAnInstall()
    {
        new HandBrakeProfileStore().AddCatalogProfile(HbDefinition("Cat One v1"), Record("Cat One v1", "handbrake"));
        new FFmpegProfileStore().AddCatalogProfile(new FFmpegProfile { Name = "FF One v1", VideoCodec = "av1_nvenc", Preset = "p5", PixFmt = "p010le", Quality = 34 }, Record("FF One v1", "ffmpeg"));
        File.WriteAllText(AppPaths.GetCatalogHandBrakeFilePath(), "{ not json");
        File.WriteAllText(AppPaths.GetCatalogFFmpegFilePath(), "{ not json");

        Assert.Throws<InvalidOperationException>(() => new HandBrakeProfileStore().AddCatalogProfile(HbDefinition("Cat Two v1"), Record("Cat Two v1", "handbrake")));
        Assert.Throws<InvalidOperationException>(() => new FFmpegProfileStore().AddCatalogProfile(
            new FFmpegProfile { Name = "FF Two v1", VideoCodec = "av1_nvenc", Preset = "p5", PixFmt = "p010le", Quality = 34 }, Record("FF Two v1", "ffmpeg")));

        Assert.Equal("{ not json", File.ReadAllText(AppPaths.GetCatalogHandBrakeFilePath()));
        Assert.Equal("{ not json", File.ReadAllText(AppPaths.GetCatalogFFmpegFilePath()));
    }

    [Fact]
    public void ACatalogProfileWithNoRecord_IsNotTrusted()
    {
        var store = new HandBrakeProfileStore();
        store.AddCatalogProfile(HbDefinition("Cat One v1"), Record("Cat One v1", "handbrake"));
        File.Delete(AppPaths.GetCatalogRecordsFilePath());

        Assert.DoesNotContain(new HandBrakeProfileStore().GetAll(), p => p.Name == "Cat One v1");
    }

    [Fact]
    public void Names_AreUniqueAcrossBuiltInCatalogAndUser_ForHandBrake()
    {
        var store = new HandBrakeProfileStore();
        store.AddCatalogProfile(HbDefinition("Cat One v1"), Record("Cat One v1", "handbrake"));

        Assert.Throws<InvalidOperationException>(() => store.AddCatalogProfile(HbDefinition("cat one V1"), Record("Cat One v1", "handbrake")));
        Assert.Throws<InvalidOperationException>(() => store.AddCatalogProfile(HbDefinition("Compressarr SD-HD"), Record("Compressarr SD-HD", "handbrake")));
        Assert.Throws<InvalidOperationException>(() => store.AddUserProfiles(new[] { HbDefinition("Cat One v1") }));

        store.AddUserProfiles(new[] { HbDefinition("Mine") });
        Assert.Throws<InvalidOperationException>(() => store.AddCatalogProfile(HbDefinition("Mine"), Record("Mine", "handbrake")));
    }

    [Fact]
    public void ACatalogProfile_CannotBeEdited_OrDeletedAsAUserProfile_ButCanBeRemovedAsACatalogOne()
    {
        var store = new HandBrakeProfileStore();
        store.AddCatalogProfile(HbDefinition("Cat One v1"), Record("Cat One v1", "handbrake"));

        var edit = Assert.Throws<InvalidOperationException>(() => store.ReplaceUserProfile("Cat One v1", HbDefinition("Cat One v1")));
        Assert.Contains("catalog profile", edit.Message);
        Assert.Throws<InvalidOperationException>(() => store.RemoveUserProfile("Cat One v1"));

        store.RemoveCatalogProfile("Cat One v1");

        Assert.DoesNotContain(store.GetAll(), p => p.Name == "Cat One v1");
        Assert.DoesNotContain(CatalogRecordFile.Load(), r => r.Name == "Cat One v1");
        Assert.Throws<InvalidOperationException>(() => store.RemoveCatalogProfile("Cat One v1"));
        Assert.True(HandBrakePresetFile.TryRead(AppPaths.GetHandBrakeActivePresetsFilePath(), out var active, out _));
        Assert.DoesNotContain(HandBrakePresetFile.GetLeaves(active), l => l.Name == "Cat One v1");
    }

    [Fact]
    public void AnFfmpegCatalogProfile_IsListedAsCatalog_DetectsDamage_AndEnforcesUniqueNames()
    {
        var store = new FFmpegProfileStore();
        var profile = new FFmpegProfile { Name = "FF One v1", VideoCodec = "av1_nvenc", Preset = "p5", PixFmt = "p010le", Quality = 34 };
        store.AddCatalogProfile(profile, Record("FF One v1", "ffmpeg"));

        var reread = new FFmpegProfileStore();
        var found = Assert.Single(reread.GetAll(), p => p.Name == "FF One v1");
        Assert.True(found.IsCatalog);
        Assert.False(found.IsBuiltIn);
        Assert.Throws<InvalidOperationException>(() => store.AddCatalogProfile(profile, Record("FF One v1", "ffmpeg")));
        Assert.Throws<InvalidOperationException>(() => store.AddUserProfiles(new[] { profile }));
        Assert.Contains("catalog profile", Assert.Throws<InvalidOperationException>(() => store.ReplaceUserProfile("FF One v1", profile)).Message);

        var path = AppPaths.GetCatalogFFmpegFilePath();
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"quality\": 34", "\"quality\": 8"));
        var damaged = new FFmpegProfileStore();
        Assert.DoesNotContain(damaged.GetAll(), p => p.Name == "FF One v1");
        Assert.Contains("FF One v1", damaged.DamagedCatalogProfiles);
    }

    [Fact]
    public void AnFfmpegCatalogProfile_CanBeRemoved_AndAHandBrakeProfileOfTheSameNameIsUnaffected()
    {
        var hb = new HandBrakeProfileStore();
        var ff = new FFmpegProfileStore();
        hb.AddCatalogProfile(HbDefinition("Same v1"), Record("Same v1", "handbrake"));
        ff.AddCatalogProfile(new FFmpegProfile { Name = "Same v1", VideoCodec = "av1_nvenc", Preset = "p5", PixFmt = "p010le" }, Record("Same v1", "ffmpeg")); // each encoder has its own names

        ff.RemoveCatalogProfile("Same v1");

        Assert.DoesNotContain(ff.GetAll(), p => p.Name == "Same v1");
        Assert.Contains(hb.GetAll(), p => p.Name == "Same v1");
        Assert.Single(CatalogRecordFile.Load());
    }
}

file static class JsonObjectExtensions
{
    public static JsonObject Also(this JsonObject o, Action<JsonObject> change)
    {
        change(o);
        return o;
    }
}

public class CatalogServiceTests : AppDataTestBase
{
    private readonly FakeCatalog _catalog = new();
    private readonly HandBrakeProfileStore _hb = new();
    private readonly FFmpegProfileStore _ff = new();

    private CatalogService Service() => new(_catalog, _hb, _ff);

    private static CatalogInstallRequest Pick(string family, int version, string? importAs = null) => new(family, version, importAs);

    [Fact]
    public async Task OneNameForBothEngines_InstallsEachOnItsOwn_WhenTheRequestSaysWhichEngine()
    {
        _catalog.Add("Both Ways", 1, "handbrake", FakeCatalog.HandBrakeFile("Both Ways v1"));
        _catalog.Add("Both Ways", 1, "ffmpeg", FakeCatalog.FFmpegFile("Both Ways v1"));

        var results = await Service().InstallAsync(new[]
        {
            new CatalogInstallRequest("Both Ways", 1, null, "handbrake"),
            new CatalogInstallRequest("Both Ways", 1, null, "ffmpeg")
        }, default);

        Assert.All(results, r => Assert.Equal("installed", r.Status));
        Assert.True(_hb.GetAll().Single(p => p.Name == "Both Ways v1").IsCatalog);
        Assert.True(_ff.GetAll().Single(p => p.Name == "Both Ways v1").IsCatalog);
    }

    [Fact]
    public async Task TheListing_GroupsByFamilyNewestFirst_AndSaysWhatEachVersionIs()
    {
        _catalog.Add("Fam A", 1, "handbrake", FakeCatalog.HandBrakeFile("Fam A v1"));
        _catalog.Add("Fam A", 2, "handbrake", FakeCatalog.HandBrakeFile("Fam A v2"));
        _catalog.Add("Fam B", 1, "ffmpeg", FakeCatalog.FFmpegFile("Fam B v1"), deprecated: "bad setting");
        _catalog.Add("Fam C", 1, "handbrake", FakeCatalog.HandBrakeFile("Fam C v1"), minApp: "99.0.0");

        var view = await Service().ListAsync(default);

        Assert.Equal("Compressarr Presets", view.SourceName);
        Assert.Equal("official", view.Kind);
        Assert.Equal("v-test", view.Release);
        Assert.Equal(new[] { "Fam A", "Fam B", "Fam C" }, view.Families.Select(f => f.Family).ToArray());
        Assert.Equal(new[] { 2, 1 }, view.Families[0].Versions.Select(v => v.Version).ToArray());
        Assert.All(view.Families[0].Versions, v => Assert.Equal("new", v.Status));
        Assert.Equal("bad setting", view.Families[1].Versions[0].Deprecated);
        Assert.Equal("needsApp", view.Families[2].Versions[0].Status);
        Assert.Equal(4, view.NotInstalled);
        Assert.Equal("Fam A v2 (copy)", view.Families[0].Versions[0].SuggestedName);
    }

    [Fact]
    public async Task AnInstalledVersion_ShowsAsInstalled_AndANewerOneAsNewerThanInstalled()
    {
        _catalog.Add("Fam A", 1, "handbrake", FakeCatalog.HandBrakeFile("Fam A v1"));
        _catalog.Add("Fam A", 2, "handbrake", FakeCatalog.HandBrakeFile("Fam A v2"));
        await Service().InstallAsync(new[] { Pick("Fam A", 1) }, default);

        var view = await Service().ListAsync(default);

        var versions = view.Families.Single().Versions;
        Assert.Equal("new", versions[0].Status);           // v2
        Assert.True(versions[0].NewerThanInstalled);       // information only - nothing is updated
        Assert.Equal("installed", versions[1].Status);     // v1
        Assert.False(versions[1].NewerThanInstalled);
        Assert.Equal(1, view.NotInstalled);
    }

    [Fact]
    public async Task Installing_AddsACatalogProfile_WithItsRecord_AndAHandBrakeAndAnFfmpegOneBothWork()
    {
        _catalog.Add("Fam A", 1, "handbrake", FakeCatalog.HandBrakeFile("Fam A v1"));
        _catalog.Add("Fam B", 1, "ffmpeg", FakeCatalog.FFmpegFile("Fam B v1"));

        var results = await Service().InstallAsync(new[] { Pick("Fam A", 1), Pick("Fam B", 1) }, default);

        Assert.All(results, r => Assert.Equal("installed", r.Status));
        Assert.True(_hb.GetAll().Single(p => p.Name == "Fam A v1").IsCatalog);
        Assert.True(_ff.GetAll().Single(p => p.Name == "Fam B v1").IsCatalog);
        var record = CatalogRecordFile.Load().Single(r => r.Name == "Fam A v1");
        Assert.Equal("Compressarr Presets", record.SourceName);
        Assert.Equal("v-test", record.Release);
        Assert.Equal("tester", record.Author);
        Assert.Equal(2, CatalogRecordFile.Load().Count);
    }

    [Fact]
    public async Task AFileThatDoesNotMatchItsChecksum_IsThrownAway_AndNothingIsInstalled()
    {
        _catalog.Add("Fam A", 1, "handbrake", FakeCatalog.HandBrakeFile("Fam A v1"), sha: new string('0', 64));

        var result = Assert.Single(await Service().InstallAsync(new[] { Pick("Fam A", 1) }, default));

        Assert.Equal("failed", result.Status);
        Assert.Contains("checksum", result.Message);
        Assert.DoesNotContain(_hb.GetAll(), p => p.IsCatalog);
        Assert.Empty(CatalogRecordFile.Load());
    }

    [Fact]
    public async Task AFileThatIsNotWhatTheCatalogSaysItIs_IsRefused()
    {
        _catalog.Add("Fam A", 1, "handbrake", FakeCatalog.HandBrakeFile("Some Other Name v1"));              // wrong name inside
        _catalog.Add("Fam B", 1, "handbrake", FakeCatalog.HandBrakeFile("Fam B v1", profiles: 2));            // two profiles in one file
        _catalog.Add("Fam C", 1, "handbrake", FakeCatalog.HandBrakeFile("Fam C v1", encoder: ""));            // no encoder
        _catalog.Add("Fam D", 1, "handbrake", "{ not json");
        _catalog.Add("Fam E", 1, "ffmpeg", FakeCatalog.FFmpegFile("Fam E v1", codec: "libfoo"));            // fails the editor's checks
        _catalog.Add("Fam F", 1, "ffmpeg", FakeCatalog.FFmpegFile("Another Name v1"));

        var results = await Service().InstallAsync(new[] { Pick("Fam A", 1), Pick("Fam B", 1), Pick("Fam C", 1), Pick("Fam D", 1), Pick("Fam E", 1), Pick("Fam F", 1) }, default);

        Assert.All(results, r => Assert.Equal("failed", r.Status));
        Assert.Contains("not the name the catalog lists", results[0].Message);
        Assert.Contains("exactly one", results[1].Message);
        Assert.Contains("video encoder", results[2].Message);
        Assert.Contains("valid", results[3].Message);
        Assert.Contains("not a valid ffmpeg profile", results[4].Message);
        Assert.Contains("not the name the catalog lists", results[5].Message);
        Assert.Empty(CatalogRecordFile.Load());
    }

    [Fact]
    public async Task ATakenName_IsRefusedUntilTheUserChoosesANewOne_ThenItIsASeparateUserProfile()
    {
        _catalog.Add("Fam A", 1, "handbrake", FakeCatalog.HandBrakeFile("Fam A v1"));
        _hb.AddUserProfiles(new[] { (JsonObject)HandBrakePresetFile.GetLeaves(JsonNode.Parse(FakeCatalog.HandBrakeFile("Fam A v1")))[0].Definition.DeepClone() });

        var listed = (await Service().ListAsync(default)).Families.Single().Versions.Single();
        var refused = Assert.Single(await Service().InstallAsync(new[] { Pick("Fam A", 1) }, default));
        var renamed = Assert.Single(await Service().InstallAsync(new[] { Pick("Fam A", 1, "Fam A v1 (copy)") }, default));
        var clash = Assert.Single(await Service().InstallAsync(new[] { Pick("Fam A", 1, "fam a v1") }, default));

        Assert.Equal("nameUsed", listed.Status);
        Assert.Equal("failed", refused.Status);
        Assert.Contains("already exists", refused.Message);
        Assert.Equal("importedAsUser", renamed.Status);
        var copy = _hb.GetAll().Single(p => p.Name == "Fam A v1 (copy)");
        Assert.False(copy.IsCatalog);
        Assert.False(copy.IsBuiltIn); // a profile of the user's own: editable, deletable
        Assert.Contains("Copied from the Compressarr Presets catalog: Fam A v1.", copy.Definition["PresetDescription"]!.GetValue<string>());
        Assert.Empty(CatalogRecordFile.Load());
        Assert.Equal("failed", clash.Status);
    }

    [Fact]
    public async Task AnFfmpegProfile_ImportedUnderANewName_IsAUserProfile()
    {
        _catalog.Add("Fam B", 1, "ffmpeg", FakeCatalog.FFmpegFile("Fam B v1"));

        var result = Assert.Single(await Service().InstallAsync(new[] { Pick("Fam B", 1, "My AV1") }, default));

        Assert.Equal("importedAsUser", result.Status);
        var copy = _ff.GetAll().Single(p => p.Name == "My AV1");
        Assert.False(copy.IsCatalog);
        Assert.Contains("Copied from", copy.Description);
    }

    [Fact]
    public async Task AVersionNeedingANewerCompressarr_IsRefused_AndAnUnknownProfileIsReportedAsSuch()
    {
        _catalog.Add("Fam C", 1, "handbrake", FakeCatalog.HandBrakeFile("Fam C v1"), minApp: "99.0.0");

        var results = await Service().InstallAsync(new[] { Pick("Fam C", 1), Pick("Nope", 3) }, default);

        Assert.Contains("needs Compressarr 99.0.0", results[0].Message);
        Assert.Contains("not in the catalog", results[1].Message);
    }

    [Fact]
    public async Task ADeletedCatalogProfile_IsOfferedAgainAsNew_NeverAutomatically()
    {
        _catalog.Add("Fam A", 1, "handbrake", FakeCatalog.HandBrakeFile("Fam A v1"));
        await Service().InstallAsync(new[] { Pick("Fam A", 1) }, default);
        _hb.RemoveCatalogProfile("Fam A v1");

        var view = await Service().ListAsync(default);

        Assert.Equal("new", view.Families.Single().Versions.Single().Status);
        Assert.DoesNotContain(_hb.GetAll(), p => p.Name == "Fam A v1");
    }

    [Fact]
    public async Task ADamagedProfile_IsShownAsDamaged()
    {
        _catalog.Add("Fam A", 1, "handbrake", FakeCatalog.HandBrakeFile("Fam A v1"));
        await Service().InstallAsync(new[] { Pick("Fam A", 1) }, default);
        var path = AppPaths.GetCatalogHandBrakeFilePath();
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"VideoQualitySlider\": 22", "\"VideoQualitySlider\": 3"));

        var view = await new CatalogService(_catalog, new HandBrakeProfileStore(), new FFmpegProfileStore()).ListAsync(default);

        Assert.Equal("damaged", view.Families.Single().Versions.Single().Status);
    }

    [Fact]
    public async Task WhenTheCatalogCannotBeRead_TheReasonIsPassedOn()
    {
        _catalog.LoadFails = new CatalogException("Could not reach the catalog: offline");

        var ex = await Assert.ThrowsAsync<CatalogException>(() => Service().ListAsync(default));

        Assert.Contains("offline", ex.Message);
    }

    [Fact]
    public void WhenTheRunningVersionCannotBeRead_OnlyAProfileWithNoRequirementIsOffered()
    {
        Assert.False(CatalogService.MeetsAppVersion("2.2.0", null));
        Assert.True(CatalogService.MeetsAppVersion("", null));
    }

    [Theory]
    [InlineData("2.2.0", "2.2.0", true)]
    [InlineData("2.2.0", "2.2.1", false)]
    [InlineData("2.2.0", "2.3.0", false)]
    [InlineData("2.3.0", "2.2.0", true)]
    [InlineData("2.2.0-beta.6", "2.2.0", true)]   // the pre-release tag is ignored: 2.2.0 beta counts as 2.2.0
    [InlineData("2.2.0", "", true)]
    [InlineData("2.2.0", "banana", false)]       // an unreadable requirement is never "fine"
    [InlineData("2.2.0-rc.1", "2.2.0-beta.6", true)]
    [InlineData("2.2.0-rc.1", "2.2.1", false)]
    public void TheAppVersionCheck_ComparesNumbers_AndDistrustsGarbage(string current, string required, bool expected)
    {
        Assert.Equal(expected, CatalogService.MeetsAppVersion(required, Compressarr.Core.Updates.AppVersion.Parse(current)));
    }
}

public class CatalogClientTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("compressarr-catalog-client-").FullName;
    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public List<string> Urls { get; } = new();
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(_respond(request));
        }
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public Factory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private const string CatalogJson = """{ "schema": 1, "catalog": { "name": "Compressarr Presets" }, "profiles": [ { "family": "F", "version": 1, "engine": "handbrake", "file": "profiles/handbrake/f-v1.json", "sha256": "x" } ] }""";

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Official_FindsTheLatestReleaseTag_ThenReadsTheCatalogAndFilesAtThatTag()
    {
        var handler = new FakeHandler(r => r.RequestUri!.Host == "api.github.com" ? Json("""{ "tag_name": "catalog-2026.10.11" }""") : Json(r.RequestUri.AbsolutePath.EndsWith("catalog.json") ? CatalogJson : "profile bytes"));
        var client = new CatalogClient(new Factory(handler), sourceOverride: null);

        var listing = await client.LoadAsync(default);
        var bytes = await client.DownloadAsync(listing, listing.Document.Profiles[0], default);

        Assert.Equal("catalog-2026.10.11", listing.Release);
        Assert.Equal("Compressarr Presets", listing.Source.Name);
        Assert.Equal(CatalogSourceKind.Official, listing.Source.Kind);
        Assert.Equal("profile bytes", Encoding.UTF8.GetString(bytes));
        Assert.Contains("https://api.github.com/repos/Compressarr/Compressarr-Presets/releases/latest", handler.Urls);
        Assert.Contains("https://raw.githubusercontent.com/Compressarr/Compressarr-Presets/catalog-2026.10.11/catalog.json", handler.Urls);
        Assert.Contains("https://raw.githubusercontent.com/Compressarr/Compressarr-Presets/catalog-2026.10.11/profiles/handbrake/f-v1.json", handler.Urls);
    }

    [Fact]
    public async Task NoPublishedReleaseYet_IsSaidPlainly()
    {
        var client = new CatalogClient(new Factory(new FakeHandler(_ => Json("{}", HttpStatusCode.NotFound))), sourceOverride: null);

        var ex = await Assert.ThrowsAsync<CatalogException>(() => client.LoadAsync(default));

        Assert.Contains("no published release", ex.Message);
    }

    [Fact]
    public async Task Offline_IsSaidPlainly()
    {
        var client = new CatalogClient(new Factory(new FakeHandler(_ => throw new HttpRequestException("no route"))), sourceOverride: null);

        var ex = await Assert.ThrowsAsync<CatalogException>(() => client.LoadAsync(default));

        Assert.Contains("Couldn't reach the catalog", ex.Message);
        Assert.DoesNotContain("No such host", ex.Message);      // the raw .NET text is never shown
    }

    [Fact]
    public async Task ACatalogFormatThisVersionDoesNotKnow_AsksForAnUpdate_AndDoesNothingElse()
    {
        File.WriteAllText(Path.Combine(_folder, "catalog.json"), """{ "schema": 2, "profiles": [] }""");
        var client = new CatalogClient(new Factory(new FakeHandler(_ => Json(""))), _folder);

        var ex = await Assert.ThrowsAsync<CatalogException>(() => client.LoadAsync(default));

        Assert.Contains("newer Compressarr", ex.Message);
    }

    [Fact]
    public async Task ATestFolder_IsReadDirectly_AndCannotBeEscaped()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "profiles", "handbrake"));
        File.WriteAllText(Path.Combine(_folder, "catalog.json"), CatalogJson);
        File.WriteAllText(Path.Combine(_folder, "profiles", "handbrake", "f-v1.json"), "from disk");
        var client = new CatalogClient(new Factory(new FakeHandler(_ => Json(""))), _folder);

        var listing = await client.LoadAsync(default);
        var ok = await client.DownloadAsync(listing, listing.Document.Profiles[0], default);

        Assert.Equal("local", listing.Release);
        Assert.Equal("from disk", Encoding.UTF8.GetString(ok));
        foreach (var bad in new[] { "../outside.json", "/etc/passwd", @"C:\Windows\win.ini", "profiles/../../x.json" })
        {
            var entry = new CatalogEntry { Family = "F", Version = 1, Engine = "handbrake", File = bad };
            await Assert.ThrowsAsync<CatalogException>(() => client.DownloadAsync(listing, entry, default));
        }
    }

    [Fact]
    public async Task AnOversizedCatalog_IsNotUsed()
    {
        File.WriteAllBytes(Path.Combine(_folder, "catalog.json"), new byte[3 * 1024 * 1024]);
        var client = new CatalogClient(new Factory(new FakeHandler(_ => Json(""))), _folder);

        await Assert.ThrowsAsync<CatalogException>(() => client.LoadAsync(default));
    }
}
