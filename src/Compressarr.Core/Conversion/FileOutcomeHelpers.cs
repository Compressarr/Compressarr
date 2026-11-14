using Compressarr.Core.Config;
using Compressarr.Core.Routing;

namespace Compressarr.Core.Conversion;

/// <summary>Small pure helpers for reading what happened to a file - shared by the orchestrator and the step that
/// finishes an encoded file. Static and side-effect free, so each is testable without a real full disk or an
/// offline network drive.</summary>
internal static class FileOutcomeHelpers
{
    /// <summary>Matches the specific wording HandBrakeCLI/libav (Linux/macOS-style "No space left
    /// on device", from an ENOSPC-based error) and .NET's own IOException (Windows' "There is not
    /// enough space on the disk", from ERROR_DISK_FULL) use for a genuinely full volume - a
    /// deliberately narrow match, not a general "did this fail" check, so a run only stops itself
    /// for the one failure mode where retrying on the next poll is actively pointless. A pure
    /// static function so it's testable without a real full disk.</summary>
    internal static bool LooksLikeDiskFull(string? text) =>
        text is not null && (
            text.Contains("No space left on device", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("There is not enough space on the disk", StringComparison.OrdinalIgnoreCase));

    /// <summary>True only for the specific exception shapes a missing/unreachable base path
    /// actually produces - our own "not configured" check, .NET's own "no such path" exception, or
    /// an IOException whose message names a network path/share as the problem. Deliberately narrow:
    /// a permission error or a locked file also fails the move, but blaming "path unavailable" for
    /// those would be actively wrong, not just unhelpfully vague - those fall through to the
    /// generic "ERROR" instead. A pure static function so it's testable without a real offline
    /// network drive.</summary>
    internal static bool LooksLikePathUnavailable(Exception ex) =>
        ex is InvalidOperationException or DirectoryNotFoundException ||
        (ex is IOException && (
            ex.Message.Contains("network path", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("network name", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("network location", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("cannot find the path", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("is not accessible", StringComparison.OrdinalIgnoreCase)));

    /// <summary>How an output that is about to be written to <paramref name="destPath"/> must be resolved when that path is the source itself (output written back into
    /// the Input folder under the same name). Overwrite would replace the original in the same move, before any source policy is looked at: Maintain would lose the
    /// file it promises to keep, and Recycle would never see it. So with Maintain - or a source that is no longer the file that was converted - the output takes the
    /// next free name instead (<paramref name="disposeSourceFirst"/> stays false), and with Delete or Recycle the source is disposed of first by the caller, per
    /// the setting, and the move then replaces nothing. Any other case is returned as it came.</summary>
    internal static DestinationCollisionMode OutputCollisionMode(string destPath, string sourcePath, DestinationCollisionMode mode, DeleteAfterConvertMode keep, bool sourceUnchanged, out bool disposeSourceFirst)
    {
        disposeSourceFirst = false;
        if (mode != DestinationCollisionMode.Overwrite || !File.Exists(sourcePath)) return mode;
        if (!string.Equals(Path.GetFullPath(destPath), Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase)) return mode;

        if (keep == DeleteAfterConvertMode.Maintain || !sourceUnchanged) return DestinationCollisionMode.Rename;
        disposeSourceFirst = true;
        return mode;
    }

    /// <summary>The folder (beside the destination) a parked original is moved into: the original keeps its own file name inside it, so the recycle bin shows the right name.</summary>
    internal const string ParkedOriginalFolderPrefix = ".compressarr-original-";

    /// <summary>Publishes the staged output at <paramref name="destPath"/> when the original occupies that very path (a lane whose Input folder is its own library, or its Output), and disposes of the
    /// original the way the setting says - without ever leaving a moment where the original is unrecoverable and the output is not in place.
    /// The original is first PARKED: moved (a rename, on the same drive) into a uniquely named folder beside the destination, under its own file name. Only then is the output moved in; and only once it is in
    /// place is the parked original deleted or recycled (Delete / Recycle both). If moving the output fails, the original is put back (and a partial copy of the output removed): the staged file is untouched,
    /// so it is simply tried again later. <paramref name="parkedChanged"/> is told the exact parked path BEFORE anything is moved (the crash journal records it) and null once the original is resolved - put
    /// back, or disposed of. When it cannot be resolved (it could not be put back, or deleted afterwards) the notification is not sent, so the journal keeps the record and a restart finishes the job.
    /// Returns the parked path when the original could not be disposed of after publishing (a lock), else null.</summary>
    internal static string? PublishOverSource(ITrashService trash, string sourcePath, DeleteAfterConvertMode policy, string stagedPath, string destPath, Action<string?>? parkedChanged = null)
    {
        var parkedFolder = Path.Combine(Path.GetDirectoryName(destPath)!, ParkedOriginalFolderPrefix + Guid.NewGuid().ToString("N")[..16]);
        var parked = Path.Combine(parkedFolder, Path.GetFileName(destPath));

        parkedChanged?.Invoke(parked);              // on record before the original is touched
        try
        {
            Directory.CreateDirectory(parkedFolder);
            File.Move(sourcePath, parked);
        }
        catch
        {
            // Nothing was parked.
            TryRemoveEmptyFolder(parkedFolder);
            parkedChanged?.Invoke(null);
            throw;
        }

        try
        {
            File.Move(stagedPath, destPath, overwrite: true);
        }
        catch
        {
            // Back to how it was: the staged output is still there to be tried again, so a half-written copy of it at the destination goes and the original returns.
            try
            {
                if (File.Exists(stagedPath) && File.Exists(destPath)) File.Delete(destPath);
                if (!File.Exists(destPath))
                {
                    File.Move(parked, destPath);
                    TryRemoveEmptyFolder(parkedFolder);
                    parkedChanged?.Invoke(null);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* stays parked and on record: the next start puts it back */ }
            throw;
        }

        // The trash service reports a failed removal in its log and returns normally, so a normal return proves nothing: the record is cleared only when the parked file is verifiably gone.
        // A recycle that could not recycle may have deleted it for good (the trash service falls back to deleting) - but only now, with the output already in place.
        try
        {
            if (!DisposeSource(trash, parked, policy)) return parked;     // still there (locked, no permission): stays on record, the next start disposes of it once the output is confirmed
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return parked;
        }

        TryRemoveEmptyFolder(parkedFolder);
        parkedChanged?.Invoke(null);
        return null;
    }

    internal static void TryRemoveEmptyFolder(string folder)
    {
        try { if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover empty folder is harmless */ }
    }

    /// <summary>Removes a source file the way the setting says (Delete or Recycle), clearing a read-only flag first so it does not block the removal.
    /// Returns whether the file is actually gone afterwards. <see cref="ITrashService.DeleteFile"/> does not tell: it logs a removal that failed (a locked file, no permission) and returns
    /// normally, and with Maintain it does nothing - so the only proof of removal is that the path no longer holds the file. Callers act on this result, never on the call returning.</summary>
    internal static bool DisposeSource(ITrashService trash, string path, DeleteAfterConvertMode mode)
    {
        if (File.Exists(path))
        {
            var attrs = File.GetAttributes(path);
            if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }

        trash.DeleteFile(path, mode);
        return PresenceOf(path) == FilePresence.Absent;      // an inaccessible path is not a removed file
    }

    /// <summary>What can be said about a file's presence: it is there, it is verifiably not there, or that cannot be told (access denied, a share or drive that is not answering).</summary>
    internal enum FilePresence { Present, Absent, Unknown }

    /// <summary><see cref="File.Exists"/> answers false when the path cannot be checked (permissions, a share that dropped), so it cannot tell a file that is gone from one that is merely out of
    /// reach. This can: <see cref="FilePresence.Absent"/> only when the system says the file is not there AND the folder it should be in (or the nearest one above it that exists) can be listed and really lacks the next step down (so the lookup was not failing for lack of access or reach);
    /// anything else that goes wrong is <see cref="FilePresence.Unknown"/>. A recovery record is only ever dropped on Absent.</summary>
    internal static FilePresence PresenceOf(string path)
    {
        try
        {
            File.GetAttributes(path);
            return FilePresence.Present;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return IsGenuinelyMissing(path) ? FilePresence.Absent : FilePresence.Unknown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            return FilePresence.Unknown;
        }
    }

    /// <summary>The lookup said "not found": true only when that is genuinely so. Walking up to the first folder that can be listed, the step below it (the next folder on the way, or the file) must not be
    /// there either; one that IS there but cannot be followed (a junction or mount whose target is offline, a share that dropped) means the file is out of reach, not gone.</summary>
    private static bool IsGenuinelyMissing(string path)
    {
        var child = Path.GetFullPath(path);
        for (var dir = Path.GetDirectoryName(child); !string.IsNullOrEmpty(dir); child = dir, dir = Path.GetDirectoryName(dir))
        {
            try
            {
                using var entries = Directory.EnumerateFileSystemEntries(dir).GetEnumerator();
                entries.MoveNext();
            }
            catch (DirectoryNotFoundException) { continue; }   // this level cannot be reached either: look one higher
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException) { return false; }

            // `dir` can be listed. If the next step down is itself there, the path failed further down for another reason.
            try { File.GetAttributes(child); return false; }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException) { return false; }
        }

        return false;
    }

    /// <summary>Combines a new post-process warning onto any existing one for the same file - the
    /// companion-file move and the arr-unmonitor call are independent steps that can both fail for
    /// the same file, and neither should silently overwrite the other's message.</summary>
    internal static string AppendWarning(string? existing, string next) =>
        existing is null ? next : $"{existing}; {next}";
}
