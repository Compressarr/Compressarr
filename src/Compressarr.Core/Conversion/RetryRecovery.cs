using Compressarr.Core.Arr;
using Compressarr.Core.Config;
using Compressarr.Core.Logging;
using Compressarr.Core.Queue;
using Compressarr.Core.Routing;

namespace Compressarr.Core.Conversion;

/// <summary>
/// Picks a lane's unfinished business back up at the start of every pass, before anything new is
/// scanned: it drops resume entries whose files are gone, retries a finished encode's failed move
/// (MoveFailed), retries stranded companion files (CompanionMoveFailed), and finishes a deferred
/// Sonarr/Radarr rescan + source-folder cleanup (CleanupPending). None of these re-encode anything.
///
/// Split out of ConversionOrchestrator.PrepareLaneAsync, where this was most of the method. The
/// code moved as-is - same order, same logging, same saves - and is covered by the same tests; only
/// its home changed - except the saves: each one now merges just the entry it handled into the file as it is now (ResumeResults), because
/// a retry can wait minutes on a Sonarr/Radarr rescan, and a save of the whole in-memory list would wipe a queue edit made meanwhile.
/// Returns how many entries were resolved this pass (RunResult.RetriesSucceeded).
/// </summary>
internal sealed class RetryRecovery
{
    private readonly IPathExpander _pathExpander;
    private readonly IFileRouter _fileRouter;
    private readonly ICompanionFileService _companionFiles;
    private readonly IArrUnmonitorService _arrUnmonitor;
    private readonly ITrashService _trash;
    private readonly IRunLogger _logger;
    private readonly IResumeStateStore _resumeStore;
    private readonly SourceCleanupCoordinator _cleanup;

    public RetryRecovery(
        IPathExpander pathExpander,
        IFileRouter fileRouter,
        ICompanionFileService companionFiles,
        IArrUnmonitorService arrUnmonitor,
        ITrashService trash,
        IRunLogger logger,
        IResumeStateStore resumeStore)
    {
        _pathExpander = pathExpander;
        _fileRouter = fileRouter;
        _companionFiles = companionFiles;
        _arrUnmonitor = arrUnmonitor;
        _trash = trash;
        _logger = logger;
        _resumeStore = resumeStore;
        _cleanup = new SourceCleanupCoordinator(arrUnmonitor, companionFiles, logger);
    }

