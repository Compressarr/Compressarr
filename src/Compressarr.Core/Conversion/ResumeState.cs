using Compressarr.Core.Config;
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
    CleanupPending
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
        if (mode != DeleteAfterConvertMode.Maintain || !File.Exists(entry.FullName)) return;
        var info = new FileInfo(entry.FullName);
        entry.KeptSourceSize = info.Length;
        entry.KeptSourceModifiedUtc = info.LastWriteTimeUtc;
    }

    /// <summary>True when this Completed entry is the very file now found at its path: converted already, left in place, unchanged.</summary>
    public static bool IsConvertedAndUnchanged(ResumeEntry entry, FileInfo found) =>
        entry.Status == ResumeStatus.Completed
        && entry.KeptSourceSize == found.Length
        && entry.KeptSourceModifiedUtc is { } kept
        && (found.LastWriteTimeUtc - kept).Duration() <= TimeTolerance;

    /// <summary>A Completed entry still worth remembering: its kept source is still there, so forgetting it would convert that file again.</summary>
    public static bool StillNeeded(ResumeEntry entry) =>
        entry.Status == ResumeStatus.Completed && entry.KeptSourceSize.HasValue && File.Exists(entry.FullName);

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
        var hasOutstanding = state.Any(e => e.Status is ResumeStatus.Pending or ResumeStatus.Error or ResumeStatus.MoveFailed or ResumeStatus.CompanionMoveFailed or ResumeStatus.CleanupPending);
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
            Save(state, path);
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }
}
