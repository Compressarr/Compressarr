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

    /// <summary>Removes a source file the way the setting says (Delete or Recycle), clearing a read-only flag first so it does not block the removal.</summary>
    internal static void DisposeSource(ITrashService trash, string path, DeleteAfterConvertMode mode)
    {
        if (File.Exists(path))
        {
            var attrs = File.GetAttributes(path);
            if (attrs.HasFlag(FileAttributes.ReadOnly)) File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }

        trash.DeleteFile(path, mode);
    }

    /// <summary>Combines a new post-process warning onto any existing one for the same file - the
    /// companion-file move and the arr-unmonitor call are independent steps that can both fail for
    /// the same file, and neither should silently overwrite the other's message.</summary>
    internal static string AppendWarning(string? existing, string next) =>
        existing is null ? next : $"{existing}; {next}";
}
