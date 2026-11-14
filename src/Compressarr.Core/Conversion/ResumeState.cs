using Compressarr.Core.Config;
using System.Security.Cryptography;
using System.Text.Json;

namespace Compressarr.Core.Conversion;

public enum ResumeStatus
{
    Pending,
    Completed,
    Error,

    /// <summary>The encode itself succeeded, but routing the result into its TV/Movie base path
    /// failed (unreachable network drive, disk full, permission error, etc.) - the encoded file is
    /// sitting wherever HandBrake wrote it (EncodedFilePath), not lost, and ConversionOrchestrator
    /// retries just the move (no re-encode) at the start of this lane's next pass.</summary>
    MoveFailed,

    /// <summary>The video itself finished and was routed to its final destination successfully,
    /// but moving its companion files (subtitles, .nfo, artwork) alongside it failed - the
    /// companions are still sitting in PendingCompanionSourceDirectory, stranded. Unlike
    /// MoveFailed, the video is fully done; ConversionOrchestrator retries just the companion move
    /// (and, once that succeeds, the folder-emptiness cleanup that was skipped the first time) at
    /// the start of this lane's next pass. A real gap found via code review: without a status to
    /// track this, the entry was left Completed and nothing ever retried the stranded companions.</summary>
    CompanionMoveFailed,

    /// <summary>The video (and, if any, its companions) are both fully done and correctly filed -
    /// only the source folder's own cleanup remains, deferred because the Sonarr/Radarr rescan
    /// this entry's pass triggered was never positively confirmed complete (see
    /// ArrRescanOutcome.SafeToCleanUp - a timeout, a "failed" status, or a cancelled wait). Unlike
    /// MoveFailed/CompanionMoveFailed, nothing about the video or its companions needs redoing;
    /// ConversionOrchestrator retries just the *arr confirmation (and, once THAT succeeds, the
    /// folder cleanup that was skipped the first time) at the start of this lane's next pass. A
    /// real gap found via code review: without a status to track this, the entry was left
    /// Completed and nothing ever came back to finish the deferred cleanup, potentially leaving an
    /// empty source folder behind forever.</summary>
    CleanupPending,

    /// <summary>The crash journal (3.1): written while a finished encode is being filed, before the final status is known. If Compressarr stops in the middle (a crash, a power
    /// cut, a kill), this is what is found on the next start, and the retry logic picks the job up at the stage it reached instead of re-encoding it: before the video was
    /// routed (EncodedFilePath is set, PendingCompanionVideoDestPath is not) the encoded file is routed as for a failed move; after it was routed (PendingCompanionVideoDestPath
    /// is set) the source is dealt with, then the companions, the Sonarr/Radarr rescan and the folder cleanup. Always replaced by one of the other statuses when filing completes.
    /// Appended last so an older build, which does not know it, reads the entry as Pending.</summary>
    FinishPending
}

/// <summary>The rules for a source that was converted and deliberately left where it was (Settings > Processing > Maintain).</summary>
public static class ResumeKeeping
{
    private static readonly TimeSpan TimeTolerance = TimeSpan.FromSeconds(2); // file systems round last-write times differently

    /// <summary>Marks an entry Completed - the one place that does, so a source that is kept is always noted with it.</summary>
    public static void MarkCompleted(ResumeEntry entry, DeleteAfterConvertMode mode)
    {
        entry.Status = ResumeStatus.Completed;
        NoteKeptSource(entry, mode);
    }

    /// <summary>Notes the source's size and time on a Completed entry when the mode keeps the source. Does nothing otherwise.</summary>
    private static void NoteKeptSource(ResumeEntry entry, DeleteAfterConvertMode mode)
    {
        if (mode == DeleteAfterConvertMode.Maintain) NoteSourceKept(entry);
    }

    /// <summary>Notes the source as converted and left in place whatever the delete setting says, so the same file found again is not work to do. For a source that could not be shown to be
    /// the file that was converted (<see cref="SourceCheck.Unknown"/>): it is left for the user rather than deleted, and not converted a second time.</summary>
    public static void NoteSourceKept(ResumeEntry entry)
    {
        if (!File.Exists(entry.FullName)) return;
        var info = new FileInfo(entry.FullName);
        entry.KeptSourceSize = info.Length;
        entry.KeptSourceModifiedUtc = info.LastWriteTimeUtc;
    }

    /// <summary>The SHA-256 of a file's whole content, as hex; null when it cannot be read. Reads the file once, which for a finished encode takes about as long as copying it.</summary>
    internal static string? ContentDigest(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer, 0, read);
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Notes what the encoded output is (its size and the SHA-256 of its content) as the crash journal records it, just before the file is moved. Clears the note when the file cannot be read.</summary>
    public static void NoteOutputIdentity(ResumeEntry entry, string encodedFilePath)
    {
        try
        {
            var info = new FileInfo(encodedFilePath);
            var digest = info.Exists ? ContentDigest(encodedFilePath) : null;
            if (digest is null) { ClearOutputIdentity(entry); return; }
            entry.EncodedFileSize = info.Length;
            entry.EncodedFileDigest = digest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ClearOutputIdentity(entry);
        }
    }

