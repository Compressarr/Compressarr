using Compressarr.Core.Config;
using Compressarr.Core.Conversion;

namespace Compressarr.Core.Queue;

public enum QueueEditResult
{
    Ok,
    /// <summary>The request named a lane that isn't configured.</summary>
    UnknownLane,
    /// <summary>The lane is fine but the file no longer exists, or already has an entry that
    /// isn't Pending (e.g. a failed file), so there is nothing editable to change.</summary>
    NotFound,
    /// <summary>The lane the file was to be assigned to isn't configured.</summary>
    UnknownDestinationLane
}

/// <summary>
/// Everything the Monitor page does to the queue, behind one door: build the list it displays, and
/// apply the user's edits (reorder, skip, remove, preset override, clear an error). Wraps
/// <see cref="QueueRules"/> with the atomic load-mutate-save the web layer needs - every edit runs
/// under IResumeStateStore.Update's lock, so a click can never be lost to a concurrent engine save.
/// </summary>
public interface IQueueService
{
    /// <summary>The In Queue list in the order it will be processed. Any file nobody has tracked yet
    /// is locked into the queue (at the end, in arrival order) the first time this sees it, then the
    /// list is rebuilt so what's shown matches what is now permanent.</summary>
    List<UpNextItem> GetUpNext(CompressarrConfig config, QueueRunContext currentRun);

    /// <summary>Sets an explicit order across the WHOLE submitted list (every lane) - what drag-to-
    /// reorder and Move to top/bottom send. Items naming an unconfigured lane, or a file that no
    /// longer exists, are skipped rather than failing the request.</summary>
    void Reorder(CompressarrConfig config, IReadOnlyList<(string LaneId, string FullName)> items);

    QueueEditResult Skip(CompressarrConfig config, string laneId, string fullName, bool skipped);

    /// <summary>Marks the entry Removed (and Skipped, so it's never encoded) rather than deleting
    /// it, so the live rescan keeps treating the file as already tracked. The file itself is left
    /// on disk.</summary>
    QueueEditResult Remove(CompressarrConfig config, string laneId, string fullName);

    /// <summary>Overrides the preset for this one file; null/blank clears it back to the lane's.</summary>
    QueueEditResult OverridePreset(CompressarrConfig config, string laneId, string fullName, string? preset);

    /// <summary>Assigns the file to land in another lane's library (see ResumeEntry.DestinationLaneId): only
    /// the landing library changes, never the file's position, preset, Output folder or lane. Null/blank, or
    /// the file's own lane, clears the assignment. A disabled destination lane is fine - its paths still
    /// exist.</summary>
    QueueEditResult SetDestination(CompressarrConfig config, string laneId, string fullName, string? destinationLaneId);

    /// <summary>Clears a failed file's Error entry (only that status) so the next scan can pick it up
    /// fresh. Returns how many entries were removed.</summary>
    int RemoveError(string laneId, string fullName);

    /// <summary>Changes the preset and/or the landing lane of many queued files in one step (3.1) - the same two per-file edits as
    /// <see cref="OverridePreset"/> and <see cref="SetDestination"/>, applied across the submitted list under one lock. Only waiting (Pending) files are touched: the
    /// file being encoded, a failed or held file, and a file that no longer exists keep what they have and are counted as skipped. Position never changes.
    /// <paramref name="presetAllowed"/> says whether a lane's encoder can run a profile; a file whose lane cannot is skipped (and counted separately) rather than given a profile
    /// it would fail with.</summary>
    BulkQueueResult BulkEdit(CompressarrConfig config, IReadOnlyList<(string LaneId, string FullName)> items, BulkQueueEdit edit, Func<LaneConfig, string, bool>? presetAllowed = null);

    /// <summary>Re-sorts the files already waiting by the configured priority rules (see <see cref="QueueRules.ApplyPriorityToWaiting"/>). Returns how many changed position.
    /// New arrivals are placed by the rules on their own; this is the one-off "apply to what is already waiting".</summary>
    int ApplyPriorityToWaiting(CompressarrConfig config);
}

/// <summary>What a bulk queue change does. Each half is applied only when its flag is set, so "change just the landing lane" leaves every file's preset alone.
/// An empty/blank <see cref="Preset"/> clears the per-file override back to the lane's own profile; an empty <see cref="DestinationLaneId"/> clears the landing lane.</summary>
public sealed record BulkQueueEdit(bool SetPreset, string? Preset, bool SetDestination, string? DestinationLaneId);

/// <summary>How a bulk queue change went. <see cref="Result"/> is Ok unless the request itself was wrong (an unknown destination lane), in which case nothing changed.</summary>
public sealed record BulkQueueResult(QueueEditResult Result, int Changed, int Skipped, int IncompatiblePreset);

