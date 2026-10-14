using Compressarr.Core.Config;

namespace Compressarr.Core.Routing;

public interface ICompanionFileService
{
    /// <summary>After a file has been routed into its destination folder, moves whatever else in
    /// its source folder belongs to it - subtitles, .nfo, artwork, etc - matched by shared base
    /// name (e.g. "Show.eng.srt" alongside "Show.mp4"). Runs immediately, every time, regardless
    /// of whether other videos are still sitting in the same shared/flat folder - a sibling
    /// episode's own companions are never touched, only this file's own.
    ///
    /// Of the stem-matched candidates, only ones whose extension is in companionExtensions are
    /// treated as wanted and actually moved/copied alongside the video. A stem-matched file whose
    /// extension ISN'T in that list (an orphaned companion, or genuinely unrelated content a user
    /// placed there under a matching name) is disposed of per unmatchedCompanionAction - Maintain
    /// leaves it exactly where it is, Delete/Recycle removes it. deleteAfterConvert==Maintain
    /// overrides all of this and guarantees the source is never touched destructively at all,
    /// regardless of unmatchedCompanionAction - that's a stronger, absolute "hands off source"
    /// contract, same as it's always been for the video itself.
    ///
    /// Deliberately does NOT touch the source folder itself - see CleanUpEmptySourceFolder for
    /// that, called separately once it's safe to (after Sonarr/Radarr's own rescan, if any, has
    /// had a chance to see the folder still present but empty of video - see its own doc comment
    /// for why the two are split).
    ///
    /// Confirmed live: an earlier "wait until this is the only video left in the folder, then
    /// sweep everything at once" design meant a file's own companions didn't move until the WHOLE
    /// shared batch finished - or never, if a sibling was permanently skipped/removed from the
    /// queue - and even Stop Monitoring landing right after one file finished (before the next
    /// started) left that file's own companions stranded. Moving per-file, immediately, by name
    /// match rather than by "am I the last one" sidesteps all of that.
    ///
    /// routedVideoDestPath is the video's own ACTUAL final destination path, not just a folder -
    /// each companion's own destination filename is derived from THIS path's stem, not the
    /// companion's original one, so a Rename-collision that gave the video "Movie (2).mkv" moves
    /// "Movie.en.srt" to "Movie (2).en.srt" alongside it - video and companions always stay a
    /// matched set. collisionMode applies the SAME Overwrite/Skip/Rename policy the video itself
    /// already gets (v2.1.3 code review finding #5 - a companion collision used to always
    /// overwrite, independent of what was actually configured); a Skip on one companion only
    /// leaves that one file where it is, it does not abort handling the rest.</summary>
    void MoveCompanionFiles(
        string originalFileFullName,
        string originalFileDirectory,
        string routedVideoDestPath,
        DeleteAfterConvertMode deleteAfterConvert,
        IReadOnlyList<string> companionExtensions,
        DeleteAfterConvertMode unmatchedCompanionAction,
        DestinationCollisionMode collisionMode = DestinationCollisionMode.Overwrite);

    /// <summary>Checks whether originalFileDirectory is now empty of video (every video that was
    /// ever in it has since been routed/deleted) and, if so, disposes of whatever's left per
    /// unmatchedCompanionAction and removes the folder itself, cascading upward through now-empty
    /// ancestor folders - up to but not including inputRoot, which is never removed.
    ///
    /// Deliberately a SEPARATE call from MoveCompanionFiles, made only once it's actually safe to
    /// remove the folder - specifically, only after Sonarr/Radarr has been told to rescan (see
    /// IArrUnmonitorService.UnmonitorAsync) and given time to actually do it. Real behavior
    /// reported live: if the source folder is already GONE by the time Sonarr/Radarr rescans, it
    /// reads as a disconnected/unreachable root and does NOT clear the episode from its tracking;
    /// if the folder is still there but genuinely empty of video, Sonarr/Radarr correctly treats
    /// the episode as missing and clears it. So the folder must still physically exist (even if
    /// empty) at the moment the rescan runs - only once that's had time to finish is it actually
    /// safe to remove. Callers must call this AFTER the arr-unmonitor/rescan step, never before -
    /// see ConversionOrchestrator for the call ordering this depends on.</summary>
    void CleanUpEmptySourceFolder(
        string originalFileDirectory,
        string inputRoot,
        IReadOnlyList<string> vidTypes,
        DeleteAfterConvertMode deleteAfterConvert,
        DeleteAfterConvertMode unmatchedCompanionAction);
}

