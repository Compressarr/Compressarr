using System.IO.Compression;
using Compressarr.Core.Backup;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Routing;
using Compressarr.Core.Tests.Presets;

namespace Compressarr.Core.Tests.Backup;

file sealed class PassThroughPathExpander : IPathExpander
{
    public string Expand(string value) => value;
    public bool PathExists(string value) => Directory.Exists(value) || File.Exists(value);
}

file sealed class FixedConfigStore : IConfigStore
{
    public CompressarrConfig Config { get; }
    public FixedConfigStore(CompressarrConfig config) => Config = config;

    public CompressarrConfig Load(string path) => Config;
    public void Save(CompressarrConfig config, string path) { }
    public T Update<T>(string path, Func<CompressarrConfig, T> mutate) => mutate(Config);
}

file sealed class NoTrash : ITrashService
{
    public void DeleteFile(string path, DeleteAfterConvertMode mode) { }
    public void DeleteFolder(string path, DeleteAfterConvertMode mode) { }
}

/// <summary>The queue (the state database) is part of every backup, copied consistently, and goes back over the current one on a restore; an older
/// backup that holds the JSON queue file is imported on the next use instead.</summary>
public class BackupQueueTests : AppDataTestBase
{
    private (BackupService Service, string BackupFolder) Build()
    {
        var backupFolder = Path.Combine(AppData, "Backups");
        var config = new CompressarrConfig();
        config.Backup.FolderPath = backupFolder;
        config.Logging.LogFilePath = Path.Combine(AppData, "Logs");
        return (new BackupService(new FixedConfigStore(config), new PassThroughPathExpander(), new NoTrash()), backupFolder);
    }

    private static string[] EntriesOf(string folder)
    {
        using var zip = ZipFile.OpenRead(Assert.Single(Directory.GetFiles(folder, "Compressarr_Backup_*.zip")));
        return zip.Entries.Select(e => e.Name).ToArray();
    }

    private static ResumeEntry Entry(string name, int order) => new() { LaneId = "lane1", FullName = @"C:\In\" + name, Order = order };

    [Fact]
    public async Task TheQueueDatabase_IsBackedUp_InPlaceOfTheJsonFile_AndRestoredOverTheCurrentQueue()
    {
        var (service, backupFolder) = Build();
        var store = new SqliteResumeStateStore();
        store.Save(new List<ResumeEntry> { Entry("a.mkv", 0), Entry("b.mkv", 1) }, AppPaths.GetResumeFilePath());

        var backup = await service.RunBackupAsync();
        store.Update(AppPaths.GetResumeFilePath(), state => { state.Clear(); state.Add(Entry("later.mkv", 0)); return true; });   // edits after the backup
        var restore = await service.RestoreBackupAsync(backup.FileName!, backupFolder);

        var entries = EntriesOf(backupFolder);
        Assert.Contains("compressarr.state.db", entries);
        Assert.DoesNotContain("compressarr.resume.json", entries);
        Assert.True(restore.Success, restore.Error);
        Assert.Equal(new[] { @"C:\In\a.mkv", @"C:\In\b.mkv" }, new SqliteResumeStateStore().Load(AppPaths.GetResumeFilePath()).Select(e => e.FullName));
    }

    [Fact]
    public async Task AnOlderBackupWithTheJsonQueue_ReplacesTheCurrentQueue_ByBeingImportedOnTheNextUse()
    {
        var (service, backupFolder) = Build();
        new JsonResumeStateStore().Save(new List<ResumeEntry> { Entry("from-backup.mkv", 0) }, AppPaths.GetResumeFilePath());
        var backup = await service.RunBackupAsync();          // no database yet: the JSON file goes into the zip
        Assert.Contains("compressarr.resume.json", EntriesOf(backupFolder));

        var store = new SqliteResumeStateStore();
        store.Load(AppPaths.GetResumeFilePath());              // first use imports it (and renames the file)
        store.Update(AppPaths.GetResumeFilePath(), state => { state.Add(Entry("added-after.mkv", 1)); return true; });
        var restore = await service.RestoreBackupAsync(backup.FileName!, backupFolder);

        Assert.True(restore.Success, restore.Error);
        Assert.Equal(new[] { @"C:\In\from-backup.mkv" }, new SqliteResumeStateStore().Load(AppPaths.GetResumeFilePath()).Select(e => e.FullName));
    }

    [Fact]
    public async Task ADamagedQueueInABackup_RefusesTheWholeRestore_AndTheCurrentQueueStays()
    {
        var (service, backupFolder) = Build();
        var store = new SqliteResumeStateStore();
        store.Save(new List<ResumeEntry> { Entry("current.mkv", 0) }, AppPaths.GetResumeFilePath());
        Directory.CreateDirectory(backupFolder);
        var zipPath = Path.Combine(backupFolder, "Compressarr_Backup_bad.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("compressarr.state.db");
            using var stream = entry.Open();
            stream.Write(Enumerable.Range(0, 2048).Select(i => (byte)(i * 17)).ToArray());
        }

        var restore = await service.RestoreBackupAsync("Compressarr_Backup_bad.zip", backupFolder);

        Assert.False(restore.Success);
        Assert.Contains("queue", restore.Error);
        Assert.Equal(@"C:\In\current.mkv", new SqliteResumeStateStore().Load(AppPaths.GetResumeFilePath()).Single().FullName);
    }
}