/// <summary>How long the queue display may reuse a lane's folder listing. Every status poll (the Monitor page every 1.5 s per open tab, Home Assistant, scripts) builds the list from a
/// recursive scan of every lane's Input folder; on a share holding thousands of files that is real disk and network traffic competing with the encode. Zero = scan every time.</summary>
public sealed record QueueScanOptions(TimeSpan CacheFor);

/// <summary>A short-lived memory of what a folder scan found, for the queue display only (the engine scans for itself, always fresh). Cleared whenever the file being encoded
/// changes - a finished file's source is gone by then - so a just-finished file does not linger in the list.</summary>
internal sealed class CachedVideoFileScanner : IVideoFileScanner
{
    private readonly IVideoFileScanner _inner;
    private readonly TimeSpan _cacheFor;
    private readonly TimeProvider _time;
    private readonly object _lock = new();
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<FileInfo> Files)> _results = new();
    private string? _currentFile;

    public CachedVideoFileScanner(IVideoFileScanner inner, TimeSpan cacheFor, TimeProvider? time = null)
    {
        _inner = inner;
        _cacheFor = cacheFor;
        _time = time ?? TimeProvider.System;
    }

    public IReadOnlyList<FileInfo> FindVideoFiles(string inputPath, IReadOnlyList<string> vidTypes, long minSizeBytes, int limit)
    {
        if (_cacheFor <= TimeSpan.Zero) return _inner.FindVideoFiles(inputPath, vidTypes, minSizeBytes, limit);

        var key = string.Join("|", inputPath, string.Join(",", vidTypes), minSizeBytes, limit);
        lock (_lock)   // held through the scan: callers arriving together share one scan instead of each walking the folder
        {
            var now = _time.GetUtcNow();
            if (_results.TryGetValue(key, out var hit) && now - hit.At < _cacheFor) return hit.Files;

            var files = _inner.FindVideoFiles(inputPath, vidTypes, minSizeBytes, limit);
            _results[key] = (now, files);
            return files;
        }
    }

    /// <summary>Tells the cache which file is being encoded; a change (one finished, the next started, or the run ended) drops everything remembered.</summary>
    public void ObserveCurrentFile(string? fullName)
    {
        lock (_lock)
        {
            if (string.Equals(_currentFile, fullName, StringComparison.OrdinalIgnoreCase)) return;
            _currentFile = fullName;
            _results.Clear();
        }
    }
}

public sealed class QueueService : IQueueService
{
    private readonly IResumeStateStore _resumeStore;
    private readonly IPathExpander _pathExpander;
    private readonly IVideoFileScanner _scanner;
    private readonly CachedVideoFileScanner _cachedScanner;

    public QueueService(IResumeStateStore resumeStore, IPathExpander pathExpander, IVideoFileScanner scanner, QueueScanOptions? scanOptions = null)
    {
        _resumeStore = resumeStore;
        _pathExpander = pathExpander;
        _cachedScanner = new CachedVideoFileScanner(scanner, scanOptions?.CacheFor ?? TimeSpan.Zero);
        _scanner = _cachedScanner;
    }

    public List<UpNextItem> GetUpNext(CompressarrConfig config, QueueRunContext currentRun)
    {
        _cachedScanner.ObserveCurrentFile(currentRun.CurrentFileFullName);
        var upNext = QueueRules.BuildUpNext(config, _resumeStore.Load(AppPaths.GetResumeFilePath()), _pathExpander, _scanner, currentRun, out var sawUntracked);
        if (!sawUntracked) return upNext;

        // A file nobody has tracked yet is ordered by live folder-scan position, which isn't
        // arrival order - a later arrival that enumerates earlier would display above one already
        // waiting. Lock every such file in right now, at the end of the queue, the first time
        // anything sees it (this poll, or the engine's own pass - whichever comes first), then
        // redisplay so what's shown matches what's now permanent.
        _resumeStore.Update(AppPaths.GetResumeFilePath(), resumeState =>
        {
            QueueRules.LockInVisibleOrder(config, resumeState, _pathExpander, _scanner);
            return true;
        });
        return QueueRules.BuildUpNext(config, _resumeStore.Load(AppPaths.GetResumeFilePath()), _pathExpander, _scanner, currentRun, out _);
    }

    public void Reorder(CompressarrConfig config, IReadOnlyList<(string LaneId, string FullName)> items)
    {
        _resumeStore.Update(AppPaths.GetResumeFilePath(), resumeState =>
        {
            QueueRules.ApplyExplicitOrder(config, resumeState, items, _pathExpander);
            return true;
        });
    }

    public QueueEditResult Skip(CompressarrConfig config, string laneId, string fullName, bool skipped) =>
        Edit(config, laneId, fullName, entry => entry.Skipped = skipped);

    public QueueEditResult Remove(CompressarrConfig config, string laneId, string fullName) =>
        Edit(config, laneId, fullName, entry =>
        {
            entry.Removed = true;
            entry.Skipped = true;
        });