/// <summary>Ported from Move-CompressarrCompanionFiles; redesigned from a batch-at-the-end sweep
/// to an immediate per-file move (see ICompanionFileService's doc comment for why), then further
/// split so the "is the folder empty, safe to remove" step could move to run after Sonarr/Radarr's
/// own rescan (see CleanUpEmptySourceFolder's doc comment).</summary>
public sealed class CompanionFileService : ICompanionFileService
{
    private readonly ITrashService _trash;

    public CompanionFileService(ITrashService trash)
    {
        _trash = trash;
    }

    public void MoveCompanionFiles(
        string originalFileFullName,
        string originalFileDirectory,
        string routedVideoDestPath,
        DeleteAfterConvertMode deleteAfterConvert,
        IReadOnlyList<string> companionExtensions,
        DeleteAfterConvertMode unmatchedCompanionAction,
        DestinationCollisionMode collisionMode = DestinationCollisionMode.Overwrite)
    {
        if (!Directory.Exists(originalFileDirectory)) return;

        var destinationFolder = Path.GetDirectoryName(routedVideoDestPath)!;
        var videoDestStem = Path.GetFileNameWithoutExtension(routedVideoDestPath);

        var wantedExtensions = companionExtensions
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => "." + t.Trim().TrimStart('.'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Matched as "the video's own extension-stripped name, followed by a '.'" rather than a
        // plain prefix, so "S03E1"'s own stem can't accidentally also claim "S03E10 - Magic.eng.srt"
        // (both start with "S03E1", but only one is followed by ".").
        var stemPrefix = Path.GetFileNameWithoutExtension(originalFileFullName) + ".";
        var ownCompanions = Directory.EnumerateFiles(originalFileDirectory)
            .Where(f => !string.Equals(f, originalFileFullName, StringComparison.OrdinalIgnoreCase))
            .Where(f => Path.GetFileName(f).StartsWith(stemPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var sibling in ownCompanions)
        {
            var isWanted = wantedExtensions.Contains(Path.GetExtension(sibling));

            // The part of the companion's own name after its shared stem (e.g. "en.srt" out of
            // "Movie.en.srt") - re-attached to the video's own ACTUAL destination stem below, not
            // the companion's original one, so a Rename-collision on the video carries the
            // companion along under the exact same renamed stem.
            var companionSuffix = Path.GetFileName(sibling)[stemPrefix.Length..];
            var desiredCompanionName = videoDestStem + "." + companionSuffix;

            if (deleteAfterConvert == DeleteAfterConvertMode.Maintain)
            {
                // Maintain never touches the source, period - an unwanted stem-matched file is
                // left exactly where it is, regardless of unmatchedCompanionAction. Collision
                // policy still applies to the copy itself (Skip/Rename), since Maintain is only
                // about the SOURCE never being touched, not about blindly clobbering destination.
                if (isWanted)
                {
                    var desiredDestPath = Path.Combine(destinationFolder, desiredCompanionName);
                    try
                    {
                        var resolvedDestPath = FileRouter.ResolveCollision(desiredDestPath, collisionMode);
                        File.Copy(sibling, resolvedDestPath, overwrite: true);
                    }
                    catch (DestinationCollisionSkippedException)
                    {
                        // This one companion stays uncopied - every other companion still gets
                        // handled normally.
                    }
                }
                continue;
            }

            if (isWanted)
            {
                var desiredDestPath = Path.Combine(destinationFolder, desiredCompanionName);
                try
                {
                    var resolvedDestPath = FileRouter.ResolveCollision(desiredDestPath, collisionMode);
                    File.Move(sibling, resolvedDestPath, overwrite: true);
                }
                catch (DestinationCollisionSkippedException)
                {
                    // Configured to skip - this one companion just stays where it is; every other
                    // companion (and the folder-cleanup logic below) still gets handled normally.
                }
            }
            else if (unmatchedCompanionAction != DeleteAfterConvertMode.Maintain)
            {
                _trash.DeleteFile(sibling, unmatchedCompanionAction);
            }
            // else: unmatchedCompanionAction == Maintain means "leave in place" - do nothing.
        }
    }

    public void CleanUpEmptySourceFolder(
        string originalFileDirectory,
        string inputRoot,
        IReadOnlyList<string> vidTypes,
        DeleteAfterConvertMode deleteAfterConvert,
        DeleteAfterConvertMode unmatchedCompanionAction)
    {
        if (deleteAfterConvert == DeleteAfterConvertMode.Maintain) return;
        if (!Directory.Exists(originalFileDirectory)) return;

        // Never sweep or remove the lane's Input root itself - it's the lane's persistent watch
        // folder for the next run, and may still hold other unrelated content this function never
        // inspected.
        var inputRootFull = SafeFullPath(inputRoot);
        var originalDirFull = SafeFullPath(originalFileDirectory);
        if (inputRootFull is not null && string.Equals(originalDirFull, inputRootFull, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var extensions = vidTypes
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => "." + t.Trim().TrimStart('.'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A shared/flat folder still holding another video (still queued, skipped, whatever) isn't
        // really empty yet - leave it, and whatever's still sitting alongside that other video,
        // completely alone. Only once every video is gone does cleanup make sense.
        var stillHasVideo = Directory.EnumerateFiles(originalFileDirectory)
            .Any(f => extensions.Contains(Path.GetExtension(f)));
        if (stillHasVideo) return;

        // Whatever's left (an orphaned companion from an earlier call, or genuinely unrelated
        // content) is disposed of per unmatchedCompanionAction, same policy as an unwanted
        // stem-matched sibling in MoveCompanionFiles - Maintain leaves it in place, Delete/Recycle
        // removes it.
        if (unmatchedCompanionAction != DeleteAfterConvertMode.Maintain)
        {
            var remaining = Directory.EnumerateFileSystemEntries(originalFileDirectory).ToList();
            foreach (var item in remaining)
            {
                if (Directory.Exists(item))
                {
                    // Input is scanned recursively, so a subfolder can hold a video that is still queued (or skipped, or under the
                    // size filter) - that is not an "unmatched leftover", and sweeping it would lose the file before its own turn.
                    // Only a folder proven to hold no video anywhere below it is disposed of; any doubt keeps it, and the folder
                    // above it then stays too (it is not empty).
                    if (IsProvablyFreeOfVideo(item, extensions)) _trash.DeleteFolder(item, unmatchedCompanionAction);
                }
                else
                {
                    _trash.DeleteFile(item, unmatchedCompanionAction);
                }
            }
        }

        // Only remove the folder itself (and cascade upward) once it's genuinely empty - if
        // unmatchedCompanionAction left something behind on purpose, this folder isn't done yet.
        if (Directory.EnumerateFileSystemEntries(originalFileDirectory).Any()) return;

        _trash.DeleteFolder(originalFileDirectory, deleteAfterConvert);

        // Cascade upward: remove each parent folder in turn as long as it's now completely
        // empty, stopping the moment we reach a non-empty folder or the lane's Input root.
        if (inputRootFull is not null)
        {
            var current = Path.GetDirectoryName(originalFileDirectory);
            while (!string.IsNullOrEmpty(current) && Directory.Exists(current) &&
                   !string.Equals(current, inputRootFull, StringComparison.OrdinalIgnoreCase))
            {
                if (Directory.EnumerateFileSystemEntries(current).Any()) break;

                _trash.DeleteFolder(current, deleteAfterConvert);
                current = Path.GetDirectoryName(current);
            }
        }
    }

    /// <summary>True only when nothing at any depth below <paramref name="directory"/> is a video (by extension - the size filter and queue state
    /// are deliberately not consulted: a file that could ever be queued counts). A link (junction/symlink) cannot be vouched for, an unreadable
    /// folder cannot be inspected, so both answer false.</summary>
    private static bool IsProvablyFreeOfVideo(string directory, HashSet<string> videoExtensions)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", new EnumerationOptions { AttributesToSkip = 0 }))
            {
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (!IsProvablyFreeOfVideo(entry, videoExtensions)) return false;
                }
                else if (videoExtensions.Contains(Path.GetExtension(entry)))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? SafeFullPath(string path)
    {
        try { return Directory.Exists(path) ? Path.GetFullPath(path) : null; }
        catch { return null; }
    }
}