    /// <param name="inputPath">The lane's own Input root - the cleanup boundary, never the destination lane's.</param>
    /// <param name="tvShowBasePath">The lane's own already-expanded TV library path (a per-file lane assignment overrides it).</param>
    /// <param name="movieBasePath">The lane's own already-expanded Movie library path.</param>
    public async Task<int> RecoverAsync(
        LaneConfig lane,
        CompressarrConfig config,
        string inputPath,
        string tvShowBasePath,
        string movieBasePath,
        List<ResumeEntry> resumeState,
        string resumeFilePath,
        CancellationToken cancellationToken)
    {
        var retriesSucceeded = 0;

        // A Pending or Error entry whose source file is gone (e.g. removed by hand, or already
        // handled by Sonarr/Radarr, between runs) can never be resumed or retried - drop it rather
        // than let it block this lane forever. Without this, a lane with only dead pending entries
        // falls into the branch below, finds nothing to process, and never falls back to scanning
        // Input for genuinely new files. Error entries need the same treatment for a different
        // reason: unlike Pending, they're never re-checked by anything else once their source
        // disappears, so a single dead Error entry pins resumeState.Count and RunOrchestrator's
        // "stillOutstanding" check permanently - the whole resume.json (including every already-
        // Completed entry sitting alongside it) never gets cleaned up, and "Resuming previous
        // incomplete run" keeps logging that inflated count on every pass.
        var deadEntries = resumeState.Where(e => e.LaneId == lane.Id
            && e.Status is ResumeStatus.Pending or ResumeStatus.Error
            && !File.Exists(e.FullName)).ToList();
        if (deadEntries.Count > 0)
        {
            foreach (var dead in deadEntries)
            {
                _logger.Log($"Resume entry for '{dead.FullName}' no longer exists - removing it.", LogSeverity.Error);
                resumeState.Remove(dead);
            }
            ResumeResults.Remove(_resumeStore, deadEntries, resumeFilePath);
        }

        // A CompanionMoveFailed entry whose companions' own source folder is gone (removed by
        // hand, or the whole thing cleaned up some other way between runs) has nothing left to
        // retry - same reasoning as deadEntries above, checked against
        // PendingCompanionSourceDirectory rather than FullName since the video itself is already
        // long gone from there by the time this status applies.
        var deadCompanionEntries = resumeState.Where(e => e.LaneId == lane.Id
            && e.Status == ResumeStatus.CompanionMoveFailed
            && !Directory.Exists(e.PendingCompanionSourceDirectory)).ToList();
        if (deadCompanionEntries.Count > 0)
        {
            foreach (var dead in deadCompanionEntries)
            {
                _logger.Log($"Resume entry for '{dead.FullName}' (awaiting companion retry) no longer has its source folder - removing it.", LogSeverity.Error);
                resumeState.Remove(dead);
            }
            ResumeResults.Remove(_resumeStore, deadCompanionEntries, resumeFilePath);
        }

        // MoveFailed entries from a prior pass: the encode already succeeded, only the move needs
        // retrying - no re-encode, just RouteFile again against the file HandBrake already wrote.
        // Runs before the normal pending-or-scan branch below so a file that's ready to be filed
        // gets filed before this pass starts encoding anything new. A dead entry here (the encoded
        // file itself is gone - moved/deleted by hand) is dropped the same way deadEntries above
        // drops a dead Pending/Error entry, checked against EncodedFilePath rather than FullName
        // since FullName is the original Input path, which delete-after-convert may have already
        // removed even though the entry is perfectly healthy.
        var moveFailedEntries = resumeState.Where(e => e.LaneId == lane.Id && e.Status == ResumeStatus.MoveFailed).ToList();
        foreach (var entry in moveFailedEntries)
        {
            if (entry.EncodedFilePath is null || !File.Exists(entry.EncodedFilePath))
            {
                _logger.Log($"Resume entry for '{entry.FullName}' (awaiting move retry) no longer has its encoded file - removing it.", LogSeverity.Error);
                resumeState.Remove(entry);
                ResumeResults.Remove(_resumeStore, new[] { entry }, resumeFilePath);
                continue;
            }

            // Captured before entry.EncodedFilePath gets nulled out below on success. Falls back
            // to parsing EncodedFilePath's own name for a resume.json entry written before
            // EncodedFileDesiredName existed - stale staging names from that era were still
            // deterministic (pre-fix), so the fallback is exactly as good as this field always was.
            var encodedFilePath = entry.EncodedFilePath;
            var desiredFileName = entry.EncodedFileDesiredName ?? Path.GetFileName(encodedFilePath);
            var retryIsTv = ContentClassifier.IsTvFile(desiredFileName);
            var retrySucceeded = false;
            var currentPath = encodedFilePath;

            // MoveFiles may have been turned off (or a collision Skip hit) since this entry first
            // failed - in either case routing has nothing further to do with it, but unlike a
            // still-failing retry, this outcome is final (Completed, never revisited), so it needs
            // its clean human-readable name now rather than staying under its GUID staging one
            // forever. Same "resting in Output" treatment (now also collision-safe via
            // FileRouter.ResolveCollision, not an unconditional overwrite) ProcessOneFileAsync's
            // own first-attempt path uses - see there for why. Returns null (stay at physicalPath)
            // when Skip applies to a real collision.
            string? RestInPlace(string physicalPath)
            {
                var desiredDestPath = Path.Combine(Path.GetDirectoryName(physicalPath)!, desiredFileName);
                string resolvedDestPath;
                try
                {
                    resolvedDestPath = FileRouter.ResolveCollision(desiredDestPath, config.Processing.OnDestinationCollision);
                }
                catch (DestinationCollisionSkippedException ex)
                {
                    _logger.Log($"  {ex.Message}");
                    return null;
                }
                File.Move(physicalPath, resolvedDestPath, overwrite: true);
                return resolvedDestPath;
            }

            // Only once this retry has genuinely reached its final resting place - either
            // routed, or deliberately left in place via a configured Skip - does the original
            // source get cleaned up, same "only once disposition is final" rule
            // ProcessOneFileAsync's own first-attempt path follows. Called before MoveCompanionFiles
            // below simply so companions follow once the source is confirmed handled - the actual
            // folder-emptiness cascade-delete (CleanUpEmptySourceFolder) runs much later now, after
            // the arr-unmonitor/rescan step, and no longer depends on this ordering for correctness.
            void CleanUpRetriedSource()
            {
                if (string.Equals(entry.FullName, currentPath, StringComparison.OrdinalIgnoreCase)) return;
                if (!File.Exists(entry.FullName) || config.Processing.DeleteAfterConvert == DeleteAfterConvertMode.Maintain) return;

                var attrs = File.GetAttributes(entry.FullName);
                if (attrs.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(entry.FullName, attrs & ~FileAttributes.ReadOnly);
                }
                _trash.DeleteFile(entry.FullName, config.Processing.DeleteAfterConvert);
            }

            // originalFileFullName/originalFileDirectory must be the TRUE original source location
            // (entry.FullName), not entry.EncodedFilePath - that's the already-converted file
            // sitting in the processing/staging area, a completely different folder. Passing the
            // wrong one here (a real bug found live) made companion-file matching search the wrong
            // directory and left the actual source folder behind as an orphan even after a
            // successful retried move. Declared outside the try block so the source-folder cleanup
            // after the arr-unmonitor call below (which must run AFTER it, not inside this try) can
            // still reach it.
            var originalSourceDirectory = Path.GetDirectoryName(entry.FullName)!;
            string? retryDestPath = null;
            var companionMoveFailed = false;
            var arrCleanupSafe = true;
            var cleanupFailed = false;

            try
            {
                var retryLibraries = LaneDestination.Resolve(config, _pathExpander, entry, tvShowBasePath, movieBasePath);
                retryDestPath = _fileRouter.RouteFile(encodedFilePath, desiredFileName, retryIsTv, retryLibraries.TvShowBasePath, retryLibraries.MovieBasePath, config.Processing.MoveFiles, config.Processing.OnDestinationCollision);
                ResumeKeeping.MarkCompleted(entry, config.Processing.DeleteAfterConvert);
                entry.EncodedFilePath = null;
                entry.EncodedFileDesiredName = null;
                entry.LastRetryFailureMessage = null;
                retrySucceeded = true;
                retriesSucceeded++;
                _logger.Log($"  Retried move for '{desiredFileName}' - succeeded.");

                currentPath = retryDestPath ?? RestInPlace(encodedFilePath) ?? encodedFilePath;
                CleanUpRetriedSource();

                if (retryDestPath is not null)
                {
                    try
                    {
                        _companionFiles.MoveCompanionFiles(entry.FullName, originalSourceDirectory, retryDestPath, config.Processing.DeleteAfterConvert, config.Processing.CompanionExtensions, config.Processing.UnmatchedCompanionAction, config.Processing.OnDestinationCollision);
                    }
                    catch (Exception ex)
                    {
                        // Same reasoning as ProcessOneFileAsync's own call site: a companion left
                        // stranded by a real move failure must not be swept up as an "unmatched
                        // leftover" by the folder cleanup below. Overrides the Completed status set
                        // above - the video itself is done, but the companions aren't, so this
                        // entry needs to come back around through the companion-retry loop on this
                        // lane's next pass rather than being reported as fully finished.
                        companionMoveFailed = true;
                        _logger.Log($"  Companion file handling skipped on move retry: {ex.Message}", LogSeverity.Error);
                        entry.Status = ResumeStatus.CompanionMoveFailed;
                        entry.PendingCompanionSourceDirectory = originalSourceDirectory;
                        entry.PendingCompanionVideoDestPath = retryDestPath;
                    }
                }
            }
            catch (DestinationCollisionSkippedException ex)
            {
                // Configured to skip, not an error - the file just stays put; nothing left to
                // retry, so this is as resolved as it's going to get.
                ResumeKeeping.MarkCompleted(entry, config.Processing.DeleteAfterConvert);
                entry.EncodedFilePath = null;
                entry.EncodedFileDesiredName = null;
                entry.LastRetryFailureMessage = null;
                retrySucceeded = true;
                retriesSucceeded++;
                _logger.Log($"  {ex.Message}");
                currentPath = RestInPlace(encodedFilePath) ?? encodedFilePath;
                CleanUpRetriedSource();
            }
            catch (Exception ex)
            {
                // Still failing - leave it as MoveFailed, tried again on the next pass. Only
                // flagged as an Error (which is what keeps this pass's log file from being
                // discarded - see RunOrchestrator) the FIRST time this exact failure shows up, or
                // if it's changed since last poll - a real gap found live: an offline destination
                // retried every single poll otherwise logged Error every single poll too, and a
                // multi-hour outage meant a kept log file per poll for the whole outage, the same
                // file-proliferation problem all over again just gated on "error" instead of
                // "empty." Still logged either way so it's visible live in the Recent Log panel,
                // just at Info once it's confirmed to be the same still-unresolved problem.
                var isSameFailureAsLastPoll = entry.LastRetryFailureMessage == ex.Message;
                var severity = isSameFailureAsLastPoll ? LogSeverity.Info : LogSeverity.Error;
                var suffix = isSameFailureAsLastPoll ? " (still failing, same as last check)" : "";
                _logger.Log($"  Retried move for '{desiredFileName}' failed again: {ex.Message}{suffix}", severity);
                entry.LastRetryFailureMessage = ex.Message;
            }

            // *arr is told to stop monitoring only once disposition is final, same rule as
            // source cleanup above. This call also triggers Sonarr/Radarr's own library rescan and
            // waits for it to likely finish (see IArrUnmonitorService.UnmonitorAsync) - it must run
            // BEFORE the source folder is actually removed below, same reasoning as
            // ProcessOneFileAsync's own call site. A still-failing retry leaves this untouched too
            // - nothing to finalize yet.
            if (retrySucceeded)
            {
                arrCleanupSafe = (await _cleanup.ConfirmRescanAsync(config, desiredFileName, retryIsTv, " on move retry", logDeferred: true, cancellationToken)).Safe;

                // Only now, after Sonarr/Radarr has had the chance to rescan the source folder
                // while it still physically existed AND that rescan was positively confirmed to
                // finish, is it actually safe to remove it if it's now empty - same ordering
                // ProcessOneFileAsync's own call site follows, and for the same reason (a folder
                // already gone at rescan time reads as disconnected, not empty, to Sonarr/Radarr;
                // a rescan that never confirmed finishing can't prove it ever saw the folder at
                // all). Skipped if the companion move itself failed - see ProcessOneFileAsync's
                // own call site for why.
                if (retryDestPath is not null && !companionMoveFailed && arrCleanupSafe)
                {
                    // Code-review finding: a filesystem-level cleanup failure (locked file,
                    // permission, antivirus interference, etc) was logged here but otherwise
                    // ignored - the entry still ended up Completed below regardless, so a
                    // transient cleanup error was just as permanently forgotten as an
                    // unconfirmed rescan used to be. Treated the same way now: falls through
                    // to CleanupPending just like arrCleanupSafe == false does.
                    cleanupFailed = _cleanup.TryCleanUpSourceFolder(config, originalSourceDirectory, inputPath, " on move retry") is not null;
                }
            }

            // Code-review finding: entry.Status was set to Completed unconditionally above the
            // moment the move itself succeeded, even when the *arr rescan was never confirmed and
            // cleanup was correctly deferred - leaving nothing to ever retry that deferred cleanup.
            // Overrides that guess now that the real outcome is known, same "not actually done yet"
            // treatment as CompanionMoveFailed above - just for the narrower remaining case where
            // the video AND its companions are both genuinely finished and only the folder cleanup
            // itself is still outstanding (whether because the rescan wasn't confirmed, or because
            // the cleanup call itself threw once it was).
            if (retrySucceeded && !companionMoveFailed && retryDestPath is not null && (!arrCleanupSafe || cleanupFailed))
            {
                entry.Status = ResumeStatus.CleanupPending;
                entry.PendingCompanionSourceDirectory = originalSourceDirectory;
                entry.PendingCompanionVideoDestPath = retryDestPath;
            }

            ResumeResults.Save(_resumeStore, entry, resumeFilePath);
        }

        // CompanionMoveFailed entries from a prior pass: the video itself is fully done, only its
        // stranded companions need retrying (and, once that succeeds, the folder-emptiness cleanup
        // that was skipped the first time around) - same "resolve leftover work before scanning for
        // anything new" ordering as the MoveFailed retry loop above, and re-running MoveCompanionFiles
        // is safe to repeat: it re-enumerates the source folder each time, so a companion already
        // moved by an earlier partial attempt simply isn't found again, only genuinely still-stranded
        // ones are retried.
        var companionRetryEntries = resumeState.Where(e => e.LaneId == lane.Id && e.Status == ResumeStatus.CompanionMoveFailed).ToList();
        foreach (var entry in companionRetryEntries)
        {
            var sourceDirectory = entry.PendingCompanionSourceDirectory!;
            var videoDestPath = entry.PendingCompanionVideoDestPath!;

            try
            {
                _companionFiles.MoveCompanionFiles(entry.FullName, sourceDirectory, videoDestPath, config.Processing.DeleteAfterConvert, config.Processing.CompanionExtensions, config.Processing.UnmatchedCompanionAction, config.Processing.OnDestinationCollision);
                _logger.Log($"  Retried companion file move for '{Path.GetFileName(videoDestPath)}' - succeeded.");

                var desiredFileName = Path.GetFileName(videoDestPath);
                var companionRetryIsTv = ContentClassifier.IsTvFile(desiredFileName);

                // Re-triggers the same unmonitor+rescan call the video's own first pass already
                // made - deliberately, not a bug: Sonarr/Radarr's own API already treats a repeat
                // call as a no-op ("already unmonitored - rescanned anyway"), and this is the only
                // way this retry can get a FRESH, positively-confirmed signal that it's now safe
                // to remove the source folder, without needing to persist the original pass's
                // rescan outcome across a run boundary.
                var arrCleanupSafe = (await _cleanup.ConfirmRescanAsync(config, desiredFileName, companionRetryIsTv, " on companion retry", logDeferred: true, cancellationToken)).Safe;

                if (arrCleanupSafe)
                {
                    if (_cleanup.TryCleanUpSourceFolder(config, sourceDirectory, inputPath, " on companion retry") is null)
                    {
                        ResumeKeeping.MarkCompleted(entry, config.Processing.DeleteAfterConvert);
                        entry.PendingCompanionSourceDirectory = null;
                        entry.PendingCompanionVideoDestPath = null;
                    }
                    else
                    {
                        // Code-review finding: a filesystem-level cleanup failure here used to be
                        // logged and then ignored - the entry still ended up Completed regardless,
                        // permanently forgetting the still-outstanding folder. PendingCompanion*
                        // fields are already correct (unchanged from the CompanionMoveFailed values
                        // above), so no need to re-set them - just stay in the cleanup lifecycle.
                        entry.Status = ResumeStatus.CleanupPending;
                    }
                }
                else
                {
                    // Code-review finding: this used to fall through to Completed even when the
                    // fresh *arr rescan just triggered above wasn't confirmed - the companions ARE
                    // genuinely done now, but marking Completed here would still lose track of the
                    // still-outstanding folder cleanup. CleanupPending picks up exactly that
                    // narrower remaining work on this lane's next pass (see the cleanup-retry loop
                    // below) - PendingCompanionSourceDirectory/VideoDestPath are already correct
                    // for it, no change needed there.
                    entry.Status = ResumeStatus.CleanupPending;
                }

                entry.LastRetryFailureMessage = null;
                retriesSucceeded++;
            }
            catch (Exception ex)
            {
                // Still failing - leave it as CompanionMoveFailed, tried again on the next pass.
                // Same "only flag as Error the first time / when it changes" dedup as the
                // MoveFailed retry loop above, for the same reason (an extended outage shouldn't
                // force a kept log file on every single poll).
                var isSameFailureAsLastPoll = entry.LastRetryFailureMessage == ex.Message;
                var severity = isSameFailureAsLastPoll ? LogSeverity.Info : LogSeverity.Error;
                var suffix = isSameFailureAsLastPoll ? " (still failing, same as last check)" : "";
                _logger.Log($"  Retried companion file move for '{Path.GetFileName(videoDestPath)}' failed again: {ex.Message}{suffix}", severity);
                entry.LastRetryFailureMessage = ex.Message;
            }

            ResumeResults.Save(_resumeStore, entry, resumeFilePath);
        }

        // CleanupPending entries from a prior pass: the video and its companions (if any) are both
        // fully done and correctly filed - only the *arr-confirmed source folder cleanup itself
        // remains, deferred because that pass's own rescan was never positively confirmed complete
        // (see ArrRescanOutcome/SafeToCleanUp). Re-triggers a FRESH unmonitor+rescan (same
        // idempotent-repeat reasoning as the companion-retry loop above - Sonarr/Radarr's own API
        // already treats a repeat call as a no-op) and only removes the folder once THIS pass's
        // rescan is confirmed safe, rather than assuming the earlier attempt eventually finished on
        // its own. Code-review finding: without this loop, a deferred cleanup had no path back to
        // ever being retried - the entry was marked Completed and could vanish from resume.json
        // entirely, leaving an empty source folder behind forever.
        var cleanupPendingEntries = resumeState.Where(e => e.LaneId == lane.Id && e.Status == ResumeStatus.CleanupPending).ToList();
        foreach (var entry in cleanupPendingEntries)
        {
            var sourceDirectory = entry.PendingCompanionSourceDirectory!;
            var videoDestPath = entry.PendingCompanionVideoDestPath!;

            // Already gone - by hand, or some other process - since the deferral. Unlike a dead
            // CompanionMoveFailed entry (companions genuinely lost), this is the actual goal
            // already achieved: no folder left to clean up, and no need to spend an *arr API call
            // confirming a rescan for cleanup that's already moot.
            if (!Directory.Exists(sourceDirectory))
            {
                ResumeKeeping.MarkCompleted(entry, config.Processing.DeleteAfterConvert);
                entry.PendingCompanionSourceDirectory = null;
                entry.PendingCompanionVideoDestPath = null;
                entry.LastRetryFailureMessage = null;
                ResumeResults.Save(_resumeStore, entry, resumeFilePath);
                continue;
            }

            var desiredFileName = Path.GetFileName(videoDestPath);
            var cleanupRetryIsTv = ContentClassifier.IsTvFile(desiredFileName);

            // logDeferred is off: this loop logs its own, deduplicated "still deferred" line below.
            var rescan = await _cleanup.ConfirmRescanAsync(config, desiredFileName, cleanupRetryIsTv, " on cleanup retry", logDeferred: false, cancellationToken);
            var arrCleanupSafe = rescan.Safe;
            var reasonKey = rescan.Outcome?.ToString() ?? rescan.FailureMessage!;

            if (arrCleanupSafe)
            {
                // context is null: this loop logs a failure itself, below, with its own Error-once/Info-on-repeat dedup.
                var cleanupError = _cleanup.TryCleanUpSourceFolder(config, sourceDirectory, inputPath, null);
                if (cleanupError is null)
                {
                    _logger.Log($"  Retried source folder cleanup for '{desiredFileName}' - succeeded.");
                    ResumeKeeping.MarkCompleted(entry, config.Processing.DeleteAfterConvert);
                    entry.PendingCompanionSourceDirectory = null;
                    entry.PendingCompanionVideoDestPath = null;
                    entry.LastRetryFailureMessage = null;
                    retriesSucceeded++;
                }
                else
                {
                    // Code-review finding: this used to be a "best-effort, log and move on"
                    // failure like every other cleanup call site - but for THIS status, the *arr
                    // confirmation is the only piece being tracked, so treating the whole entry as
                    // Completed the moment CleanUpEmptySourceFolder merely THREW (rather than
                    // actually succeeding) permanently forgot a transient filesystem problem
                    // (locked file, permission, antivirus interference, network share hiccup) with
                    // no path back to retrying it. Stays CleanupPending instead - same Error-once/
                    // Info-on-repeat dedup as the "not confirmed" branch below, keyed off the same
                    // LastRetryFailureMessage field (only one of the two branches runs per pass, so
                    // there's no ambiguity about which kind of failure it's tracking at any time).
                    var isSameAsLastPoll = entry.LastRetryFailureMessage == cleanupError;
                    var severity = isSameAsLastPoll ? LogSeverity.Info : LogSeverity.Error;
                    var suffix = isSameAsLastPoll ? " (still failing, same as last check)" : "";
                    _logger.Log($"  Source folder cleanup failed on cleanup retry: {cleanupError}{suffix}", severity);
                    entry.LastRetryFailureMessage = cleanupError;
                }
            }
            else
            {
                // Still not confirmed - leave it as CleanupPending, tried again on the next pass.
                // Same "only flag as Error the first time / when it changes" dedup as the other
                // retry loops above, for the same reason (a persistently-unreachable arr instance
                // shouldn't force a kept log file on every single poll).
                var isSameAsLastPoll = entry.LastRetryFailureMessage == reasonKey;
                var severity = isSameAsLastPoll ? LogSeverity.Info : LogSeverity.Error;
                var suffix = isSameAsLastPoll ? " (still not confirmed, same as last check)" : "";
                _logger.Log($"  Source folder cleanup still deferred for '{desiredFileName}' - rescan not positively confirmed complete{suffix}.", severity);
                entry.LastRetryFailureMessage = reasonKey;
            }

            ResumeResults.Save(_resumeStore, entry, resumeFilePath);
        }

        return retriesSucceeded;
    }
}