    /// <summary>Forgets the journal's note of the output (and that it was being published in Output): filing is settled, or the move did not happen.</summary>
    public static void ClearOutputIdentity(ResumeEntry entry)
    {
        entry.EncodedFileSize = null;
        entry.EncodedFileDigest = null;
        entry.FiledInOutput = false;
    }

    /// <summary>Whether the file at <paramref name="path"/> is the output the journal recorded for this entry.</summary>
    public enum OutputCheck
    {
        /// <summary>It has the size and time the encoded output had.</summary>
        Matches,
        /// <summary>Nothing is there.</summary>
        Missing,
        /// <summary>A file is there and it is positively not the recorded one (another size, or other content that was read).</summary>
        Different,
        /// <summary>No record was made (an entry journalled by 3.1.0-rc.1), so nothing can be compared.</summary>
        Unrecorded,
        /// <summary>It could not be compared right now: the path cannot be checked, or the content cannot be read (a lock, access denied, a share that dropped). Not a mismatch and not "missing":
        /// nothing may be concluded from it, and no journal record may be cleared because of it - recovery leaves the entry as it is and looks again on the next pass.</summary>
        Indeterminate
    }

    /// <summary>Compares the file at <paramref name="path"/> with the output the journal recorded: the same size and the same SHA-256 of its whole content (a copy that is the same length, or that has
    /// the same times, is not enough). Only <see cref="OutputCheck.Matches"/> allows treating it as this job's filed output; every other answer means the original must be kept. Reads the file once.</summary>
    public static OutputCheck CheckOutput(ResumeEntry entry, string path)
    {
        try
        {
            // Only what was positively read decides Matches or Different. A path that cannot be checked, or content that cannot be read, is Indeterminate - never a mismatch.
            var presence = FileOutcomeHelpers.PresenceOf(path);
            if (presence == FileOutcomeHelpers.FilePresence.Absent) return OutputCheck.Missing;
            if (presence == FileOutcomeHelpers.FilePresence.Unknown) return OutputCheck.Indeterminate;
            var info = new FileInfo(path);
            if (entry.EncodedFileSize is not { } size || string.IsNullOrEmpty(entry.EncodedFileDigest)) return OutputCheck.Unrecorded;
            if (info.Length != size) return OutputCheck.Different;
            var digest = ContentDigest(path);
            if (digest is null) return OutputCheck.Indeterminate;
            return string.Equals(digest, entry.EncodedFileDigest, StringComparison.OrdinalIgnoreCase) ? OutputCheck.Matches : OutputCheck.Different;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return OutputCheck.Indeterminate;
        }
    }

    private const int FingerprintSample = 1 << 20;

