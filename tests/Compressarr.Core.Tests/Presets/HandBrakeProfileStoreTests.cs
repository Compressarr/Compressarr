using System.Text.Json.Nodes;
using Compressarr.Core.Config;
using Compressarr.Core.Presets;

namespace Compressarr.Core.Tests.Presets;

public class HandBrakeProfileStoreTests : AppDataTestBase
{
    private static JsonObject Profile(string name, string format = "av_mp4") => new()
    {
        ["PresetName"] = name,
        ["FileFormat"] = format,
        ["Folder"] = false,
        ["Type"] = 1,
        ["VideoEncoder"] = "x265_10bit",
        ["VideoQualitySlider"] = 26
    };

    private static string ActivePath => AppPaths.GetHandBrakeActivePresetsFilePath();
    private static string UserPath => AppPaths.GetHandBrakeProfilesFilePath();

    [Fact]
    public void TheProfileANewLaneStartsWith_IsABuiltInThatAlwaysExists()
    {
        var profile = new HandBrakeProfileStore().Find(LaneConfig.DefaultProfileName);

        Assert.NotNull(profile);
        Assert.True(profile!.IsBuiltIn);
    }

    [Fact]
    public void GetAll_FreshInstall_IsJustTheLockedBuiltIns()
    {
        var all = new HandBrakeProfileStore().GetAll();

        Assert.Equal(new[] { "Compressarr SD-HD", "Compressarr UHD AV1" }, all.Select(p => p.Name).ToArray());
        Assert.All(all, p => Assert.True(p.IsBuiltIn));
        Assert.All(all, p => Assert.Equal("av_mkv", p.FileFormat));
    }

    [Fact]
    public void EnsureActiveFile_FreshInstall_WritesAValidFileWithOnlyTheBuiltInFolder()
    {
        var path = new HandBrakeProfileStore().EnsureActiveFile();

        Assert.Equal(ActivePath, path);
        Assert.True(HandBrakePresetFile.TryRead(path, out var root, out _));
        var folders = root!["PresetList"]!.AsArray();
        Assert.Single(folders);
        Assert.Equal("Compressarr (built-in)", folders[0]!["PresetName"]!.GetValue<string>());
        Assert.True(folders[0]!["Folder"]!.GetValue<bool>());
        Assert.Equal(2, HandBrakePresetFile.GetLeaves(root).Count);
        Assert.NotNull(root["VersionMajor"]); // the preset-format version travels with the file
    }

    [Fact]
    public void AddUserProfiles_AreListedPersistedAndAppearInTheActiveFile()
    {
        var store = new HandBrakeProfileStore();

        store.AddUserProfiles(new[] { Profile("Cartoons x265") });

        var mine = store.Find("Cartoons x265");
        Assert.NotNull(mine);
        Assert.False(mine!.IsBuiltIn);
        Assert.Equal("av_mp4", mine.FileFormat);

        // survives a new instance (i.e. a restart): it is on disk in the user file
        Assert.Contains(new HandBrakeProfileStore().GetAll(), p => p.Name == "Cartoons x265" && !p.IsBuiltIn);
        Assert.True(File.Exists(UserPath));

        // and HandBrake will be handed it: the active file has a "Yours" folder holding it
        Assert.True(HandBrakePresetFile.TryRead(ActivePath, out var root, out _));
        var leaves = HandBrakePresetFile.GetLeaves(root);
        Assert.Equal("Yours", leaves.Single(l => l.Name == "Cartoons x265").Group);
        Assert.Equal("Compressarr (built-in)", leaves.Single(l => l.Name == "Compressarr SD-HD").Group);
    }

