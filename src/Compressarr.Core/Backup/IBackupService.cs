using System.IO.Compression;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Logging;
using Compressarr.Core.Orchestration;
using Compressarr.Core.Routing;

namespace Compressarr.Core.Backup;

public sealed record BackupResult(bool Success, string? FileName, string? Error);

public sealed record BackupFileInfo(string FileName, long SizeBytes, DateTimeOffset CreatedUtc);

public interface IBackupService
{
    /// <summary>Bundles settings/lanes, the run counter, resume state, the history CSV and the user's own encoder profiles into a
    /// timestamped zip in the configured backup folder, then prunes bundles older than retention.
    /// Reloads config itself, so it always acts on whatever was most recently saved rather than a
    /// caller's possibly-stale copy.</summary>
    Task<BackupResult> RunBackupAsync();

    /// <summary>Lists the zip files in folderOverride (or the saved config's Backup.FolderPath if
    /// null/blank), newest first. Empty (not an error) if the folder doesn't exist yet - e.g.
    /// before the very first backup has run. Accepting an override (rather than only ever reading
    /// the saved config) is what lets Settings show/restore from a folder the user has typed or
    /// browsed to but not saved yet - including on a first launch/new machine, before any
    /// settings have ever been saved at all.</summary>
    IReadOnlyList<BackupFileInfo> ListBackups(string? folderOverride = null);

    /// <summary>Restores settings/lanes (validated + merged-over-defaults, same as
    /// /api/settings/import), the run counter, resume state, and the history CSV from a backup
    /// zip previously created by RunBackupAsync. fileName is resolved against folderOverride (or
    /// the saved config's folder if null/blank) - Path.GetFileName strips any path components
    /// first, so this can't escape that folder. Overwrites whichever of those files the backup
    /// actually contains - anything the backup doesn't include (e.g. no resume.json because none
    /// existed at backup time) is left untouched rather than deleted.</summary>
    Task<BackupResult> RestoreBackupAsync(string fileName, string? folderOverride = null);

    /// <summary>The full path of a backup zip that can be handed to the user as a download, or null if there is no such
    /// backup. Only a file named like the ones Compressarr writes (Compressarr_Backup_*.zip) sitting directly in the
    /// resolved backup folder qualifies: Path.GetFileName strips any folder the caller sent, and the name pattern
    /// keeps this from serving other zip files that happen to share the folder.</summary>
    string? GetBackupPath(string fileName, string? folderOverride = null);
}

public sealed class BackupService : IBackupService
{
    private readonly IConfigStore _configStore;
    private readonly IPathExpander _pathExpander;
    private readonly ITrashService _trash;

    public BackupService(IConfigStore configStore, IPathExpander pathExpander, ITrashService trash)
    {
        _configStore = configStore;
        _pathExpander = pathExpander;
        _trash = trash;
    }

    private string ResolveFolder(string? folderOverride, CompressarrConfig config) =>
        _pathExpander.Expand(string.IsNullOrWhiteSpace(folderOverride) ? config.Backup.FolderPath : folderOverride);