    /// <summary>A short fingerprint of a file's content: its size and its first and last megabyte, hashed. Cheap (two megabytes read however large the file is) and enough to tell
    /// one video from another even when a copy tool has given them the same size and time. Null when the file cannot be read.</summary>
    internal static string? ContentFingerprint(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[FingerprintSample];

            void Append(long from)
            {
                stream.Seek(from, SeekOrigin.Begin);
                var wanted = (int)Math.Min(FingerprintSample, length - from);
                var read = 0;
                while (read < wanted)
                {
                    var n = stream.Read(buffer, read, wanted - read);
                    if (n == 0) break;
                    read += n;
                }

                hash.AppendData(buffer, 0, read);
            }

            Append(0);
            if (length > FingerprintSample) Append(Math.Max(FingerprintSample, length - FingerprintSample));
            hash.AppendData(BitConverter.GetBytes(length));
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Notes what the source is as its encode starts (size, last-write and creation time, a content fingerprint), so that deleting "the source" afterwards can first check it
    /// is still that file.</summary>
    public static void NoteEncodeStart(ResumeEntry entry)
    {
        if (!File.Exists(entry.FullName)) return;
        var info = new FileInfo(entry.FullName);
        entry.EncodedFromSize = info.Length;
        entry.EncodedFromModifiedUtc = info.LastWriteTimeUtc;
        entry.EncodedFromCreatedUtc = info.CreationTimeUtc;
        entry.EncodedFromFingerprint = ContentFingerprint(entry.FullName);
    }

    /// <summary>What can be said about the file now at an entry's path, compared with the one that was encoded.</summary>
    public enum SourceCheck
    {
        /// <summary>It matches what was noted when the encode started (or the file is gone).</summary>
        Unchanged,
        /// <summary>It is known not to be that file: another size, time or creation time, other content, or (with nothing noted) written or created after the encoded file was finished.</summary>
        Changed,
        /// <summary>Nothing complete was noted when the encode started (an entry written by an earlier build, or one whose content could not be read), so it cannot be told.</summary>
        Unknown
    }

    /// <summary>Compares what is at the entry's path with what was noted as its encode started (size, last-write time, creation time, content fingerprint). A note missing any of those is
    /// not accepted as identity: an older entry, or a v7 row with no fingerprint, answers <see cref="SourceCheck.Unknown"/> unless the file is already known to differ. With no note at all,
    /// <paramref name="encodedFilePath"/> (where the result is or was staged) can still show a difference: a source written or created after that file was finished cannot be what it was
    /// made from. Being older than the encode proves nothing. This is change detection, not proof: a different file made to look the same in all of these ways would not be noticed.</summary>
    public static SourceCheck CheckSource(ResumeEntry entry, string? encodedFilePath = null) => CheckSourceFile(entry, entry.FullName, encodedFilePath);

    /// <summary>The same comparison for the file at <paramref name="path"/> - the original wherever it was moved to (a parked original), not only at the entry's own path.</summary>
    public static SourceCheck CheckSourceFile(ResumeEntry entry, string path, string? encodedFilePath = null) => CheckSourceFileDetailed(entry, path, encodedFilePath).Check;

    /// <summary>As <see cref="CheckSourceFile"/>, and also says whether the answer is <c>Indeterminate</c>: the file could not be examined right now (the path cannot be checked, or its content cannot be
    /// read - a lock, access denied, a share that dropped). That is reported as Unknown (kept, never deletable) to every ordinary caller; recovery of a parked original uses the flag to tell it from an
    /// Unknown that is permanent (an entry with no note of the source), and keeps its journal record for the former.</summary>
    public static (SourceCheck Check, bool Indeterminate) CheckSourceFileDetailed(ResumeEntry entry, string path, string? encodedFilePath = null)
    {
        // A file that cannot be reached (permissions, a share that dropped) is not "gone": it cannot be shown to be the original, so it is Unknown (kept), never Unchanged (deletable).
        var presence = FileOutcomeHelpers.PresenceOf(path);
        if (presence == FileOutcomeHelpers.FilePresence.Absent) return (SourceCheck.Unchanged, false);
        if (presence == FileOutcomeHelpers.FilePresence.Unknown) return (SourceCheck.Unknown, true);

        try
        {
            var info = new FileInfo(path);

            // Unknown below is permanent (nothing was noted about the source): the file itself was examined fine.
            if (entry.EncodedFromSize is not { } size || entry.EncodedFromModifiedUtc is not { } modified)
            {
                if (encodedFilePath is null || !File.Exists(encodedFilePath)) return (SourceCheck.Unknown, false);
                var finishedAt = File.GetLastWriteTimeUtc(encodedFilePath);
                return (info.LastWriteTimeUtc > finishedAt + TimeTolerance || info.CreationTimeUtc > finishedAt + TimeTolerance ? SourceCheck.Changed : SourceCheck.Unknown, false);
            }

            if (info.Length != size || (info.LastWriteTimeUtc - modified).Duration() > TimeTolerance) return (SourceCheck.Changed, false);
            if (entry.EncodedFromCreatedUtc is { } created && (info.CreationTimeUtc - created).Duration() > TimeTolerance) return (SourceCheck.Changed, false);
            if (entry.EncodedFromFingerprint is not { } fingerprint) return (SourceCheck.Unknown, false);

            // A fingerprint that could not be read (a lock, access denied) says nothing about the content: it is not a difference.
            var current = ContentFingerprint(path);
            if (current is null) return (SourceCheck.Unknown, true);
            return (string.Equals(current, fingerprint, StringComparison.Ordinal) ? SourceCheck.Unchanged : SourceCheck.Changed, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (SourceCheck.Unknown, true);
        }
    }

    /// <summary>True only when <see cref="CheckSource"/> says it is the file that was encoded (or the file is gone): the one answer that allows deleting or overwriting the source.</summary>
    public static bool SourceUnchangedSinceEncode(ResumeEntry entry, string? encodedFilePath = null) => CheckSource(entry, encodedFilePath) == SourceCheck.Unchanged;

    /// <summary>True when this Completed entry is the very file now found at its path: converted already, left in place, unchanged.</summary>
    public static bool IsConvertedAndUnchanged(ResumeEntry entry, FileInfo found) =>
        entry.Status == ResumeStatus.Completed
        && entry.KeptSourceSize == found.Length
        && entry.KeptSourceModifiedUtc is { } kept
        && (found.LastWriteTimeUtc - kept).Duration() <= TimeTolerance;

    /// <summary>A Completed entry still worth remembering: its kept source is still there, so forgetting it would convert that file again.</summary>
    public static bool StillNeeded(ResumeEntry entry) =>
        HoldsParkedOriginal(entry) || (entry.Status == ResumeStatus.Completed && entry.KeptSourceSize.HasValue && File.Exists(entry.FullName));

    /// <summary>True while the entry's original is set aside for a same-path replacement and not yet put back or disposed of (<see cref="ResumeEntry.ParkedOriginalPath"/>). Such an entry is the only
    /// record of where that file is: it is never dropped, and nothing but the parked-original recovery acts on it (no routing, retry, or cleanup) until the file is resolved.</summary>
    public static bool HoldsParkedOriginal(ResumeEntry entry) => !string.IsNullOrEmpty(entry.ParkedOriginalPath);

    /// <summary>The lane's entries that were converted with their source kept, by path - built once per scan, so asking about each scanned
    /// file is a lookup rather than a pass over the whole resume state.</summary>
    public static Dictionary<string, ResumeEntry> KeptSources(IEnumerable<ResumeEntry> state, string laneId)
    {
        var kept = new Dictionary<string, ResumeEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in state)
        {
            if (e.LaneId == laneId && e.Status == ResumeStatus.Completed && e.KeptSourceSize.HasValue) kept[e.FullName] = e;
        }

        return kept;
    }

    /// <summary>Whether this file, found in a scan, was already converted with its source kept (so it is not work to do).</summary>
    public static bool AlreadyConverted(IReadOnlyDictionary<string, ResumeEntry> keptSources, FileInfo found) =>
        keptSources.TryGetValue(found.FullName, out var entry) && IsConvertedAndUnchanged(entry, found);
}

/// <summary>How processing writes its results to resume.json: one entry at a time, merged into the file as it is now. Never a save of the
/// whole in-memory list - the Monitor page can change the queue (skip, reorder, override, remove, new files) while processing waits on a
/// move, a Sonarr/Radarr rescan or an encode, and a whole-list save would write the older picture back over those edits.
/// An entry's processing-owned fields (below) are copied over; the user-owned ones (Order, Skipped, PresetOverride, Removed,
/// CreatedByQueueEdit, DestinationLaneId) stay whatever is freshest on disk.</summary>
internal static class ResumeResults
{
    private static bool Same(ResumeEntry a, ResumeEntry b) =>
        a.LaneId == b.LaneId && string.Equals(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase);

