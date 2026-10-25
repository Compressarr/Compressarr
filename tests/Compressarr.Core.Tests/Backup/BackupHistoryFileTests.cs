using System.IO.Compression;
using Compressarr.Core.Backup;
using Compressarr.Core.Config;
using Compressarr.Core.Logging;
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

/// <summary>The run history is part of every backup, wherever it currently lives: the app data folder (2.2), or -
/// until 2.2 has started once - the Logs folder where 2.1.x kept it.</summary>
public class BackupHistoryFileTests : AppDataTestBase
{
    private const string Rows = "yyyy,mm,dd,BegSize,EndSize\n2026,09,01,10,4\n";

    private (BackupService Service, string BackupFolder, string LogFolder) Build()
    {
        var backupFolder = Path.Combine(AppData, "Backups");
        var logFolder = Path.Combine(AppData, "Logs");
        var config = new CompressarrConfig();
        config.Backup.FolderPath = backupFolder;
        config.Logging.LogFilePath = logFolder;
        return (new BackupService(new FixedConfigStore(config), new PassThroughPathExpander(), new NoTrash()), backupFolder, logFolder);
    }

    private static string[] EntriesOf(string folder)
    {
        using var zip = ZipFile.OpenRead(Assert.Single(Directory.GetFiles(folder, "Compressarr_Backup_*.zip")));
        return zip.Entries.Select(e => e.Name).ToArray();
    }

    [Fact]
    public async Task TheHistoryInTheAppDataFolder_IsBackedUp_AndRestoredThere()
    {
        var (service, backupFolder, _) = Build();
        File.WriteAllText(AppPaths.GetHistoryFilePath(), Rows);

        var backup = await service.RunBackupAsync();
        File.Delete(AppPaths.GetHistoryFilePath());
        var restore = await service.RestoreBackupAsync(backup.FileName!, backupFolder);

        Assert.Contains("Compressarr_History.csv", EntriesOf(backupFolder));
        Assert.True(restore.Success);
        Assert.Equal(Rows, File.ReadAllText(AppPaths.GetHistoryFilePath()));
    }

    private static RunHistoryRecord SomeRun(int number, int files) => new(2026, 10, 24, 10, 4, files, 0, 5, 0, number, $"r{number}.html");

    [Fact]
    public async Task TheHistoryDatabase_IsBackedUpAsAConsistentCopy_InPlaceOfTheCsv_AndRestoredOverTheCurrentOne()
    {
        var (service, backupFolder, logFolder) = Build();
        var store = new SqliteRunHistoryStore(AppPaths.GetHistoryDbPath);
        store.AppendRun(logFolder, SomeRun(1, 3));
        store.IncrementRunCount(AppPaths.GetRunCountFilePath());

        var backup = await service.RunBackupAsync();
        store.AppendRun(logFolder, SomeRun(2, 5));       // history written after the backup
        store.IncrementRunCount(AppPaths.GetRunCountFilePath());
        var restore = await service.RestoreBackupAsync(backup.FileName!, backupFolder);

        var entries = EntriesOf(backupFolder);
        Assert.Contains("compressarr.history.db", entries);
        Assert.DoesNotContain("Compressarr_History.csv", entries);
        Assert.True(restore.Success, restore.Error);
        var restored = new SqliteRunHistoryStore(AppPaths.GetHistoryDbPath);
        Assert.Equal(3, Assert.Single(restored.GetHistory(logFolder)).FileCount);          // back to the backup's state
        Assert.Equal(1, restored.GetRunCount(AppPaths.GetRunCountFilePath()));             // the counter travels inside it
        Assert.Equal(3, HistoryTotalFiles());                                              // and so do the running totals
    }

    private static long HistoryTotalFiles()
    {
        using var connection = HistoryDatabase.Open(AppPaths.GetHistoryDbPath());
        return HistoryDatabase.Scalar(connection, "SELECT COALESCE(SUM(files), 0) FROM daily_totals");
    }

    [Fact]
    public async Task AnOlderBackupWithACsv_ReplacesTheCurrentHistory_ByBeingImportedOnTheNextUse()
    {
        var (service, backupFolder, logFolder) = Build();
        File.WriteAllText(AppPaths.GetHistoryFilePath(), "yyyy,mm,dd,BegSize,EndSize,FileCount,ProcessHours,ProcessMinutes,ProcessSeconds,RunNumber,ReportFileName,ErrorCount,WarningCount,RedirectCount\n2026,09,01,10,4,2,0,5,0,1,a.html,0,0,0\n");
        var backup = await service.RunBackupAsync();        // no database yet: the CSV goes into the zip
        var store = new SqliteRunHistoryStore(AppPaths.GetHistoryDbPath);
        store.GetHistory(logFolder);                         // first use imports it (and renames the CSV)
        store.AppendRun(logFolder, SomeRun(9, 7));

        var restore = await service.RestoreBackupAsync(backup.FileName!, backupFolder);

        Assert.True(restore.Success, restore.Error);
        Assert.Contains("Compressarr_History.csv", EntriesOf(backupFolder));
        var after = new SqliteRunHistoryStore(AppPaths.GetHistoryDbPath).GetHistory(logFolder);
        Assert.Equal(2, Assert.Single(after).FileCount);     // the backup's history, not the newer one
    }

    [Fact]
    public async Task ADamagedHistoryInsideABackup_StopsTheRestoreBeforeAnythingIsChanged()
    {
        var (service, backupFolder, logFolder) = Build();
        var store = new SqliteRunHistoryStore(AppPaths.GetHistoryDbPath);
        store.AppendRun(logFolder, SomeRun(1, 3));
        Directory.CreateDirectory(backupFolder);
        var zipPath = Path.Combine(backupFolder, "Compressarr_Backup_2026-10-24_000000.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("compressarr.history.db");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("this is not a database, only text that is long enough to look like a file to anything but sqlite");
        }

        var restore = await service.RestoreBackupAsync(Path.GetFileName(zipPath), backupFolder);

        Assert.False(restore.Success);
        Assert.Contains("history", restore.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, Assert.Single(new SqliteRunHistoryStore(AppPaths.GetHistoryDbPath).GetHistory(logFolder)).FileCount);   // untouched
    }

    [Fact]
    public async Task GetBackupPath_OnlyHandsOutBackupsInTheBackupFolder()
    {
        var (service, backupFolder, _) = Build();
        var backup = await service.RunBackupAsync();
        File.WriteAllText(Path.Combine(backupFolder, "other.zip"), "x");           // a zip that is not one of ours
        var outside = Path.Combine(AppData, "Compressarr_Backup_outside.zip");     // ours by name, but not in the folder
        File.WriteAllText(outside, "x");

        Assert.Equal(Path.Combine(backupFolder, backup.FileName!), service.GetBackupPath(backup.FileName!, backupFolder));
        Assert.Null(service.GetBackupPath("other.zip", backupFolder));
        Assert.Null(service.GetBackupPath(@"..\Compressarr_Backup_outside.zip", backupFolder));
        Assert.Null(service.GetBackupPath("Compressarr_Backup_missing.zip", backupFolder));
        Assert.Null(service.GetBackupPath("", backupFolder));
    }

    [Fact]
    public async Task AnUnmigrated21HistoryInTheLogsFolder_IsStillBackedUp()
    {
        var (service, backupFolder, logFolder) = Build();
        Directory.CreateDirectory(logFolder);
        File.WriteAllText(Path.Combine(logFolder, "Compressarr_History.csv"), Rows);

        await service.RunBackupAsync();

        Assert.Contains("Compressarr_History.csv", EntriesOf(backupFolder));
    }
}