    [Fact]
    public void AddUserProfiles_KeepsEverythingTheDefinitionStores()
    {
        var store = new HandBrakeProfileStore();
        var definition = Profile("Rich");
        definition["SomethingTheEditorDoesNotKnow"] = new JsonObject { ["nested"] = 42 };

        store.AddUserProfiles(new[] { definition });

        var stored = new HandBrakeProfileStore().Find("Rich")!.Definition;
        Assert.Equal(42, stored["SomethingTheEditorDoesNotKnow"]!["nested"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("Compressarr SD-HD")]
    [InlineData("compressarr sd-hd")]
    public void AddUserProfiles_NameClashWithABuiltIn_ThrowsAndWritesNothing(string name)
    {
        var store = new HandBrakeProfileStore();

        Assert.Throws<InvalidOperationException>(() => store.AddUserProfiles(new[] { Profile(name) }));

        Assert.False(File.Exists(UserPath));
    }

    [Fact]
    public void AddUserProfiles_NameClashWithAnExistingUserProfile_ThrowsAndLeavesTheFileAlone()
    {
        var store = new HandBrakeProfileStore();
        store.AddUserProfiles(new[] { Profile("Mine") });
        var before = File.ReadAllBytes(UserPath);

        Assert.Throws<InvalidOperationException>(() => store.AddUserProfiles(new[] { Profile("Other"), Profile("MINE") }));

        Assert.Equal(before, File.ReadAllBytes(UserPath)); // "Other" was not half-added
    }

    [Fact]
    public void AddUserProfiles_TheSameNameTwiceInOneBatch_Throws()
    {
        var store = new HandBrakeProfileStore();

        Assert.Throws<InvalidOperationException>(() => store.AddUserProfiles(new[] { Profile("Twice"), Profile("twice") }));

        Assert.False(File.Exists(UserPath));
    }

    [Fact]
    public void AddUserProfiles_BlankName_Throws()
    {
        var store = new HandBrakeProfileStore();

        Assert.Throws<InvalidOperationException>(() => store.AddUserProfiles(new[] { Profile("  ") }));
    }

    [Fact]
    public void EnsureActiveFile_WhenAlreadyUpToDate_DoesNotTouchTheFile()
    {
        var store = new HandBrakeProfileStore();
        store.AddUserProfiles(new[] { Profile("Mine") });
        var old = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(ActivePath, old);

        store.EnsureActiveFile();
        new HandBrakeProfileStore().EnsureActiveFile(); // a fresh start finds it current too

        Assert.Equal(old, File.GetLastWriteTimeUtc(ActivePath));
    }

    [Fact]
    public void EnsureActiveFile_MissingOrTamperedFile_IsRebuiltToTheSameBytes()
    {
        var store = new HandBrakeProfileStore();
        store.AddUserProfiles(new[] { Profile("Mine") });
        var expected = File.ReadAllBytes(ActivePath);

        File.WriteAllText(ActivePath, "garbage");
        store.EnsureActiveFile();
        Assert.Equal(expected, File.ReadAllBytes(ActivePath));

        File.Delete(ActivePath);
        store.EnsureActiveFile();
        Assert.Equal(expected, File.ReadAllBytes(ActivePath));
    }

    [Fact]
    public void EnsureActiveFile_SameProfiles_AreByteIdenticalAcrossInstances()
    {
        // The compare-then-write check only means something if generation is deterministic.
        var first = new HandBrakeProfileStore();
        first.AddUserProfiles(new[] { Profile("A"), Profile("B") });
        var bytes = File.ReadAllBytes(ActivePath);

        File.Delete(ActivePath);
        new HandBrakeProfileStore().EnsureActiveFile();

        Assert.Equal(bytes, File.ReadAllBytes(ActivePath));
    }

    [Fact]
    public void EnsureActiveFile_ChangedProfiles_RewriteIt_AndLeaveNoTempFileBehind()
    {
        var store = new HandBrakeProfileStore();
        store.EnsureActiveFile();
        var before = File.ReadAllBytes(ActivePath);

        store.AddUserProfiles(new[] { Profile("Mine") });

        Assert.NotEqual(before, File.ReadAllBytes(ActivePath));
        Assert.Empty(Directory.GetFiles(AppPaths.GetProfilesDirectory(), "*.tmp"));
    }

    [Fact]
    public void GetAll_PicksUpAnEditToTheUserFileMadeBehindItsBack()
    {
        var store = new HandBrakeProfileStore();
        store.AddUserProfiles(new[] { Profile("One") });
        Assert.NotNull(store.Find("One"));

        // e.g. a backup restore replacing the file while the app is running
        var other = new HandBrakeProfileStore();
        File.Delete(UserPath);
        other.AddUserProfiles(new[] { Profile("Two") });

        Assert.Null(store.Find("One"));
        Assert.NotNull(store.Find("Two"));
    }

    [Fact]
    public void UnreadableUserFile_OffersTheBuiltInsOnly_ReportsWhy_AndIsNeverOverwritten()
    {
        Directory.CreateDirectory(AppPaths.GetProfilesDirectory());
        File.WriteAllText(UserPath, "{ this is not json");
        var store = new HandBrakeProfileStore();

        Assert.Equal(2, store.GetAll().Count);
        Assert.False(string.IsNullOrWhiteSpace(store.UserFileError));
        Assert.Throws<InvalidOperationException>(() => store.AddUserProfiles(new[] { Profile("Mine") }));
        Assert.Equal("{ this is not json", File.ReadAllText(UserPath));
    }

    [Fact]
    public void UserFileError_IsNullWhenAllIsWell()
    {
        var store = new HandBrakeProfileStore();
        store.AddUserProfiles(new[] { Profile("Mine") });

        Assert.Null(store.UserFileError);
    }
}