    public static void Save(IResumeStateStore store, ResumeEntry entry, string resumeFilePath)
    {
        store.Update(resumeFilePath, disk =>
        {
            var onDisk = disk.FirstOrDefault(e => Same(e, entry));
            if (onDisk is null)
            {
                // Genuinely rare (Reset Resume File fired mid-pass): keep this file's result rather than dropping it.
                disk.Add(entry);
                return true;
            }

            onDisk.Status = entry.Status;
            onDisk.EncodedFilePath = entry.EncodedFilePath;
            onDisk.EncodedFileDesiredName = entry.EncodedFileDesiredName;
            onDisk.PendingCompanionSourceDirectory = entry.PendingCompanionSourceDirectory;
            onDisk.PendingCompanionVideoDestPath = entry.PendingCompanionVideoDestPath;
            onDisk.LastRetryFailureMessage = entry.LastRetryFailureMessage;
            onDisk.HeldAfterFailure = entry.HeldAfterFailure;
            onDisk.KeptSourceSize = entry.KeptSourceSize;
            onDisk.KeptSourceModifiedUtc = entry.KeptSourceModifiedUtc;
            onDisk.EncodedFromSize = entry.EncodedFromSize;
            onDisk.EncodedFromModifiedUtc = entry.EncodedFromModifiedUtc;
            onDisk.EncodedFromCreatedUtc = entry.EncodedFromCreatedUtc;
            onDisk.EncodedFromFingerprint = entry.EncodedFromFingerprint;
            onDisk.EncodedFileSize = entry.EncodedFileSize;
            onDisk.EncodedFileDigest = entry.EncodedFileDigest;
            onDisk.FiledInOutput = entry.FiledInOutput;
            onDisk.ParkedOriginalPath = entry.ParkedOriginalPath;
            return true;
        });
    }

    // Every settable field of an entry except its identity (lane + path): what a change-based save compares and copies.
    private static readonly System.Reflection.PropertyInfo[] Fields = typeof(ResumeEntry).GetProperties()
        .Where(p => p.CanRead && p.CanWrite && p.Name is not (nameof(ResumeEntry.LaneId) or nameof(ResumeEntry.FullName)))
        .ToArray();

    // The fields only the Monitor page's queue edits change (and a pass may fill in when they are still unset).
    private static readonly HashSet<string> UserOwned = new()
    {
        nameof(ResumeEntry.Order), nameof(ResumeEntry.Skipped), nameof(ResumeEntry.PresetOverride), nameof(ResumeEntry.Removed),
        nameof(ResumeEntry.DestinationLaneId), nameof(ResumeEntry.CreatedByQueueEdit)
    };

    private static string KeyOf(ResumeEntry e) => e.LaneId + "|" + e.FullName.ToUpperInvariant();

    private static ResumeEntry Copy(ResumeEntry e, string? fullName = null)
    {
        var copy = new ResumeEntry { LaneId = e.LaneId, FullName = fullName ?? e.FullName };
        foreach (var field in Fields) field.SetValue(copy, field.GetValue(e));
        return copy;
    }

    /// <summary>A copy of the list as this pass last knew the file to be (taken right after a refresh from disk): the baseline for
    /// <see cref="SaveChanges"/>.</summary>
    public static List<ResumeEntry> Snapshot(IEnumerable<ResumeEntry> entries) => entries.Select(e => Copy(e)).ToList();

