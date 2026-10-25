using System.IO.Compression;
using System.Text.Json.Nodes;
using Compressarr.Core.Backup;
using Compressarr.Core.Config;
using Compressarr.Core.Presets;
using Compressarr.Core.Routing;
using Compressarr.Core.Tests.Presets;

namespace Compressarr.Core.Tests.Backup;

file sealed class PassThroughExpander : IPathExpander
{
    public string Expand(string value) => value;
    public bool PathExists(string value) => Directory.Exists(value) || File.Exists(value);
}

file sealed class NoTrash : ITrashService
{
    public void DeleteFile(string path, DeleteAfterConvertMode mode) { }
    public void DeleteFolder(string path, DeleteAfterConvertMode mode) { }
}

/// <summary>The user's own encoder profiles are part of "your settings": the automatic backup carries
/// handbrake-profiles.json and a restore puts it back - but never the generated handbrake-active.json,
/// which is derived and rebuilt before every encode.</summary>
public class BackupProfilesTests : AppDataTestBase
{
    private static JsonObject Profile(string name) => new()
    {
        ["PresetName"] = name,
        ["FileFormat"] = "av_mkv",
        ["Folder"] = false
    };

    private (BackupService Service, string BackupFolder) Setup()
    {
        var folder = Path.Combine(AppData, "Backups");
        var configStore = new JsonConfigStore();
        configStore.Update(AppPaths.GetConfigFilePath(), c =>
        {
            c.Backup.FolderPath = folder;
            c.Logging.LogFilePath = Path.Combine(AppData, "Logs");
            return true;
        });
        return (new BackupService(configStore, new PassThroughExpander(), new NoTrash(), new InMemoryAppState()), folder);
    }

    [Fact]
    public async Task Backup_CarriesYourProfiles_ButNotTheGeneratedFile()
    {
        var (service, folder) = Setup();
        var store = new HandBrakeProfileStore();
        store.AddUserProfiles(new[] { Profile("Cartoons x265") }); // also generates handbrake-active.json
        Assert.True(File.Exists(AppPaths.GetHandBrakeActivePresetsFilePath()));

        var result = await service.RunBackupAsync();

        Assert.True(result.Success, result.Error);
        using var zip = ZipFile.OpenRead(Path.Combine(folder, result.FileName!));
        var names = zip.Entries.Select(e => e.Name).ToList();
        Assert.Contains("handbrake-profiles.json", names);
        Assert.DoesNotContain("handbrake-active.json", names);
    }

    [Fact]
    public async Task Restore_PutsYourProfilesBack()
    {
        var (service, folder) = Setup();
        new HandBrakeProfileStore().AddUserProfiles(new[] { Profile("Cartoons x265") });
        var backup = await service.RunBackupAsync();

        File.Delete(AppPaths.GetHandBrakeProfilesFilePath()); // e.g. a new machine
        Assert.Null(new HandBrakeProfileStore().Find("Cartoons x265"));

        var restored = await service.RestoreBackupAsync(backup.FileName!, folder);

        Assert.True(restored.Success, restored.Error);
        var found = new HandBrakeProfileStore().Find("Cartoons x265");
        Assert.NotNull(found);
        Assert.False(found!.IsBuiltIn);
    }

    [Fact]
    public async Task Restore_OfABackupWithoutProfiles_LeavesYourCurrentOnesAlone()
    {
        var (service, folder) = Setup();
        var backup = await service.RunBackupAsync(); // taken before any profile existed (e.g. a 2.1.x backup)
        new HandBrakeProfileStore().AddUserProfiles(new[] { Profile("Added later") });

        var restored = await service.RestoreBackupAsync(backup.FileName!, folder);

        Assert.True(restored.Success, restored.Error);
        Assert.NotNull(new HandBrakeProfileStore().Find("Added later"));
    }
}