    public QueueEditResult OverridePreset(CompressarrConfig config, string laneId, string fullName, string? preset) =>
        Edit(config, laneId, fullName, entry => entry.PresetOverride = string.IsNullOrWhiteSpace(preset) ? null : preset);

    public QueueEditResult SetDestination(CompressarrConfig config, string laneId, string fullName, string? destinationLaneId)
    {
        var wanted = string.IsNullOrWhiteSpace(destinationLaneId) || destinationLaneId == laneId ? null : destinationLaneId;
        if (wanted is not null && config.Lanes.All(l => l.Id != wanted)) return QueueEditResult.UnknownDestinationLane;

        return Edit(config, laneId, fullName, entry => entry.DestinationLaneId = wanted);
    }

    public BulkQueueResult BulkEdit(CompressarrConfig config, IReadOnlyList<(string LaneId, string FullName)> items, BulkQueueEdit edit, Func<LaneConfig, string, bool>? presetAllowed = null)
    {
        var wantedDestination = string.IsNullOrWhiteSpace(edit.DestinationLaneId) ? null : edit.DestinationLaneId;
        if (edit.SetDestination && wantedDestination is not null && config.Lanes.All(l => l.Id != wantedDestination))
        {
            return new BulkQueueResult(QueueEditResult.UnknownDestinationLane, 0, items.Count, 0);
        }
        var wantedPreset = string.IsNullOrWhiteSpace(edit.Preset) ? null : edit.Preset;

        var counts = _resumeStore.Update(AppPaths.GetResumeFilePath(), resumeState =>
        {
            // As every single-file edit does: pin what is on screen first, so touching some files can never let an untouched one jump ahead.
            QueueRules.LockInVisibleOrder(config, resumeState, _pathExpander, _scanner);

            var changed = 0;
            var skipped = 0;
            var incompatible = 0;
            var seen = new HashSet<(string, string)>();
            foreach (var (laneId, fullName) in items)
            {
                if (!seen.Add((laneId, fullName.ToLowerInvariant()))) continue;   // the same file listed twice counts once

                var lane = config.Lanes.FirstOrDefault(l => l.Id == laneId);
                if (lane is null) { skipped++; continue; }
                if (edit.SetPreset && wantedPreset is not null && presetAllowed is not null && !presetAllowed(lane, wantedPreset)) { incompatible++; continue; }

                var entry = QueueRules.FindOrCreatePendingEntry(resumeState, lane.Id, fullName, _pathExpander.Expand(lane.Input));
                if (entry is null) { skipped++; continue; }

                if (edit.SetPreset) entry.PresetOverride = wantedPreset;
                if (edit.SetDestination) entry.DestinationLaneId = wantedDestination == lane.Id ? null : wantedDestination;
                changed++;
            }
            return (changed, skipped, incompatible);
        });

        return new BulkQueueResult(QueueEditResult.Ok, counts.changed, counts.skipped, counts.incompatible);
    }

    public int ApplyPriorityToWaiting(CompressarrConfig config) =>
        _resumeStore.Update(AppPaths.GetResumeFilePath(), resumeState =>
        {
            QueueRules.LockInVisibleOrder(config, resumeState, _pathExpander, _scanner);   // pin what is on screen first, then sort it
            return QueueRules.ApplyPriorityToWaiting(resumeState, new QueuePriorityRules(config.Priority.Rules));
        });

    public int RemoveError(string laneId, string fullName) =>
        _resumeStore.Update(AppPaths.GetResumeFilePath(), resumeState => resumeState.RemoveAll(e =>
            e.LaneId == laneId &&
            e.Status == ResumeStatus.Error &&
            string.Equals(e.FullName, fullName, StringComparison.OrdinalIgnoreCase)));

    /// <summary>The shared shape of every single-file edit: lock in everything currently visible
    /// first (so touching one untouched file can never let it jump ahead of the untouched files
    /// around it), then find-or-create this file's entry and apply the change.</summary>
    private QueueEditResult Edit(CompressarrConfig config, string laneId, string fullName, Action<ResumeEntry> apply)
    {
        var lane = config.Lanes.FirstOrDefault(l => l.Id == laneId);
        if (lane is null) return QueueEditResult.UnknownLane;

        var found = _resumeStore.Update(AppPaths.GetResumeFilePath(), resumeState =>
        {
            QueueRules.LockInVisibleOrder(config, resumeState, _pathExpander, _scanner);
            var entry = QueueRules.FindOrCreatePendingEntry(resumeState, lane.Id, fullName, _pathExpander.Expand(lane.Input));
            if (entry is null) return false;
            apply(entry);
            return true;
        });

        return found ? QueueEditResult.Ok : QueueEditResult.NotFound;
    }
}