    /// <summary>Saves what this pass changed - and only that - into the file as it is now. A pass that adds files, remaps renames or prunes works on
    /// an in-memory list that was refreshed from disk a moment earlier; saving that whole list would write the older picture back over a skip,
    /// reorder, override or new file the Monitor page committed in between. Here an entry added since the baseline is added, one removed is removed,
    /// and for one that is in both only the fields that differ from the baseline are copied, so every other field keeps whatever is freshest on disk. When the pass and the Monitor page both changed the same queue field since the baseline, the Monitor page's value stays.
    /// <paramref name="renames"/> (old path to new path) lets a remapped file carry the disk entry's latest user edits to its new path.</summary>
    public static void SaveChanges(IResumeStateStore store, IReadOnlyList<ResumeEntry> baseline, IReadOnlyList<ResumeEntry> current, string resumeFilePath,
        IReadOnlyDictionary<string, string>? renames = null)
    {
        var before = baseline.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.First());
        var now = current.GroupBy(KeyOf).ToDictionary(g => g.Key, g => g.First());
        var added = now.Where(kv => !before.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        var removed = before.Where(kv => !now.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        var changed = now.Where(kv => before.TryGetValue(kv.Key, out var b) && Fields.Any(f => !Equals(f.GetValue(b), f.GetValue(kv.Value)))).Select(kv => kv.Value).ToList();
        if (added.Count == 0 && removed.Count == 0 && changed.Count == 0) return;

        store.Update(resumeFilePath, disk =>
        {
            ResumeEntry? OnDisk(ResumeEntry e) => disk.FirstOrDefault(d => d.LaneId == e.LaneId && string.Equals(d.FullName, e.FullName, StringComparison.OrdinalIgnoreCase));

            // A file FileBot renamed is one queue entry that changed its path: the entry on disk under the old path carries the latest queue edits
            // (skip, order, profile, destination), and they go to the new path - whether or not the Monitor page has already tracked that path.
            var handled = new HashSet<ResumeEntry>();
            foreach (var old in removed.ToList())
            {
                var rename = renames?.FirstOrDefault(kv => string.Equals(kv.Key, old.FullName, StringComparison.OrdinalIgnoreCase));
                if (rename is not { Value: { } to }) continue;
                var target = current.FirstOrDefault(e => e.LaneId == old.LaneId && string.Equals(e.FullName, to, StringComparison.OrdinalIgnoreCase));
                if (target is null || OnDisk(old) is not { } oldOnDisk) continue;

                var targetOnDisk = OnDisk(target);
                if (targetOnDisk is null)
                {
                    var moved = Copy(oldOnDisk, target.FullName);
                    foreach (var field in Fields)
                    {
                        if (!Equals(field.GetValue(old), field.GetValue(target))) field.SetValue(moved, field.GetValue(target));
                    }

                    disk.Remove(oldOnDisk);
                    disk.Add(moved);
                }
                else if (targetOnDisk.Status == ResumeStatus.Pending)
                {
                    // Both identities are on disk (the Monitor page adopted the new path first): one pending file, so one entry survives - the one
                    // at the new path - carrying the queue edits of the old one, the same rule QueueRules.RemapRenamedFiles applies in memory.
                    foreach (var field in Fields.Where(p => UserOwned.Contains(p.Name))) field.SetValue(targetOnDisk, field.GetValue(oldOnDisk));
                    disk.Remove(oldOnDisk);
                }
                else
                {
                    continue;   // a finished copy sits at the new path: different work, nothing is replaced (the old entry goes with its missing file later)
                }

                removed.Remove(old);
                handled.Add(target);
            }

            foreach (var entry in added)
            {
                if (handled.Contains(entry) || OnDisk(entry) is not null) continue;   // handled above, or the Monitor page already tracked it
                disk.Add(Copy(entry));
            }

            foreach (var entry in removed)
            {
                if (OnDisk(entry) is { } gone) disk.Remove(gone);
            }

            foreach (var entry in changed)
            {
                if (handled.Contains(entry)) continue;

                var target = OnDisk(entry);
                if (target is null)
                {
                    disk.Add(Copy(entry));   // taken out of the file meanwhile (Reset Resume File): this pass still has work to record
                    continue;
                }

                var was = before[KeyOf(entry)];
                foreach (var field in Fields)
                {
                    if (Equals(field.GetValue(was), field.GetValue(entry))) continue;   // the pass did not change this one

                    // A queue edit is the user's: when the Monitor page changed the same field after the baseline, theirs wins over what a pass
                    // computed from the older list (an order backfill, say). The pass's own results always apply.
                    if (UserOwned.Contains(field.Name) && !Equals(field.GetValue(target), field.GetValue(was))) continue;

                    field.SetValue(target, field.GetValue(entry));
                }
            }

            return true;
        });
    }

    /// <summary>Drops these entries from the file (they were removed in memory because their file is gone), leaving every other entry as it is.</summary>
    public static void Remove(IResumeStateStore store, IReadOnlyCollection<ResumeEntry> entries, string resumeFilePath)
    {
        if (entries.Count == 0) return;
        store.Update(resumeFilePath, disk =>
        {
            disk.RemoveAll(d => entries.Any(e => Same(d, e)));
            return true;
        });
    }
}

public sealed class ResumeEntry
{
    public required string LaneId { get; set; }
    public required string FullName { get; set; }
    public ResumeStatus Status { get; set; }

    /// <summary>Only set (and only meaningful) when Status is MoveFailed - where the already-
    /// encoded file actually sits (in the lane's Output folder, not FullName's original Input
    /// location) so a later retry can find and route it without re-encoding. Deliberately a
    /// collision-safe staging name (not a human-readable one) for as long as MoveFailed persists -
    /// see ConversionOrchestrator's own notes on why a deterministic name is never left sitting in
    /// Output unrouted.</summary>
    public string? EncodedFilePath { get; set; }

    /// <summary>Only set (and only meaningful) when Status is MoveFailed - the clean, human-
    /// readable filename (e.g. "Movie.mkv") EncodedFilePath's own file should be classified and
    /// renamed/routed under once the retry succeeds. Kept separate from EncodedFilePath itself
    /// because that path stays under a collision-safe staging name while MoveFailed persists, so
    /// nothing downstream can derive the real title/episode info from it any more.</summary>
    public string? EncodedFileDesiredName { get; set; }