    public Task<BackupResult> RunBackupAsync()
    {
        try
        {
            var config = _configStore.Load(AppPaths.GetConfigFilePath());
            var folder = _pathExpander.Expand(config.Backup.FolderPath);
            Directory.CreateDirectory(folder);

            var fileName = $"Compressarr_Backup_{DateTime.Now:yyyy-MM-dd_HHmmss}.zip";
            var zipPath = Path.Combine(folder, fileName);

            // Settings/lanes (one file - Lanes is a property inside compressarr.settings.json, not
            // a separate file), the run counter, resume/run state, the history CSV, and the user's own
            // encoder profiles (HandBrake and ffmpeg, and the ones added from a catalog). Deliberately excludes the generated handbrake-active.json (derived from
            // the profiles, rebuilt before every encode) and the Reports folder (regenerable HTML dumps,
            // already retention-pruned).
            // A 2.1.x install's history is still in the Logs folder until 2.2 first starts; pick it up from there too so a
            // backup taken before that point doesn't silently leave it out.
            // The queue and resume state (3.x) are a SQLite file too, copied the same way; until the first start has imported compressarr.resume.json, that file is what there is.
            // From 3.0 the history (runs, files, totals, run counter) is one SQLite file, copied with SQLite's own backup call so the copy is
            // consistent while the app runs. Until the first 3.0 start has imported it, the old CSV is what there is.
            var snapshotFolder = Path.Combine(Path.GetTempPath(), $"compressarr-backup-{Guid.NewGuid():N}");
            Directory.CreateDirectory(snapshotFolder);
            try
            {
                var stateSnapshot = Path.Combine(snapshotFolder, StateDatabase.FileName);
                var queueFile = StateDatabase.Snapshot(AppPaths.GetStateDbPath(), stateSnapshot) ? stateSnapshot : AppPaths.GetResumeFilePath();
                string? historyFile = null;
                var snapshot = Path.Combine(snapshotFolder, Path.GetFileName(AppPaths.GetHistoryDbPath()));
                if (HistoryDatabase.Snapshot(AppPaths.GetHistoryDbPath(), snapshot)) historyFile = snapshot;
                else
                {
                    historyFile = AppPaths.GetHistoryFilePath();
                    var legacyHistoryFile = Path.Combine(_pathExpander.Expand(config.Logging.LogFilePath), "Compressarr_History.csv");
                    if (!File.Exists(historyFile) && File.Exists(legacyHistoryFile)) historyFile = legacyHistoryFile;
                }

                var sources = new[]
                {
                    AppPaths.GetConfigFilePath(),
                    AppPaths.GetRunCountFilePath(),
                    queueFile,
                    AppPaths.GetHandBrakeProfilesFilePath(),
                    AppPaths.GetFFmpegProfilesFilePath(),
                    AppPaths.GetCatalogHandBrakeFilePath(),
                    AppPaths.GetCatalogFFmpegFilePath(),
                    AppPaths.GetCatalogRecordsFilePath(),
                    historyFile
                };

                CreateBundleZip(zipPath, sources);
            }
            finally
            {
                try { Directory.Delete(snapshotFolder, recursive: true); } catch { }
            }

            RetentionCleaner.CleanUp(_trash, folder, new[] { ".zip" }, config.Backup.RetentionDays, "backup");

            _configStore.Update(AppPaths.GetConfigFilePath(), c =>
            {
                c.Backup.LastRunUtc = DateTimeOffset.UtcNow;
                return true;
            });

            return Task.FromResult(new BackupResult(true, fileName, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new BackupResult(false, null, ex.Message));
        }
    }

    public IReadOnlyList<BackupFileInfo> ListBackups(string? folderOverride = null)
    {
        var config = _configStore.Load(AppPaths.GetConfigFilePath());
        var folder = ResolveFolder(folderOverride, config);
        if (!Directory.Exists(folder)) return Array.Empty<BackupFileInfo>();

        return Directory.EnumerateFiles(folder, "*.zip")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => new BackupFileInfo(f.Name, f.Length, f.CreationTimeUtc))
            .ToList();
    }

