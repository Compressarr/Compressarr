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
            && !ResumeKeeping.HoldsParkedOriginal(e)
            && FileOutcomeHelpers.PresenceOf(e.FullName) == FileOutcomeHelpers.FilePresence.Absent).ToList();
        if (deadEntries.Count > 0)
        {
            foreach (var dead in deadEntries)
            {
                _logger.Log($"Resume entry for '{dead.FullName}' no longer exists - removing it.", LogSeverity.Error);
                resumeState.Remove(dead);
            }
            ResumeResults.Remove(_resumeStore, deadEntries, resumeFilePath);
        }

        // A Pending or Error entry whose file is not inside this lane's Input folder (the Input setting was changed since it was queued, or a request named some
        // other file) is not work this lane may do: converting it would end in deleting that file and sweeping its folder, which are only ever allowed inside Input.
        // Only the queue entry is dropped; the file is not touched.
        var strayEntries = resumeState.Where(e => e.LaneId == lane.Id
            && e.Status is ResumeStatus.Pending or ResumeStatus.Error
            && !ResumeKeeping.HoldsParkedOriginal(e)
            && File.Exists(e.FullName)
            && !PathContainment.IsStrictlyInside(e.FullName, inputPath)).ToList();
        if (strayEntries.Count > 0)
        {
            foreach (var stray in strayEntries)
            {
                _logger.Log($"Resume entry for '{stray.FullName}' is not inside this lane's Input folder - removing it from the queue (the file itself is left alone).", LogSeverity.Error);
                resumeState.Remove(stray);
            }
            ResumeResults.Remove(_resumeStore, strayEntries, resumeFilePath);
        }

        // The crash journal (3.1): a file whose filing was interrupted (Compressarr stopped while routing it, dealing with its source, moving its companions or waiting for
        // Sonarr/Radarr) is turned back into the retry status for the stage it reached, so the loops below finish it - no re-encode.
        ResolveParkedOriginals(lane, config, resumeState, resumeFilePath);
        ResolveInterruptedFinishes(lane, config, inputPath, resumeState, resumeFilePath);

        // A CompanionMoveFailed entry whose companions' own source folder is gone (removed by
        // hand, or the whole thing cleaned up some other way between runs) has nothing left to
        // retry - same reasoning as deadEntries above, checked against
        // PendingCompanionSourceDirectory rather than FullName since the video itself is already
        // long gone from there by the time this status applies.
        var deadCompanionEntries = resumeState.Where(e => e.LaneId == lane.Id
            && e.Status == ResumeStatus.CompanionMoveFailed
            && !ResumeKeeping.HoldsParkedOriginal(e)
            && (string.IsNullOrEmpty(e.PendingCompanionSourceDirectory) || FileOutcomeHelpers.PresenceOf(e.PendingCompanionSourceDirectory) == FileOutcomeHelpers.FilePresence.Absent)).ToList();   // dropped only when verifiably gone, not when out of reach
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
        // An entry whose original is still set aside (see ResolveParkedOriginals) is left out of every loop below until that file is resolved: routing it, or dropping it as dead, would lose the record.
        var moveFailedEntries = resumeState.Where(e => e.LaneId == lane.Id && e.Status == ResumeStatus.MoveFailed && !ResumeKeeping.HoldsParkedOriginal(e)).ToList();
        foreach (var entry in moveFailedEntries)
        {
            // Dropped only when the encoded file is verifiably gone - not when it merely cannot be reached (a share that dropped): that entry is tried again.
            if (entry.EncodedFilePath is not null && FileOutcomeHelpers.PresenceOf(entry.EncodedFilePath) == FileOutcomeHelpers.FilePresence.Unknown)
            {
                _logger.Log($"Resume entry for '{entry.FullName}' (awaiting move retry): its encoded file '{entry.EncodedFilePath}' cannot be checked right now - left for the next pass.", LogSeverity.Error);
                continue;
            }

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
            var retryIsTv = LaneKinds.IsTv(lane, desiredFileName);
            var retrySucceeded = false;
            var currentPath = encodedFilePath;

            // The result is filed whatever the Input setting says now; but the original and its companions are only deleted, moved or swept if they are still inside
            // this lane's Input - the setting may have changed while the move waited.
            var sourceOutsideInput = !PathContainment.IsStrictlyInside(entry.FullName, inputPath);

            // MoveFiles may have been turned off (or a collision Skip hit) since this entry first
            // failed - in either case routing has nothing further to do with it, but unlike a
            // still-failing retry, this outcome is final (Completed, never revisited), so it needs
            // its clean human-readable name now rather than staying under its GUID staging one
            // forever. Same "resting in Output" treatment (now also collision-safe via
            // FileRouter.ResolveCollision, not an unconditional overwrite) ProcessOneFileAsync's
            // own first-attempt path uses - see there for why. Returns null (stay at physicalPath)
            // when Skip applies to a real collision.
            // The exact parked path of an original set aside for a replacement is journalled (and cleared once it is resolved): see FileOutcomeHelpers.PublishOverSource.
            void ParkedChanged(string? parked)
            {
                entry.ParkedOriginalPath = parked;
                ResumeResults.Save(_resumeStore, entry, resumeFilePath);
            }

            string? RestInPlace(string physicalPath)
            {
                var desiredDestPath = Path.Combine(Path.GetDirectoryName(physicalPath)!, desiredFileName);
                var collisionMode = FileOutcomeHelpers.OutputCollisionMode(desiredDestPath, entry.FullName, config.Processing.OnDestinationCollision, config.Processing.DeleteAfterConvert,
                    ResumeKeeping.SourceUnchangedSinceEncode(entry, physicalPath) && !sourceOutsideInput, out var disposeSourceFirst);
                string resolvedDestPath;
                try
                {
                    resolvedDestPath = FileRouter.ResolveCollision(desiredDestPath, collisionMode);
                }
                catch (DestinationCollisionSkippedException ex)
                {
                    _logger.Log($"  {ex.Message}");
                    return null;
                }
                // Journalled BEFORE the rename, as a library move is (see the router callback below): a stop after it is found as a file published in Output.
                ResumeKeeping.NoteOutputIdentity(entry, physicalPath);
                entry.Status = ResumeStatus.FinishPending;
                entry.PendingCompanionVideoDestPath = resolvedDestPath;
                entry.FiledInOutput = true;
                ResumeResults.Save(_resumeStore, entry, resumeFilePath);
                if (disposeSourceFirst) FileOutcomeHelpers.PublishOverSource(_trash, entry.FullName, config.Processing.DeleteAfterConvert, physicalPath, resolvedDestPath, ParkedChanged);
                else File.Move(physicalPath, resolvedDestPath, overwrite: true);
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

                if (sourceOutsideInput)
                {
                    _logger.Log($"  The source '{Path.GetFileName(entry.FullName)}' is not inside this lane's Input folder, so it was left where it is (deleting is only done inside Input).");
                    return;
                }

                // The move may have been waiting days for its destination: what is at the original path now is only "the source" if it is still the file that was encoded.
                var check = ResumeKeeping.CheckSource(entry, currentPath);
                if (check == ResumeKeeping.SourceCheck.Changed)
                {
                    _logger.Log($"  The source '{Path.GetFileName(entry.FullName)}' is not the file that was converted (it changed since), so it was kept: it is queued again as new work.");
                    return;
                }

                // A move queued by an earlier build has no note of what the source was when its encode started. A source older than the encode may be the original, or a different
                // file with an old date: that cannot be told, so it is left for the user (and not converted a second time).
                if (check == ResumeKeeping.SourceCheck.Unknown)
                {
                    ResumeKeeping.NoteSourceKept(entry);
                    _logger.Log($"  The source '{Path.GetFileName(entry.FullName)}' could not be confirmed to be the file that was converted (this move was queued by an earlier version), so it was kept: delete it yourself if it is.");
                    return;
                }

                if (!FileOutcomeHelpers.DisposeSource(_trash, entry.FullName, config.Processing.DeleteAfterConvert))
                {
                    // The trash service only logs a removal that failed: the original is still there. Kept for the user, and noted as converted so a scan does not take it for new work.
                    ResumeKeeping.NoteSourceKept(entry);
                    _logger.Log($"  The original '{Path.GetFileName(entry.FullName)}' could not be removed, so it was kept (delete it yourself).", LogSeverity.Error);
                }
            }

            // The encoded file is where it will stay (routed, or resting in Output under its clean name): only now is the entry done. Marking it Completed any
            // earlier would let a failure of the move above (a locked file, a share that dropped) be swallowed as "still failing" with the entry already
            // Completed and its EncodedFilePath forgotten - nothing would ever retry, and the file would sit under its staging name for good.
            void ResolveRetry(string finalPath)
            {
                currentPath = finalPath;
                ResumeKeeping.MarkCompleted(entry, config.Processing.DeleteAfterConvert);
                entry.EncodedFilePath = null;
                entry.EncodedFileDesiredName = null;
                // The journal's notes of this move are done with (a companion failure or a pending cleanup below sets the ones it needs again).
                entry.PendingCompanionVideoDestPath = null;
                entry.PendingCompanionSourceDirectory = null;
                ResumeKeeping.ClearOutputIdentity(entry);
                entry.LastRetryFailureMessage = null;
                retrySucceeded = true;
                retriesSucceeded++;
            }

            // Removing the original is housekeeping once the result is safe: a failure is reported, never allowed to undo or hide the finished move.
            void CleanUpRetriedSourceSafely()
            {
                try { CleanUpRetriedSource(); }
                catch (Exception ex) { _logger.Log($"  Could not remove the original '{entry.FullName}' after the retried move: {ex.Message}", LogSeverity.Error); }
            }

            // Still failing - leave it as MoveFailed, tried again on the next pass. Only
            // flagged as an Error (which is what keeps this pass's log file from being
            // discarded - see RunOrchestrator) the FIRST time this exact failure shows up, or
            // if it's changed since last poll - a real gap found live: an offline destination
            // retried every single poll otherwise logged Error every single poll too, and a
            // multi-hour outage meant a kept log file per poll for the whole outage, the same
            // file-proliferation problem all over again just gated on "error" instead of
            // "empty." Still logged either way so it's visible live in the Recent Log panel,
            // just at Info once it's confirmed to be the same still-unresolved problem.
            void NoteStillFailing(Exception ex)
            {
                // The journal marked the entry as being filed when the move began; the move did not happen, so it is a failed move again.
                if (entry.Status == ResumeStatus.FinishPending)
                {
                    entry.Status = ResumeStatus.MoveFailed;
                    entry.PendingCompanionVideoDestPath = null;
                    ResumeKeeping.ClearOutputIdentity(entry);
                }

                var isSameFailureAsLastPoll = entry.LastRetryFailureMessage == ex.Message;
                var severity = isSameFailureAsLastPoll ? LogSeverity.Info : LogSeverity.Error;
                var suffix = isSameFailureAsLastPoll ? " (still failing, same as last check)" : "";
                _logger.Log($"  Retried move for '{desiredFileName}' failed again: {ex.Message}{suffix}", severity);
                entry.LastRetryFailureMessage = ex.Message;
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
                // Same edition rule as the first attempt, worked out from the original source path (which is still recorded when the file itself is gone).
                var retryShowEdition = retryIsTv ? PlexTags.EditionOfSourceShowFolder(entry.FullName, inputPath, ContentClassifier.GetEpisodeInfo(desiredFileName).ShowName) : null;
                retryDestPath = _fileRouter.RouteFile(encodedFilePath, desiredFileName, retryIsTv, retryLibraries.TvShowBasePath, retryLibraries.MovieBasePath, config.Processing.MoveFiles, config.Processing.OnDestinationCollision, retryShowEdition,
                    new SourceProtection(entry.FullName, config.Processing.DeleteAfterConvert, ResumeKeeping.SourceUnchangedSinceEncode(entry, encodedFilePath) && !sourceOutsideInput, _trash, ParkedChanged),
                    LaneKinds.IsVideos(retryLibraries.RedirectedTo ?? lane),
                    // The destination is journalled BEFORE the file is moved there (the same crash journal the first attempt uses): a stop between the move and the end of this
                    // retry is then found on the next start as a file that was filed, not as an encoded file that vanished.
                    beforeMove: destination =>
                    {
                        ResumeKeeping.NoteOutputIdentity(entry, encodedFilePath);
                        entry.Status = ResumeStatus.FinishPending;
                        entry.PendingCompanionVideoDestPath = destination;
                        entry.FiledInOutput = false;
                        ResumeResults.Save(_resumeStore, entry, resumeFilePath);
                    });
                ResolveRetry(retryDestPath ?? RestInPlace(encodedFilePath) ?? encodedFilePath);
                _logger.Log($"  Retried move for '{desiredFileName}' - succeeded.");
                CleanUpRetriedSourceSafely();

                if (retryDestPath is not null && !sourceOutsideInput)
                {
                    try
                    {
                        _companionFiles.MoveCompanionFiles(entry.FullName, originalSourceDirectory, retryDestPath, config.Processing.DeleteAfterConvert, config.Processing.CompanionExtensions, config.Processing.UnmatchedCompanionAction, config.Processing.OnDestinationCollision, config.Processing.VidTypes);
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
                // retry, so this is as resolved as it's going to get (unless resting it in Output fails: then it is still failing).
                _logger.Log($"  {ex.Message}");
                if (entry.Status == ResumeStatus.FinishPending) { entry.Status = ResumeStatus.MoveFailed; entry.PendingCompanionVideoDestPath = null; }
                try
                {
                    ResolveRetry(RestInPlace(encodedFilePath) ?? encodedFilePath);
                    CleanUpRetriedSourceSafely();
                }
                catch (Exception restFailure)
                {
                    NoteStillFailing(restFailure);
                }
            }
            catch (Exception ex)
            {
                NoteStillFailing(ex);
            }

            // *arr is told to stop monitoring only once disposition is final, same rule as
            // source cleanup above. This call also triggers Sonarr/Radarr's own library rescan and
            // waits for it to likely finish (see IArrUnmonitorService.UnmonitorAsync) - it must run
            // BEFORE the source folder is actually removed below, same reasoning as
            // ProcessOneFileAsync's own call site. A still-failing retry leaves this untouched too
            // - nothing to finalize yet.
            if (retrySucceeded)
            {
                arrCleanupSafe = (await _cleanup.ConfirmRescanAsync(config, desiredFileName, retryIsTv, " on move retry", logDeferred: true, cancellationToken, skipArr: LaneKinds.IsVideos(lane))).Safe;

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
                    cleanupFailed = _cleanup.TryCleanUpSourceFolder(config, originalSourceDirectory, inputPath, " on move retry", entry.FullName) is not null;
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

            // The original that was set aside for the replacement could not be disposed of after the output was filed: it stays on record and the entry stays open (with the output's identity), so the
            // next start finishes the job from that exact file. (A retry that failed and could not put the original back keeps its record too: it is still MoveFailed.)
            if (retrySucceeded && entry.ParkedOriginalPath is not null)
            {
                var published = retryDestPath ?? currentPath;
                ResumeKeeping.NoteOutputIdentity(entry, published);
                entry.Status = ResumeStatus.FinishPending;
                entry.PendingCompanionVideoDestPath = published;
                entry.FiledInOutput = retryDestPath is null;
                entry.PendingCompanionSourceDirectory = originalSourceDirectory;
                _logger.Log($"  The original '{Path.GetFileName(entry.FullName)}' could not be removed once the output was filed; it is kept at '{entry.ParkedOriginalPath}' and will be removed on the next pass.", LogSeverity.Error);
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
        var companionRetryEntries = resumeState.Where(e => e.LaneId == lane.Id && e.Status == ResumeStatus.CompanionMoveFailed && !ResumeKeeping.HoldsParkedOriginal(e)).ToList();
        foreach (var entry in companionRetryEntries)
        {
            var sourceDirectory = entry.PendingCompanionSourceDirectory!;
            var videoDestPath = entry.PendingCompanionVideoDestPath!;

            if (LeaveAloneOutsideInput(entry, sourceDirectory, "companion retry", resumeFilePath)) continue;

            try
            {
                // A file published in Output (see ResumeEntry.FiledInOutput) has no companions to move and no folder to tidy: only the Sonarr/Radarr step below is left.
                if (!entry.FiledInOutput)
                {
                    _companionFiles.MoveCompanionFiles(entry.FullName, sourceDirectory, videoDestPath, config.Processing.DeleteAfterConvert, config.Processing.CompanionExtensions, config.Processing.UnmatchedCompanionAction, config.Processing.OnDestinationCollision, config.Processing.VidTypes);
                    _logger.Log($"  Retried companion file move for '{Path.GetFileName(videoDestPath)}' - succeeded.");
                }

                var desiredFileName = Path.GetFileName(videoDestPath);
                var companionRetryIsTv = LaneKinds.IsTv(lane, desiredFileName);

                // Re-triggers the same unmonitor+rescan call the video's own first pass already
                // made - deliberately, not a bug: Sonarr/Radarr's own API already treats a repeat
                // call as a no-op ("already unmonitored - rescanned anyway"), and this is the only
                // way this retry can get a FRESH, positively-confirmed signal that it's now safe
                // to remove the source folder, without needing to persist the original pass's
                // rescan outcome across a run boundary.
                var arrCleanupSafe = (await _cleanup.ConfirmRescanAsync(config, desiredFileName, companionRetryIsTv, " on companion retry", logDeferred: true, cancellationToken, skipArr: LaneKinds.IsVideos(lane))).Safe;

                if (arrCleanupSafe)
                {
                    if (entry.FiledInOutput || _cleanup.TryCleanUpSourceFolder(config, sourceDirectory, inputPath, " on companion retry", entry.FullName) is null)
                    {
                        ResumeKeeping.MarkCompleted(entry, config.Processing.DeleteAfterConvert);
                        entry.PendingCompanionSourceDirectory = null;
                        entry.PendingCompanionVideoDestPath = null;
                        ResumeKeeping.ClearOutputIdentity(entry);
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
                    // for it, no change needed there. A file published in Output has no folder to tidy: it simply stays here until the rescan is confirmed.
                    if (!entry.FiledInOutput) entry.Status = ResumeStatus.CleanupPending;
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
        var cleanupPendingEntries = resumeState.Where(e => e.LaneId == lane.Id && e.Status == ResumeStatus.CleanupPending && !ResumeKeeping.HoldsParkedOriginal(e)).ToList();
        foreach (var entry in cleanupPendingEntries)
        {
            var sourceDirectory = entry.PendingCompanionSourceDirectory!;
            var videoDestPath = entry.PendingCompanionVideoDestPath!;

            if (LeaveAloneOutsideInput(entry, sourceDirectory, "cleanup retry", resumeFilePath)) continue;

            // Already gone - by hand, or some other process - since the deferral. Unlike a dead
            // CompanionMoveFailed entry (companions genuinely lost), this is the actual goal
            // already achieved: no folder left to clean up, and no need to spend an *arr API call
            // confirming a rescan for cleanup that's already moot.
            var folderPresence = FileOutcomeHelpers.PresenceOf(sourceDirectory);
            if (folderPresence == FileOutcomeHelpers.FilePresence.Unknown)
            {
                _logger.Log($"  Source folder '{sourceDirectory}' cannot be checked right now - its cleanup is left for the next pass.", LogSeverity.Error);
                continue;
            }

            if (folderPresence == FileOutcomeHelpers.FilePresence.Absent)
            {
                ResumeKeeping.MarkCompleted(entry, config.Processing.DeleteAfterConvert);
                entry.PendingCompanionSourceDirectory = null;
                entry.PendingCompanionVideoDestPath = null;
                entry.LastRetryFailureMessage = null;
                ResumeResults.Save(_resumeStore, entry, resumeFilePath);
                continue;
            }

            var desiredFileName = Path.GetFileName(videoDestPath);
            var cleanupRetryIsTv = LaneKinds.IsTv(lane, desiredFileName);

            // logDeferred is off: this loop logs its own, deduplicated "still deferred" line below.
            var rescan = await _cleanup.ConfirmRescanAsync(config, desiredFileName, cleanupRetryIsTv, " on cleanup retry", logDeferred: false, cancellationToken, skipArr: LaneKinds.IsVideos(lane));
            var arrCleanupSafe = rescan.Safe;
            var reasonKey = rescan.Outcome?.ToString() ?? rescan.FailureMessage!;

            if (arrCleanupSafe)
            {
                // context is null: this loop logs a failure itself, below, with its own Error-once/Info-on-repeat dedup.
                var cleanupError = _cleanup.TryCleanUpSourceFolder(config, sourceDirectory, inputPath, null, entry.FullName);
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

        // A pending companion move or folder cleanup whose folder is no longer inside this lane's Input (the setting changed since) is not Compressarr's to touch: the
        // entry is finished with a note, and the folder and its files are left exactly as they are.
        bool LeaveAloneOutsideInput(ResumeEntry entry, string sourceDirectory, string context, string resumeFilePath)
        {
            // The video was a file inside Input (so its folder is Input itself or below it); anything else is not ours to touch.
            if (PathContainment.IsStrictlyInside(entry.FullName, inputPath) && PathContainment.IsInsideOrSame(sourceDirectory, inputPath)) return false;

            _logger.Log($"  '{sourceDirectory}' is not inside this lane's Input folder, so its {context} was dropped and it was left as it is.");
            ResumeKeeping.MarkCompleted(entry, config.Processing.DeleteAfterConvert);
            entry.PendingCompanionSourceDirectory = null;
            entry.PendingCompanionVideoDestPath = null;
            entry.LastRetryFailureMessage = null;
            ResumeResults.Save(_resumeStore, entry, resumeFilePath);
            return true;
        }
    }

    /// <summary>
    /// Resolves the originals that were PARKED for a same-path replacement (<see cref="ResumeEntry.ParkedOriginalPath"/>), before anything else is picked up. It acts on exactly the file the journal
    /// names - never on a file that merely has a similar name - and only when that file still is the original:
    ///  - the output is published and is exactly this job's (size and content digest): the parked original is deleted or recycled, as the setting says, if it still matches what was converted;
    ///  - otherwise the original is put back at its own path (the replacement did not happen), unless something else is there now;
    ///  - anything that cannot be shown to be the original, or a setting that now keeps originals, leaves the file where it is and says so.
    /// A record whose file is gone is simply cleared.
    /// </summary>
    private void ResolveParkedOriginals(LaneConfig lane, CompressarrConfig config, List<ResumeEntry> resumeState, string resumeFilePath)
    {
        // THE RULE: the record (ParkedOriginalPath) is cleared only on DEFINITE evidence - the file is verifiably absent, was verifiably removed, was put back, or is positively known not to be what
        // this job converted / the output is positively not this job's. Whenever anything could not be examined (a path that cannot be checked, content that cannot be read: a lock, access denied, a
        // share that dropped) the record, the output's identity and the entry stay exactly as they are and the next pass looks again. Every clear below says which evidence allows it.
        foreach (var entry in resumeState.Where(e => e.LaneId == lane.Id && !string.IsNullOrEmpty(e.ParkedOriginalPath)).ToList())
        {
            var parked = entry.ParkedOriginalPath!;
            var name = Path.GetFileName(entry.FullName);
            var parkedFolder = Path.GetDirectoryName(parked)!;
            var policy = config.Processing.DeleteAfterConvert;

            void NotYet(string why) =>
                _logger.Log($"'{name}': the original set aside at '{parked}' could not be examined right now ({why}) - it is left on record, and looked at again on the next pass.", LogSeverity.Error);

            var parkedPresence = FileOutcomeHelpers.PresenceOf(parked);
            if (parkedPresence == FileOutcomeHelpers.FilePresence.Unknown) { NotYet("its path cannot be checked"); continue; }

            if (parkedPresence == FileOutcomeHelpers.FilePresence.Absent)
            {
                entry.ParkedOriginalPath = null;                      // CLEAR: verifiably not there - put back or disposed of already
                FileOutcomeHelpers.TryRemoveEmptyFolder(parkedFolder);
                ResumeResults.Save(_resumeStore, entry, resumeFilePath);
                continue;
            }

            var output = string.IsNullOrEmpty(entry.PendingCompanionVideoDestPath) ? ResumeKeeping.OutputCheck.Missing : ResumeKeeping.CheckOutput(entry, entry.PendingCompanionVideoDestPath!);
            if (output == ResumeKeeping.OutputCheck.Indeterminate) { NotYet("the published output cannot be checked"); continue; }

            if (output == ResumeKeeping.OutputCheck.Matches)
            {
                // The output is filed and is exactly this job's: the original that was waiting can go - if it is still the file that was converted.
                var parkedCheck = ResumeKeeping.SourceCheck.Changed;
                if (policy != DeleteAfterConvertMode.Maintain)
                {
                    var (check, indeterminate) = ResumeKeeping.CheckSourceFileDetailed(entry, parked);
                    if (indeterminate) { NotYet("it cannot be read"); continue; }
                    parkedCheck = check;
                }

                if (parkedCheck == ResumeKeeping.SourceCheck.Unchanged)
                {
                    try
                    {
                        // CLEAR only when the file is verifiably gone: the trash service logs a failed removal and returns normally.
                        if (FileOutcomeHelpers.DisposeSource(_trash, parked, policy))
                        {
                            FileOutcomeHelpers.TryRemoveEmptyFolder(parkedFolder);
                            entry.ParkedOriginalPath = null;
                            _logger.Log($"'{name}': the original that was waiting to be removed after its replacement was filed has been removed.");
                        }
                        else _logger.Log($"'{name}': the original kept at '{parked}' could not be removed yet (it is tried again on the next pass).", LogSeverity.Error);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.Log($"'{name}': the original kept at '{parked}' could not be removed yet: {ex.Message}", LogSeverity.Error);
                    }
                }
                else
                {
                    // CLEAR: the file was examined and the answer is definite - it is positively not what was converted (Changed), nothing was ever noted about it so it can never be shown to be (a record
                    // from an earlier build), or the setting keeps originals. It stays where it is, named in the log, for the user.
                    entry.ParkedOriginalPath = null;
                    _logger.Log($"'{name}': the original set aside at '{parked}' was left there (it could not be confirmed to be the file that was converted, or originals are now kept) - it is yours to keep or delete.", LogSeverity.Error);
                }
            }
            else
            {
                // The output is not (confirmed to be) here: Missing, Different (positively another file) or Unrecorded (a record from an earlier build). The original goes back if its own path is free.
                var home = FileOutcomeHelpers.PresenceOf(entry.FullName);
                if (home == FileOutcomeHelpers.FilePresence.Unknown) { NotYet($"'{entry.FullName}' cannot be checked"); continue; }

                if (home == FileOutcomeHelpers.FilePresence.Absent)
                {
                    try
                    {
                        File.Move(parked, entry.FullName);
                        FileOutcomeHelpers.TryRemoveEmptyFolder(parkedFolder);
                        entry.ParkedOriginalPath = null;                  // CLEAR: moved back
                        _logger.Log($"'{name}' was interrupted while its original was being replaced: the original was put back at '{entry.FullName}'.");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.Log($"'{name}': the original kept at '{parked}' could not be put back at '{entry.FullName}': {ex.Message}", LogSeverity.Error);
                    }
                }
                else
                {
                    // CLEAR: a file is at the original's path and it was positively read as not this job's output (the output check above was not Indeterminate), so it is someone else's.
                    entry.ParkedOriginalPath = null;
                    _logger.Log($"'{name}': another file is now at '{entry.FullName}', so the original set aside at '{parked}' was left there - it is yours to keep or delete.", LogSeverity.Error);
                }
            }

            ResumeResults.Save(_resumeStore, entry, resumeFilePath);
        }
    }

    /// <summary>
    /// Picks up the files whose filing was interrupted (see <see cref="ResumeStatus.FinishPending"/>). The journal holds the staged encode (EncodedFilePath), the destination it was about to be
    /// moved to (PendingCompanionVideoDestPath, written BEFORE the move) and what the output is (size and time). From those, on the next start:
    ///  - the staged file is still there: the move did not finish; it is routed again (as for a failed move). A file at the planned destination that is the same output (a copy across drives that
    ///    stopped before the staged file was deleted) only has its leftover removed.
    ///  - the staged file is gone and a destination was journalled: the file is only taken to be filed if the destination still has the output's size and time. Anything else there - another
    ///    file, nothing, a destination that cannot be read, a journal without the note - means nothing can be shown, so the original is kept, never deleted, and not converted again.
    ///  - the staged file is gone and nothing was journalled: it was removed by hand; the source is converted again if it is still in Input.
    /// A filed video has its source dealt with the way the setting says (only when it is still the file that was converted, and inside Input; one whose identity is unknown is kept AND noted
    /// as kept), then goes to the companion path, which moves the companions, confirms the Sonarr/Radarr rescan and tidies the folder. Nothing is encoded again.
    /// </summary>
    private void ResolveInterruptedFinishes(LaneConfig lane, CompressarrConfig config, string inputPath, List<ResumeEntry> resumeState, string resumeFilePath)
    {
        var interrupted = resumeState.Where(e => e.LaneId == lane.Id && e.Status == ResumeStatus.FinishPending && !ResumeKeeping.HoldsParkedOriginal(e)).ToList();
        foreach (var entry in interrupted)
        {
            var name = Path.GetFileName(entry.FullName);
            var staged = entry.EncodedFilePath;
            var journalled = entry.PendingCompanionVideoDestPath;
            var stagedPresence = string.IsNullOrEmpty(staged) ? FileOutcomeHelpers.FilePresence.Absent : FileOutcomeHelpers.PresenceOf(staged);
            if (stagedPresence == FileOutcomeHelpers.FilePresence.Unknown)
            {
                // The staged file cannot be checked (a share that dropped, access denied): concluding it is gone would drop or re-convert the entry. It stays as it is for the next pass.
                _logger.Log($"'{name}' was interrupted while it was being filed, but its encoded file '{staged}' cannot be checked right now - left for the next pass.", LogSeverity.Error);
                continue;
            }

            var stagedExists = stagedPresence == FileOutcomeHelpers.FilePresence.Present;
            var hasDestination = !string.IsNullOrEmpty(journalled);

            if (stagedExists)
            {
                var atDestination = hasDestination ? ResumeKeeping.CheckOutput(entry, journalled!) : ResumeKeeping.OutputCheck.Missing;
                if (atDestination == ResumeKeeping.OutputCheck.Indeterminate)
                {
                    _logger.Log($"'{name}' was interrupted while it was being moved, but what is at '{journalled}' cannot be examined right now - left for the next pass.", LogSeverity.Error);
                    continue;
                }

                if (atDestination == ResumeKeeping.OutputCheck.Matches)
                {
                    // A move across drives copies and then deletes the staged file; the stop came between the two. The copy is complete (same size and time): only the leftover goes.
                    try
                    {
                        File.Delete(staged!);
                        _logger.Log($"'{name}' was interrupted while it was being moved: the filed copy at '{journalled}' is complete, so the staged file was removed.");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.Log($"'{name}' was filed at '{journalled}', but the staged file '{staged}' could not be removed: {ex.Message}", LogSeverity.Error);
                    }
                }
                else
                {
                    // The move did not finish: route the encoded file again. Whatever is at the old destination is not this output (a partial copy, or a file that came meanwhile): it is left
                    // as it is, and the destination setting (overwrite, rename, skip) decides what happens to it.
                    if (atDestination != ResumeKeeping.OutputCheck.Missing)
                        _logger.Log($"'{name}' was interrupted while it was being moved, and a different file (or a partial copy) is at '{journalled}'. It was left as it is; the encoded file is routed again under the destination setting.", LogSeverity.Error);
                    else
                        _logger.Log($"Finishing '{name}', which was interrupted before it was filed - routing the encoded file now (no re-encode).");
                    entry.PendingCompanionVideoDestPath = null;
                    ResumeKeeping.ClearOutputIdentity(entry);
                    entry.Status = ResumeStatus.MoveFailed;
                    ResumeResults.Save(_resumeStore, entry, resumeFilePath);
                    continue;
                }
            }
            else if (!hasDestination)
            {
                _logger.Log($"'{name}' was interrupted while it was being filed and its encoded file is no longer there - it will be converted again if its source is still in Input.", LogSeverity.Error);
                resumeState.Remove(entry);
                ResumeResults.Remove(_resumeStore, new[] { entry }, resumeFilePath);
                continue;
            }
            else
            {
                // The staged file is gone and a destination was journalled: the move may have happened. Only a destination that is still the encoded output shows it.
                var check = ResumeKeeping.CheckOutput(entry, journalled!);
                if (check == ResumeKeeping.OutputCheck.Indeterminate)
                {
                    _logger.Log($"'{name}' was interrupted while it was being filed, but what is at '{journalled}' cannot be examined right now - left for the next pass.", LogSeverity.Error);
                    continue;
                }

                if (check != ResumeKeeping.OutputCheck.Matches)
                {
                    var what = check switch
                    {
                        ResumeKeeping.OutputCheck.Missing => $"the filed video is no longer at '{journalled}'",
                        ResumeKeeping.OutputCheck.Unrecorded => $"what is at '{journalled}' cannot be confirmed to be the encoded file (the interruption was recorded by an earlier version)",
                        _ => $"what is at '{journalled}' is not the encoded file"
                    };
                    _logger.Log($"'{name}' was interrupted while it was being filed, and {what}, so the original was kept as it is and nothing was deleted.", LogSeverity.Error);
                    ResumeKeeping.NoteSourceKept(entry);
                    entry.EncodedFilePath = null;
                    entry.EncodedFileDesiredName = null;
                    entry.PendingCompanionSourceDirectory = null;
                    entry.PendingCompanionVideoDestPath = null;
                    ResumeKeeping.ClearOutputIdentity(entry);
                    entry.Status = ResumeStatus.Completed;
                    ResumeResults.Save(_resumeStore, entry, resumeFilePath);
                    continue;
                }
            }

            // The video is in its library (confirmed above): only the aftermath is left.
            _logger.Log($"Finishing '{name}', which was interrupted after it was filed - dealing with its original, companions and cleanup now (no re-encode).");
            var policy = config.Processing.DeleteAfterConvert;
            var sameAsRouted = string.Equals(entry.FullName, journalled, StringComparison.OrdinalIgnoreCase);
            if (policy != DeleteAfterConvertMode.Maintain && !sameAsRouted && File.Exists(entry.FullName) && PathContainment.IsStrictlyInside(entry.FullName, inputPath))
            {
                var sourceCheck = ResumeKeeping.CheckSource(entry);
                if (sourceCheck == ResumeKeeping.SourceCheck.Unchanged)
                {
                    if (!FileOutcomeHelpers.DisposeSource(_trash, entry.FullName, policy))
                    {
                        ResumeKeeping.NoteSourceKept(entry);
                        _logger.Log($"  The original '{name}' could not be removed, so it was kept (delete it yourself).", LogSeverity.Error);
                    }
                }
                else if (sourceCheck == ResumeKeeping.SourceCheck.Unknown)
                {
                    // Kept for the user, and noted as converted (as the ordinary finisher does), so a scan does not take it for new work and convert it a second time.
                    ResumeKeeping.NoteSourceKept(entry);
                    _logger.Log($"  The original '{name}' could not be confirmed to be the file that was converted, so it was kept (delete it yourself if it is).");
                }
                else _logger.Log($"  The original '{name}' is not the file that was converted (it changed since), so it was kept: it is queued again as new work.");
            }

            entry.EncodedFilePath = null;
            entry.EncodedFileDesiredName = null;
            // The output's identity is spent (it was verified above); FiledInOutput stays, so a file published in Output skips the companion move and the folder cleanup.
            entry.EncodedFileSize = null;
            entry.EncodedFileDigest = null;
            entry.Status = ResumeStatus.CompanionMoveFailed;
            entry.PendingCompanionSourceDirectory ??= Path.GetDirectoryName(entry.FullName);
            ResumeResults.Save(_resumeStore, entry, resumeFilePath);
        }
    }
}