    /// <summary>The exception message from this entry's most recent failed move-retry attempt -
    /// lets ConversionOrchestrator tell "still the same unresolved problem as last poll" apart
    /// from "something new/different just happened." Real gap found live: an offline destination
    /// (a network share down overnight) got retried every single poll, and every one of those
    /// retries logged at Error severity - which, combined with the "don't keep an empty/error-free
    /// pass's log" cleanup, meant a KEPT log file every poll for as long as the outage lasted, the
    /// same file-proliferation problem all over again just gated on "error" instead of "empty."
    /// Null once the entry isn't MoveFailed any more (succeeded, or removed).</summary>
    public string? LastRetryFailureMessage { get; set; }

    /// <summary>Only set (and only meaningful) when Status is CompanionMoveFailed or
    /// CleanupPending - the video's own original source folder. For CompanionMoveFailed, this is
    /// where the stranded companion files are still sitting, so a later retry can find and move
    /// them without needing the video's own path (which may already be gone by then). For
    /// CleanupPending, this is simply the folder a later retry's own CleanUpEmptySourceFolder call
    /// targets, once a fresh *arr confirmation says it's actually safe to.</summary>
    public string? PendingCompanionSourceDirectory { get; set; }

    /// <summary>Only set (and only meaningful) when Status is CompanionMoveFailed or
    /// CleanupPending - the video's own actual final destination path. For CompanionMoveFailed,
    /// needed to derive each companion's destination filename from the video's real (possibly
    /// collision-renamed) stem - same reasoning as ICompanionFileService.MoveCompanionFiles' own
    /// routedVideoDestPath parameter. For CleanupPending, needed only for its filename (to
    /// re-derive the *arr lookup name and TV/movie classification for a fresh rescan attempt).</summary>
    public string? PendingCompanionVideoDestPath { get; set; }

    /// <summary>Permanent queue position, lower first - stamped once, automatically, the moment a
    /// file is first tracked (by a real scan or by any queue-control action), then never
    /// recomputed again except by an explicit user reorder (drag, or move to top/bottom, which is
    /// just a reorder submission on the frontend). Deliberately NOT a "user-set override that
    /// falls back to live natural order otherwise" - a file's queue position must be locked the
    /// instant it appears and stay put through preset overrides, skips, or anything else that
    /// isn't a direct reorder. See ResumeQueueOrder.NextOrder for how a fresh Order value is
    /// chosen (always appended after the current maximum, so a new file lands at the end).</summary>
    public int? Order { get; set; }

    /// <summary>User-set "skip this pass" - a Skipped Pending entry stays visible in the queue
    /// (dimmed) but ConversionOrchestrator excludes it from what actually gets encoded. Persists
    /// until explicitly un-skipped - not a true one-shot "just this pass," since there's no
    /// reliable single moment to auto-clear it at (a monitoring pass has no natural end event the
    /// resume file can hook into) - the user toggles it back on from the same queue row.</summary>
    public bool Skipped { get; set; }

    /// <summary>User-set preset for this specific file, overriding the lane's TvPreset/MoviePreset
    /// for this one entry only. Null/empty means "use the lane default" (the normal behavior).</summary>
    public string? PresetOverride { get; set; }

    /// <summary>User-set "Remove from queue" - unlike Skipped, a Removed entry is dropped from the
    /// Monitor page's In Queue list entirely, not just dimmed. Confirmed live: the queue's Input
    /// folder is live-rescanned on every poll, so simply deleting the resume entry (the original
    /// implementation) let an untouched file get rediscovered and reappear within ~1.5s, making
    /// Remove look like it did nothing. Setting this flag instead - and having it also imply
    /// Skipped, so ConversionOrchestrator never encodes it - keeps the file tracked (so the live
    /// rescan skips it, same as any other tracked path) while hiding it from the UI, with the same
    /// "stays until explicitly toggled back" reasoning as Skipped.</summary>
    public bool Removed { get; set; }

    /// <summary>Set when the encode itself failed (ERROR 101). Such a file stays in the queue with its ERROR
    /// badge and is NOT retried by later passes, so a file that fails the same way every time doesn't make
    /// a new error report on every poll - the user investigates first, then uses Remove on the row to
    /// queue it afresh. Other Error entries (an abort, a missing preset) are still retried by rescans.</summary>
    public bool HeldAfterFailure { get; set; }

    /// <summary>Set when this entry finished (Completed) and its source was deliberately left in the Input folder
    /// (Settings > Processing > "Maintain"): the size the source had. A rescan that finds the same file, unchanged,
    /// leaves the entry alone instead of queueing it again - otherwise a kept source is converted again on every
    /// pass, forever. A different file at the same path (another size or time) is a new arrival, as before.</summary>
    public long? KeptSourceSize { get; set; }

    /// <summary>The last-write time the kept source had. See <see cref="KeptSourceSize"/>.</summary>
    public DateTime? KeptSourceModifiedUtc { get; set; }