    public string? GetBackupPath(string fileName, string? folderOverride = null)
    {
        var safeName = Path.GetFileName(fileName ?? "");
        if (!safeName.StartsWith("Compressarr_Backup_", StringComparison.OrdinalIgnoreCase)
            || !safeName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return null;

        var folder = ResolveFolder(folderOverride, _configStore.Load(AppPaths.GetConfigFilePath()));
        var path = Path.Combine(folder, safeName);
        return File.Exists(path) ? path : null;
    }

    public async Task<BackupResult> RestoreBackupAsync(string fileName, string? folderOverride = null)
    {
        try
        {
            var config = _configStore.Load(AppPaths.GetConfigFilePath());
            var folder = ResolveFolder(folderOverride, config);

            // Strips any directory component the caller might have sent, so this can only ever
            // read a file that's directly inside the resolved backup folder - never an arbitrary
            // path elsewhere on disk.
            var safeName = Path.GetFileName(fileName);
            var zipPath = Path.Combine(folder, safeName);

            if (!File.Exists(zipPath))
            {
                return new BackupResult(false, null, "Backup file not found.");
            }

            var tempDir = Path.Combine(Path.GetTempPath(), $"compressarr-restore-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                try { ZipFile.ExtractToDirectory(zipPath, tempDir, overwriteFiles: true); }
                catch (InvalidDataException) { return new BackupResult(false, null, "That backup file is damaged or incomplete, so nothing was restored."); }

                // The history database is checked (and brought up to this build's schema) before anything is written, so a damaged one
                // leaves the current data exactly as it was.
                var extractedDatabase = Path.Combine(tempDir, Path.GetFileName(AppPaths.GetHistoryDbPath()));
                if (File.Exists(extractedDatabase))
                {
                    try { HistoryDatabase.ValidateAndMigrate(extractedDatabase); }
                    catch (InvalidDataException) { return new BackupResult(false, null, "The history inside that backup is damaged, so nothing was restored."); }
                    catch (InvalidOperationException e) { return new BackupResult(false, null, $"The history inside that backup cannot be used by this version ({e.Message}), so nothing was restored."); }
                }

                var extractedState = Path.Combine(tempDir, StateDatabase.FileName);
                if (File.Exists(extractedState))
                {
                    try { StateDatabase.ValidateAndMigrate(extractedState); }
                    catch (InvalidDataException) { return new BackupResult(false, null, "The queue inside that backup is damaged, so nothing was restored."); }
                    catch (InvalidOperationException e) { return new BackupResult(false, null, $"The queue inside that backup cannot be used by this version ({e.Message}), so nothing was restored."); }
                }

                // Settings/lanes go through the same validate + merge-over-defaults path
                // /api/settings/import already uses (load the extracted file through IConfigStore,
                // then Save it as the real config), rather than a raw file copy - an
                // older/partial backup still restores cleanly.
                CompressarrConfig? restoredConfig = null;
                var extractedSettings = Path.Combine(tempDir, "compressarr.settings.json");
                if (File.Exists(extractedSettings))
                {
                    try
                    {
                        if (!ConfigFileShape.LooksLikeAConfig(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(extractedSettings)))) throw new InvalidDataException("no settings");
                        restoredConfig = _configStore.Load(extractedSettings);
                    }
                    catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or InvalidOperationException)
                    {
                        return new BackupResult(false, null, "The settings inside that backup are damaged or empty, so nothing was restored.");
                    }
                    _configStore.Save(restoredConfig, AppPaths.GetConfigFilePath());
                }

                // The queue: a backup that carries the database replaces the current one. An older backup carries compressarr.resume.json instead: the
                // current database is removed so the next use imports that file, which makes the queue what the backup had.
                if (File.Exists(extractedState))
                {
                    RestoreEntry(tempDir, StateDatabase.FileName, AppPaths.GetStateDbPath());
                }
                else if (File.Exists(Path.Combine(tempDir, "compressarr.resume.json")))
                {
                    try { File.Delete(AppPaths.GetStateDbPath()); } catch { /* in use: the restored file is then picked up on the next start */ }
                    RestoreEntry(tempDir, "compressarr.resume.json", AppPaths.GetResumeFilePath());
                }
                RestoreEntry(tempDir, "handbrake-profiles.json", AppPaths.GetHandBrakeProfilesFilePath());
                RestoreEntry(tempDir, "ffmpeg-profiles.json", AppPaths.GetFFmpegProfilesFilePath());
                // The profiles added from a catalog, with their records (an older backup has none, and then nothing is touched).
                RestoreEntry(tempDir, "catalog-handbrake.json", AppPaths.GetCatalogHandBrakeFilePath());
                RestoreEntry(tempDir, "catalog-ffmpeg.json", AppPaths.GetCatalogFFmpegFilePath());
                RestoreEntry(tempDir, "catalog-installed.json", AppPaths.GetCatalogRecordsFilePath());

                // The history goes back into the app data folder. A 3.0 backup carries the database (counter included) and replaces the
                // current one. An older backup carries the CSV (also a 2.1.x one, where it was the Logs folder's file - the entry name is the
                // same) and the run counter file: the current database is removed so the next use imports those, which makes the history
                // what the backup had.
                if (File.Exists(extractedDatabase))
                {
                    RestoreEntry(tempDir, Path.GetFileName(extractedDatabase), AppPaths.GetHistoryDbPath());
                }
                else if (File.Exists(Path.Combine(tempDir, "Compressarr_History.csv")))
                {
                    try { File.Delete(AppPaths.GetHistoryDbPath()); } catch { /* in use: the restored CSV is then picked up on the next start */ }
                    RestoreEntry(tempDir, "compressarr.runcount.json", AppPaths.GetRunCountFilePath());
                    RestoreEntry(tempDir, "Compressarr_History.csv", AppPaths.GetHistoryFilePath());
                }
                else
                {
                    RestoreEntry(tempDir, "compressarr.runcount.json", AppPaths.GetRunCountFilePath());
                }

                return new BackupResult(true, safeName, null);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
        catch (Exception ex)
        {
            return new BackupResult(false, null, ex.Message);
        }
    }

    /// <summary>Writes whichever of sourceFiles actually exist into a new zip at zipPath, one entry
    /// per file (missing ones are silently skipped - e.g. a fresh install with no resume.json yet).
    /// Extracted as its own internal static method, same pattern as
    /// HandBrakeProcessRunner.DetermineSuccess, so it's testable against real temp files without
    /// needing to fake the AppData-rooted paths RunBackupAsync resolves its sources from.</summary>
    internal static void CreateBundleZip(string zipPath, IEnumerable<string> sourceFiles)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var source in sourceFiles)
        {
            if (File.Exists(source))
            {
                zip.CreateEntryFromFile(source, Path.GetFileName(source));
            }
        }
    }

    /// <summary>Copies entryName from inside an already-extracted backup (tempDir) to destPath, if
    /// that entry was actually present in the backup - a no-op otherwise (restoring an older
    /// backup that predates a given file shouldn't delete whatever's there now). Extracted as its
    /// own internal static method, same reasoning as CreateBundleZip - testable against real temp
    /// paths without needing to fake the AppData-rooted destinations RestoreBackupAsync resolves
    /// for the run counter/resume file.</summary>
    internal static void RestoreEntry(string tempDir, string entryName, string destPath)
    {
        var source = Path.Combine(tempDir, entryName);
        if (!File.Exists(source)) return;

        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
        File.Copy(source, destPath, overwrite: true);
    }
}
