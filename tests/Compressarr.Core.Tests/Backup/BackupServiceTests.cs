using Compressarr.Core.Backup;
using Compressarr.Core.Config;
using Compressarr.Core.Routing;

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

file sealed record TrashCall(string Path, DeleteAfterConvertMode Mode);

file sealed class RecordingTrashService : ITrashService
{
    public List<TrashCall> Calls { get; } = new();
    public void DeleteFile(string path, DeleteAfterConvertMode mode) => Calls.Add(new TrashCall(path, mode));
    public void DeleteFolder(string path, DeleteAfterConvertMode mode) { }
}

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"compressarr-backup-tests-{Guid.NewGuid():N}");

    public BackupServiceTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() { try { Directory.Delete(_tempDir, recursive: true); } catch { } }

    [Fact]
    public void CreateBundleZip_IncludesOnlyExistingFiles()
    {
        var existingA = Path.Combine(_tempDir, "a.json");
        var existingB = Path.Combine(_tempDir, "b.csv");
        var missing = Path.Combine(_tempDir, "does-not-exist.json");
        File.WriteAllText(existingA, "{}");
        File.WriteAllText(existingB, "col1,col2");
        var zipPath = Path.Combine(_tempDir, "bundle.zip");

        BackupService.CreateBundleZip(zipPath, new[] { existingA, existingB, missing });

        using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
        var entryNames = zip.Entries.Select(e => e.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "a.json", "b.csv" }, entryNames);
    }

    private CompressarrConfig MakeConfig(string backupFolder, int retentionDays = 28)
    {
        var config = new CompressarrConfig();
        config.Backup.FolderPath = backupFolder;
        config.Backup.IntervalDays = 7;
        config.Backup.RetentionDays = retentionDays;
        config.Logging.LogFilePath = Path.Combine(_tempDir, "Logs"); // never created - history file just won't exist, and CreateBundleZip skips it
        return config;
    }

    [Fact]
    public async Task RunBackupAsync_WritesZipToConfiguredFolder_AndUpdatesLastRunUtc()
    {
        var backupFolder = Path.Combine(_tempDir, "Backups");
        var configStore = new FixedConfigStore(MakeConfig(backupFolder));
        var appState = new InMemoryAppState();
        var service = new BackupService(configStore, new PassThroughPathExpander(), new RecordingTrashService(), appState);

        var before = DateTimeOffset.UtcNow;
        var result = await service.RunBackupAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.FileName);
        var zipFiles = Directory.GetFiles(backupFolder, "Compressarr_Backup_*.zip");
        Assert.Single(zipFiles);
        Assert.Equal(Path.GetFileName(zipFiles[0]), result.FileName);
        Assert.NotNull(appState.BackupLastRunUtc);
        Assert.True(appState.BackupLastRunUtc >= before);
        Assert.Null(configStore.Config.Backup.LastRunUtc);   // the settings file is no longer where it is kept
    }

    [Fact]
    public async Task RunBackupAsync_PrunesBackupsOlderThanRetention()
    {
        var backupFolder = Path.Combine(_tempDir, "Backups");
        Directory.CreateDirectory(backupFolder);
        var oldZip = Path.Combine(backupFolder, "Compressarr_Backup_2020-01-01_000000.zip");
        File.WriteAllText(oldZip, "old");
        File.SetCreationTime(oldZip, DateTime.Now.AddDays(-100));

        var configStore = new FixedConfigStore(MakeConfig(backupFolder, retentionDays: 28));
        var trash = new RecordingTrashService();
        var service = new BackupService(configStore, new PassThroughPathExpander(), trash, new InMemoryAppState());

        await service.RunBackupAsync();

        Assert.Contains(trash.Calls, c => c.Path == oldZip && c.Mode == DeleteAfterConvertMode.Recycle);
    }

    [Fact]
    public void ListBackups_ReturnsZipsNewestFirst_UsingSavedFolder_WhenNoOverride()
    {
        var backupFolder = Path.Combine(_tempDir, "Backups");
        Directory.CreateDirectory(backupFolder);
        var older = Path.Combine(backupFolder, "Compressarr_Backup_2020-01-01_000000.zip");
        var newer = Path.Combine(backupFolder, "Compressarr_Backup_2026-01-01_000000.zip");
        File.WriteAllText(older, "old");
        File.WriteAllText(newer, "new");
        File.SetCreationTime(older, DateTime.Now.AddDays(-10));
        File.SetCreationTime(newer, DateTime.Now);

        var configStore = new FixedConfigStore(MakeConfig(backupFolder));
        var service = new BackupService(configStore, new PassThroughPathExpander(), new RecordingTrashService(), new InMemoryAppState());

        var backups = service.ListBackups();

        Assert.Equal(new[] { "Compressarr_Backup_2026-01-01_000000.zip", "Compressarr_Backup_2020-01-01_000000.zip" },
            backups.Select(b => b.FileName).ToArray());
    }

    [Fact]
    public void ListBackups_FolderOverride_WinsOverSavedConfig()
    {
        var savedFolder = Path.Combine(_tempDir, "SavedBackups");
        var overrideFolder = Path.Combine(_tempDir, "OverrideBackups");
        Directory.CreateDirectory(savedFolder);
        Directory.CreateDirectory(overrideFolder);
        File.WriteAllText(Path.Combine(savedFolder, "Compressarr_Backup_saved.zip"), "x");
        File.WriteAllText(Path.Combine(overrideFolder, "Compressarr_Backup_override.zip"), "x");

        var configStore = new FixedConfigStore(MakeConfig(savedFolder));
        var service = new BackupService(configStore, new PassThroughPathExpander(), new RecordingTrashService(), new InMemoryAppState());

        var backups = service.ListBackups(overrideFolder);

        Assert.Equal(new[] { "Compressarr_Backup_override.zip" }, backups.Select(b => b.FileName).ToArray());
    }

    [Fact]
    public void ListBackups_ReturnsEmpty_WhenFolderDoesNotExist()
    {
        var configStore = new FixedConfigStore(MakeConfig(Path.Combine(_tempDir, "does-not-exist")));
        var service = new BackupService(configStore, new PassThroughPathExpander(), new RecordingTrashService(), new InMemoryAppState());

        Assert.Empty(service.ListBackups());
    }

    [Fact]
    public void RestoreEntry_CopiesFile_WhenPresentInExtractedBackup()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, "extracted"));
        var extractedDir = Path.Combine(_tempDir, "extracted");
        File.WriteAllText(Path.Combine(extractedDir, "compressarr.runcount.json"), "42");
        var dest = Path.Combine(_tempDir, "restored", "compressarr.runcount.json");

        BackupService.RestoreEntry(extractedDir, "compressarr.runcount.json", dest);

        Assert.Equal("42", File.ReadAllText(dest));
    }

    [Fact]
    public void RestoreEntry_OverwritesExistingDestinationFile()
    {
        var extractedDir = Path.Combine(_tempDir, "extracted");
        Directory.CreateDirectory(extractedDir);
        File.WriteAllText(Path.Combine(extractedDir, "compressarr.resume.json"), "from-backup");
        var dest = Path.Combine(_tempDir, "compressarr.resume.json");
        File.WriteAllText(dest, "stale-current-value");

        BackupService.RestoreEntry(extractedDir, "compressarr.resume.json", dest);

        Assert.Equal("from-backup", File.ReadAllText(dest));
    }

    [Fact]
    public void RestoreEntry_DoesNothing_WhenEntryNotPresentInBackup()
    {
        var extractedDir = Path.Combine(_tempDir, "extracted");
        Directory.CreateDirectory(extractedDir);
        var dest = Path.Combine(_tempDir, "compressarr.resume.json");
        File.WriteAllText(dest, "left-untouched");

        BackupService.RestoreEntry(extractedDir, "compressarr.resume.json", dest);

        Assert.Equal("left-untouched", File.ReadAllText(dest));
    }
}