    /// <summary>The size the source had when its encode started. Whatever is found at the path later (a retried move can come hours or days after the encode, an encode
    /// itself takes hours) is only the file this encode replaced if it still matches: a different file there - Sonarr or Radarr upgraded it, someone copied a new
    /// one in - is new work and is never deleted as "the source". Null (an entry from before this was recorded) means nothing to compare against.</summary>
    public long? EncodedFromSize { get; set; }

    /// <summary>The last-write time the source had when its encode started. See <see cref="EncodedFromSize"/>.</summary>
    public DateTime? EncodedFromModifiedUtc { get; set; }

    /// <summary>The creation time the source had when its encode started: a file replaced by deleting and copying a new one in has a new one. See <see cref="EncodedFromSize"/>.</summary>
    public DateTime? EncodedFromCreatedUtc { get; set; }

    /// <summary>A fingerprint of the source's content when its encode started (see <see cref="ResumeKeeping.ContentFingerprint"/>): tells another video from this one even when its
    /// size and times were made to match. See <see cref="EncodedFromSize"/>.</summary>
    public string? EncodedFromFingerprint { get; set; }

    /// <summary>The crash journal's record of the encoded OUTPUT (not the source): its size, noted with <see cref="EncodedFileDigest"/> just before the file is moved to where it will be filed. After a
    /// stop, a file found at the recorded destination is only this job's output if it has this size and this digest - a different file there (Sonarr, Radarr or a person put one in, even one of the
    /// same length and time) is never taken for it, and the original is never deleted on its say-so. Null when nothing is journalled (and for an entry journalled by 3.1.0-rc.1, which did not
    /// record it: such a destination cannot be confirmed, so the original is kept). See <see cref="ResumeKeeping.CheckOutput"/>.</summary>
    public long? EncodedFileSize { get; set; }

    /// <summary>The SHA-256 of the encoded output's whole content, as hex. See <see cref="EncodedFileSize"/>.</summary>
    public string? EncodedFileDigest { get; set; }

    /// <summary>Set (with a journalled destination) when the encoded output is being published in the lane's Output folder under its clean name rather than moved into a library: Move converted
    /// files is off, or a library collision is set to Skip. Such a file has no companions to move and no folder to clean up, so a restart finishes it differently from a library move.</summary>
    public bool FiledInOutput { get; set; }

    /// <summary>Set while an original that occupied the filed file's own path (a lane whose Input is its library, or its Output) is parked: the exact, unique path it was moved aside to while the output is
    /// published. Written to the journal BEFORE the original is moved, and cleared only once it is resolved - put back after a failed publication, or deleted/recycled once the output is confirmed. While it
    /// is set, a restart acts on exactly that file (and nothing that merely looks like it): it restores it, or disposes of it after verifying both the output and that it is still the original.</summary>
    public string? ParkedOriginalPath { get; set; }

    /// <summary>Set when this entry was created purely as bookkeeping for a queue-control action
    /// (reorder/skip/preset-override/remove) on a file the current pass hadn't tracked yet, rather
    /// than by ConversionOrchestrator's own real resume-from-interrupted-run bookkeeping. Exists
    /// solely so the Monitor page's New/Resumed badge isn't fooled by it - without this, touching
    /// even one fresh file's queue metadata makes ComputeUpNext's "does this lane have pending work
    /// left over from before" heuristic (any Pending entry at all) fire, mislabeling every other
    /// file in that lane "Resumed" too, and reordering the whole queue (which touches every file at
    /// once) made literally the entire lane show "Resumed". Confirmed live on the real running app.</summary>
    public bool CreatedByQueueEdit { get; set; }

    /// <summary>Set once, at entry-creation time, when the optional FileBot pre-processing pass
    /// (IFileBotRunner) ran for this lane but left this specific file's name/location untouched -
    /// treated as "FileBot didn't/couldn't confidently match this one." Drives the Monitor page's
    /// "Unmatched" queue badge. Never recomputed for an already-tracked entry.</summary>
    public bool FileBotUnmatched { get; set; }

    /// <summary>User-set "land this file in a different lane's library" - the ONLY thing it changes
    /// is which lane's TvShowBasePath / MovieBasePath the finished file is routed into (and its
    /// companion files with it). LaneId keeps meaning "the lane whose Input folder holds this file",
    /// which owns scanning, the preset, the Output/staging folder, the source-cleanup boundary and
    /// the Sonarr/Radarr handoff - none of that changes, and the file is never moved, so Sonarr/
    /// Radarr never see it missing. Null (every existing entry) means "land in my own lane's
    /// library". Additive: a build that predates this field ignores it and lands the file in its
    /// own lane's library, which is the wrong place for that one file but loses nothing.</summary>
    public string? DestinationLaneId { get; set; }
}

/// <summary>The single source of truth for "what Order value does a newly-tracked file get" -
/// always one past the current maximum, so a fresh entry always lands at the very end of the
/// queue rather than sorting ambiguously (null) or colliding with an existing position.</summary>
public static class ResumeQueueOrder
{
    public static int NextOrder(IEnumerable<ResumeEntry> resumeState) =>
        resumeState.Where(e => e.Order.HasValue).Select(e => e.Order!.Value).DefaultIfEmpty(-1).Max() + 1;

    /// <summary>
    /// Gives a newly arrived file its place in the queue, honouring the priority rules (3.1). With no rules this is exactly <see cref="NextOrder"/>: the very end. With rules
    /// the file goes to the end of its own tier: after the last waiting file of its tier or a higher one, so a lowered show's backlog stays behind everything newer, a
    /// raised show's files go ahead of the rest, and files of the same tier keep their arrival order. Positions after it shift down by one; nothing is renumbered
    /// otherwise, and a file the user placed by hand is never moved by a rule here (only new arrivals are placed).
    /// </summary>
    public static void Place(List<ResumeEntry> resumeState, ResumeEntry entry, Compressarr.Core.Queue.QueuePriorityRules rules)
    {
        if (rules.IsEmpty) { entry.Order = NextOrder(resumeState); return; }

        var tier = rules.TierOf(entry.FullName);
        var waiting = resumeState.Where(e => !ReferenceEquals(e, entry) && e.Status == ResumeStatus.Pending && !e.Removed && e.Order.HasValue).ToList();
        if (waiting.Count == 0) { entry.Order = NextOrder(resumeState); return; }

        var sameOrHigher = waiting.Where(e => rules.TierOf(e.FullName) >= tier).Select(e => e.Order!.Value).ToList();
        var insertAt = sameOrHigher.Count > 0 ? sameOrHigher.Max() + 1 : waiting.Min(e => e.Order!.Value);

        foreach (var other in resumeState)
        {
            if (!ReferenceEquals(other, entry) && other.Order is { } order && order >= insertAt) other.Order = order + 1;
        }
        entry.Order = insertAt;
    }
}

/// <summary>Implemented by a queue store that can be unavailable (the database that holds the queue cannot be opened): the engine checks it before a pass and pauses instead of
/// working from an empty queue.</summary>
public interface IQueueAvailability
{
    /// <summary>Why the queue cannot be used right now, or null when it can.</summary>
    string? UnavailableReason(string resumeFilePath);
}

public interface IResumeStateStore
{
    List<ResumeEntry> Load(string path);
    void Save(List<ResumeEntry> state, string path);
    void DeleteIfComplete(List<ResumeEntry> state, string path);

    /// <summary>Atomically loads resume state, applies mutate, and saves the result - all under
    /// one lock. Mirrors IConfigStore.Update for the exact same reason: the web UI's queue-control
    /// endpoints (reorder/skip/preset-override/remove) can fire concurrently with each other and
    /// with ConversionOrchestrator's own in-flight pass, and separate Load()+Save() calls race on
    /// the read - each starts from the same pre-mutation snapshot, so whichever Save happens last
    /// silently clobbers the other's change (a lost update), even though neither call throws.
    /// Confirmed live: a user's reorder/skip/preset-override change was wiped out the moment the
    /// in-flight file being encoded finished and ConversionOrchestrator did its own next Save.</summary>
    T Update<T>(string path, Func<List<ResumeEntry>, T> mutate);
}

/// <summary>Ported from Import-CompressarrResumeState/Export-CompressarrResumeState. Written to
/// disk after every single file processed (not batched), so it's always accurate to the file
/// currently in flight — an interrupted run resumes mid-lane instead of rescanning already
/// attempted files.</summary>
public sealed class JsonResumeStateStore : IResumeStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private readonly SemaphoreSlim _lock = new(1, 1);

    public List<ResumeEntry> Load(string path)
    {
        if (!File.Exists(path)) return new List<ResumeEntry>();

        var raw = File.ReadAllText(path);
        return JsonSerializer.Deserialize<List<ResumeEntry>>(raw, Options) ?? new List<ResumeEntry>();
    }

    public void Save(List<ResumeEntry> state, string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
        {
            Directory.CreateDirectory(folder);
        }

        var json = JsonSerializer.Serialize(state, Options);

        // Write to a temp file and swap it into place rather than writing the target file
        // directly, so a concurrent Load() never observes a partially-written/torn file even if
        // it happens to run outside the Update() lock below - same pattern JsonConfigStore.Save
        // already uses, for the same reason. resume.json is rewritten after every single file AND
        // every queue-control edit, so a crash/power-loss mid-write here is high-frequency enough
        // to matter, unlike a one-off settings save.
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
    }

    public void DeleteIfComplete(List<ResumeEntry> state, string path)
    {
        var hasOutstanding = state.Any(e => e.Status is ResumeStatus.Pending or ResumeStatus.Error or ResumeStatus.MoveFailed or ResumeStatus.CompanionMoveFailed or ResumeStatus.CleanupPending or ResumeStatus.FinishPending);
        if (hasOutstanding || !File.Exists(path)) return;

        // Nothing left to do - but a converted file whose source was kept must stay remembered, or it is converted again.
        var kept = state.Where(ResumeKeeping.StillNeeded).ToList();
        if (kept.Count == 0) File.Delete(path); else Save(kept, path);
    }

    public T Update<T>(string path, Func<List<ResumeEntry>, T> mutate)
    {
        _lock.Wait();
        try
        {
            var state = Load(path);
            var result = mutate(state);
            if (state.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);   // an empty queue and no file read the same, and this keeps "nothing left" atomic with the change
            }
            else
            {
                Save(state, path);
            }

            return result;
        }
        finally
        {
            _lock.Release();
        }
    }
}
