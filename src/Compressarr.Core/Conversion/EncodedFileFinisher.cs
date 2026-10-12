using Compressarr.Core.Config;
using Compressarr.Core.Logging;
using Compressarr.Core.Queue;
using Compressarr.Core.Reporting;
using Compressarr.Core.Routing;
using static Compressarr.Core.Conversion.FileOutcomeHelpers;

namespace Compressarr.Core.Conversion;

/// <summary>Everything the finishing step needs to know about one successfully encoded file.</summary>
/// <param name="Entry">The file's resume entry; its Status (and the Encoded*/Pending* fields) are set here.</param>
/// <param name="TempFileName">Where the encoder wrote the result - the file's collision-safe staging name in Output.</param>
/// <param name="NewFileName">The nominal, human-readable name the file will rest under or be routed as.</param>
/// <param name="DestFolder">The folder a file that is not routed rests in (the lane's Output, or the source folder).</param>
/// <param name="TvShowBasePath">The file's OWN lane's already-expanded TV library (a per-file lane assignment overrides it).</param>
/// <param name="MovieBasePath">The file's own lane's already-expanded Movie library.</param>
/// <param name="InputPath">The file's own lane's Input root - the source-cleanup boundary, never the destination lane's.</param>
internal sealed record FinishRequest(
    CompressarrConfig Config,
    ResumeEntry Entry,
    FileInfo File,
    bool IsTv,
    string TempFileName,
    string NewFileName,
    string DestFolder,
    string TvShowBasePath,
    string MovieBasePath,
    string InputPath);

/// <summary>What finishing a file produced, for the orchestrator to put into the file's result.</summary>
internal sealed record FinishOutcome(
    double EndSizeGb,
    string? ArrStatus,
    string FinalFileName,
    bool MoveFailed,
    bool DiskFull,
    string? FailureReason,
    ReportErrorCode? ErrorCode,
    string? PostProcessWarning,
    LaneConfig? RedirectedTo);

/// <summary>
/// The part of processing one file that happens after HandBrake succeeded: clear the title tag, route the
/// finished file into its library (or rest it in Output), remove the source, move its companion files, tell
/// Sonarr/Radarr, remove the now-empty source folder, and record where that left the file (Completed /
/// MoveFailed / CompanionMoveFailed / CleanupPending). Split out of ConversionOrchestrator.ProcessOneFileAsync,
/// where it was over half the method; the code moved as-is - same order, same logging, same status rules - and
/// is covered by the same tests. A failed encode never reaches here.
/// </summary>
internal sealed class EncodedFileFinisher
{
    private const long BytesPerGb = 1024L * 1024L * 1024L;

    private readonly IPathExpander _pathExpander;
    private readonly IMetadataService _metadata;
    private readonly IFileRouter _fileRouter;
    private readonly ICompanionFileService _companionFiles;
    private readonly ITrashService _trash;
    private readonly IRunLogger _logger;
    private readonly SourceCleanupCoordinator _cleanup;

    public EncodedFileFinisher(
        IPathExpander pathExpander,
        IMetadataService metadata,
        IFileRouter fileRouter,
        ICompanionFileService companionFiles,
        ITrashService trash,
        IRunLogger logger,
        SourceCleanupCoordinator cleanup)
    {
        _pathExpander = pathExpander;
        _metadata = metadata;
        _fileRouter = fileRouter;
        _companionFiles = companionFiles;
        _trash = trash;
        _logger = logger;
        _cleanup = cleanup;
    }

    public async Task<FinishOutcome> FinishAsync(FinishRequest request, CancellationToken cancellationToken)
    {
        var config = request.Config;
        var resumeEntry = request.Entry;
        var file = request.File;
        var isTv = request.IsTv;
        var tempFileName = request.TempFileName;
        var newFileName = request.NewFileName;
        var destFolder = request.DestFolder;
        var tvShowBasePath = request.TvShowBasePath;
        var movieBasePath = request.MovieBasePath;
        var inputPath = request.InputPath;

        double endSizeGb = 0;
        string? arrStatus = null;
        string? finalFileName = newFileName;
        var moveFailed = false;
        var diskFull = false;
        LaneConfig? redirectedTo = null;
        string? failureReason = null;
        ReportErrorCode? errorCode = null;
        string? postProcessWarning = null;

        _metadata.ClearTitle(tempFileName);
        endSizeGb = Math.Round(new FileInfo(tempFileName).Length / (double)BytesPerGb, 3);

        // The human-readable name content classification (season/episode, movie title) and
        // this file's own eventual resting/routed leaf name are derived FROM - independent of
        // wherever the file's bytes physically live right now (tempFileName's own
        // collision-safe GUID name). See the "moveFailed" branch below for why the two must
        // stay independent for as long as this file's final disposition is still unresolved.
        var desiredFileName = Path.GetFileName(newFileName);

        // Resolves the SAME OnDestinationCollision policy real routing already gets before
        // resting the file in Output under its clean human-readable name - previously this
        // always overwrote unconditionally, regardless of what the user configured, so two
        // files that happened to both rest in Output under the same name (MoveFiles off, or a
        // routing-level Skip - see below) could silently clobber each other even with
        // Rename/Skip configured. Returns null (stay at physicalPath, under its current
        // collision-safe temp name) when Skip applies to a real collision - the caller falls
        // back to physicalPath itself in that case, matching how a routing-level Skip already
        // leaves a file at tempFileName rather than renaming it.
        string? RestInPlace(string physicalPath, string folder)
        {
            var desiredDestPath = Path.Combine(folder, desiredFileName);
            string resolvedDestPath;
            try
            {
                resolvedDestPath = FileRouter.ResolveCollision(desiredDestPath, config.Processing.OnDestinationCollision);
            }
            catch (DestinationCollisionSkippedException ex)
            {
                postProcessWarning = AppendWarning(postProcessWarning, ex.Message);
                _logger.Log($"  {ex.Message}");
                return null;
            }
            File.Move(physicalPath, resolvedDestPath, overwrite: true);
            return resolvedDestPath;
        }

        var currentPath = tempFileName;
        string? routedDestPath = null;
        try
        {
            var libraries = LaneDestination.Resolve(config, _pathExpander, resumeEntry, tvShowBasePath, movieBasePath);
            routedDestPath = _fileRouter.RouteFile(tempFileName, desiredFileName, isTv, libraries.TvShowBasePath, libraries.MovieBasePath, config.Processing.MoveFiles, config.Processing.OnDestinationCollision);
            // Only a file that really reached a library counts as redirected - one resting in Output
            // (MoveFiles off, a Skip) or stuck on a failed move landed nowhere yet.
            if (routedDestPath is not null) redirectedTo = libraries.RedirectedTo;
            currentPath = routedDestPath is not null
                ? routedDestPath
                // MoveFiles is false - nothing further to route to, so unlike the moveFailed
                // branch below, this already IS the file's final resting place and needs its
                // proper human-readable name now, not the collision-safe staging one.
                : RestInPlace(tempFileName, destFolder) ?? tempFileName;
        }
        catch (DestinationCollisionSkippedException ex)
        {
            // Configured to skip on collision, not an error - the encode succeeded and the
            // result is exactly where it's meant to end up (Output). Same "final disposition
            // reached" treatment as the MoveFiles=false case above - nothing will ever revisit
            // this entry again, so it needs its clean resting name now too (or, if Output ALSO
            // has a real collision on that name, RestInPlace falls back to leaving it under
            // tempFileName rather than clobbering whatever's already there).
            postProcessWarning = AppendWarning(postProcessWarning, ex.Message);
            _logger.Log($"  {ex.Message}");
            currentPath = RestInPlace(tempFileName, destFolder) ?? tempFileName;
        }
        catch (Exception ex)
        {
            // The conversion itself already succeeded - a bad/unreachable base path (e.g. an
            // offline network drive) shouldn't take down the whole run, just leave the file
            // where it currently is. Still flagged as an error on this file's own result (see
            // moveFailed below) so it doesn't quietly report "OK" while sitting unrouted.
            //
            // Deliberately NOT renamed to its human-readable name here, unlike every other
            // branch above - this is the one outcome PrepareLaneAsync's MoveFailed handling
            // will revisit later, possibly after a long wait for the destination to come back.
            // Two different attempts for the same title (a stuck retry sitting alongside a
            // fresh encode of the same source re-added to the queue, e.g.) must never be able
            // to collide on the same deterministic Output filename in the meantime -
            // tempFileName's own GUID suffix already guarantees that, for as long as it takes.
            // currentPath stays at tempFileName; only the earlier (fixed) bug used to rename it
            // to its deterministic, collidable name unconditionally before routing even ran.
            moveFailed = true;
            if (LooksLikeDiskFull(ex.Message))
            {
                diskFull = true;
                failureReason = "Output drive full, monitoring stopped";
                errorCode = ReportErrorCode.MoveDiskFull;
            }
            else if (ex is DestinationLaneMissingException)
            {
                failureReason = "Destination lane no longer exists, move skipped";
                errorCode = ReportErrorCode.MoveFailedOther;
            }
            else if (LooksLikePathUnavailable(ex))
            {
                failureReason = "Base folder path unavailable, move skipped";
                errorCode = ReportErrorCode.MoveDestinationUnavailable;
            }
            else
            {
                // Some other move failure (permission denied, invalid credentials, a locked
                // file, etc.) - leave failureReason null so the plain-text log still shows
                // generic "ERROR" rather than a path-unavailable message that would be actively
                // wrong for this cause, but the report still gets its own numbered code.
                errorCode = ReportErrorCode.MoveFailedOther;
            }
            _logger.Log($"  Move skipped: {ex.Message} - file remains at '{tempFileName}'.", LogSeverity.Error);
        }

        finalFileName = currentPath;

        // If source and final resting place are the same path (in-place conversion, only
        // possible when writing output back into the Input folder with no further routing),
        // the move above already replaced the original with the converted result - there is
        // nothing left to separately delete.
        var sameAsSource = string.Equals(file.FullName, currentPath, StringComparison.OrdinalIgnoreCase);

        // Only clean up the original source once the encoded file has genuinely reached its
        // final resting place - either successfully routed, or resting in Output under its own
        // clean name (MoveFiles off, or a configured Skip). A real routing failure (moveFailed)
        // leaves the source untouched: if the destination is offline or unreachable, the
        // original is what you're left with until routing succeeds - not gone before anyone
        // knows whether the move will ever work. A later successful retry (PrepareLaneAsync's
        // MoveFailed handling) cleans it up once it actually succeeds.
        //
        // Deliberately BEFORE the companion-file move below, not after: a companion belongs
        // alongside its video, and the video's own source copy needs to already be gone for
        // the folder-emptiness check further below (CleanUpEmptySourceFolder, called much
        // later - see there for why) to eventually find this folder truly empty.
        if (!moveFailed && !sameAsSource && config.Processing.DeleteAfterConvert != DeleteAfterConvertMode.Maintain)
        {
            if (File.Exists(file.FullName))
            {
                var attrs = File.GetAttributes(file.FullName);
                if (attrs.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(file.FullName, attrs & ~FileAttributes.ReadOnly);
                }
            }
            _trash.DeleteFile(file.FullName, config.Processing.DeleteAfterConvert);
        }

        // Only attempted when the video actually reached a routed (TV/Movie library)
        // destination - a file just resting in Output has no separate "companions folder" to
        // move anything alongside, and if moveFailed, there's nothing to move companions
        // alongside yet; they'll go together once a later retry (PrepareLaneAsync's MoveFailed
        // handling) actually succeeds.
        var companionMoveFailed = false;
        if (routedDestPath is not null)
        {
            try
            {
                _companionFiles.MoveCompanionFiles(file.FullName, file.DirectoryName!, routedDestPath, config.Processing.DeleteAfterConvert, config.Processing.CompanionExtensions, config.Processing.UnmatchedCompanionAction, config.Processing.OnDestinationCollision);
            }
            catch (Exception ex)
            {
                // A companion that failed to move (locked file, permission error, etc.) is
                // still sitting in the source folder - companionMoveFailed suppresses the
                // folder cleanup below so it can't be swept up as an "unmatched leftover" and
                // deleted/recycled. A real data-loss edge case found via code review, not live.
                companionMoveFailed = true;
                _logger.Log($"  Companion file handling skipped: {ex.Message}", LogSeverity.Error);
                postProcessWarning = AppendWarning(postProcessWarning, $"Companion files not moved: {ex.Message}");
            }
        }

        // Tell Sonarr/Radarr to stop monitoring this file only once its final disposition is
        // settled (same !moveFailed rule as source cleanup above) - not silently dropped from
        // *arr's tracking for a file that never actually finished landing anywhere. This call
        // also triggers Sonarr/Radarr's own library rescan and waits for it to likely finish
        // (see IArrUnmonitorService.UnmonitorAsync) - it must run BEFORE the source folder is
        // actually removed below: reported live, if the folder is already gone when the rescan
        // runs, Sonarr/Radarr reads it as a disconnected root and never clears the episode; if
        // the folder is still there but empty, it correctly treats the episode as missing.
        var arrCleanupSafe = true;
        var cleanupFailed = false;
        if (!moveFailed)
        {
            var rescan = await _cleanup.ConfirmRescanAsync(config, file.Name, isTv, "", logDeferred: true, cancellationToken);
            arrCleanupSafe = rescan.Safe;
            if (rescan.FailureMessage is not null)
            {
                arrStatus = $"Failed: {rescan.FailureMessage}";
                postProcessWarning = AppendWarning(postProcessWarning, $"Sonarr/Radarr unmonitor failed: {rescan.FailureMessage}");
            }
            else
            {
                if (rescan.Message is not null) arrStatus = rescan.Message;
                if (!rescan.Safe) postProcessWarning = AppendWarning(postProcessWarning, "Source folder cleanup deferred until the next pass (rescan not confirmed complete)");
            }
        }

        // Only now, after Sonarr/Radarr has been given the chance to rescan the source folder
        // WHILE it still physically exists AND that rescan was positively confirmed to finish
        // (see the comment above and ArrRescanOutcome/SafeToCleanUp), is it actually safe to
        // remove it if it's now empty - real regression this whole split guards against:
        // deleting the folder any earlier made a subsequent rescan see a disconnected root
        // instead of a genuinely-empty one, and Sonarr/Radarr would never clear the episode. A
        // rescan that failed, timed out, or was cancelled mid-wait can't prove it ever saw the
        // folder while it existed, so cleanup is left for a later pass instead of guessing.
        // Skipped entirely if the companion move itself failed above - the folder isn't
        // actually empty of "belongs here" content in that case, it's got a stranded
        // companion still waiting to be moved, not a genuine orphan to sweep.
        if (routedDestPath is not null && !companionMoveFailed && arrCleanupSafe)
        {
            var cleanupError = _cleanup.TryCleanUpSourceFolder(config, file.DirectoryName!, inputPath, "");
            if (cleanupError is not null)
            {
                // Code-review finding: a filesystem-level cleanup failure (locked file,
                // permission, antivirus interference, etc) was logged here but otherwise
                // ignored - the entry still ended up Completed below regardless, so a
                // transient cleanup error was just as permanently forgotten as an unconfirmed
                // rescan used to be. Treated the same way now: falls through to CleanupPending
                // just like arrCleanupSafe == false does. Code-review follow-up finding: unlike
                // the sibling !arrCleanupSafe branch above, this didn't append a
                // PostProcessWarning - the report could show a plain, unqualified "OK" for a
                // file whose folder cleanup actually failed and is still pending retry.
                postProcessWarning = AppendWarning(postProcessWarning, $"Source folder cleanup deferred: {cleanupError}");
                cleanupFailed = true;
            }
        }

        // moveFailed here means the real-error branch above (not the Skip case, which never
        // sets moveFailed) - the file is genuinely stuck unrouted in Output. MoveFailed (not
        // Completed) so the retry logic in this lane's next PrepareLane call picks it back up
        // instead of a resume entry silently lying that this file is fully done. A companion
        // move failure gets the same "not actually done" treatment via CompanionMoveFailed -
        // the video itself IS filed correctly, only its companions are stranded, so this lane's
        // next PrepareLane call retries just the companion move (see the companion-retry loop
        // there), not a full re-route. Code-review finding: a deferred cleanup (arrCleanupSafe
        // false, but nothing else wrong) used to fall straight through to Completed here too -
        // accurate about the video/companions, but it meant NOTHING ever came back to retry
        // the *arr confirmation and finish the cleanup, so an unconfirmed rescan could leave an
        // empty source folder behind forever. CleanupPending tracks exactly that remaining
        // work, retried by its own loop in PrepareLaneAsync (whether the rescan wasn't
        // confirmed, or the cleanup call itself threw once it was - see cleanupFailed above).
        if (moveFailed)
        {
            resumeEntry.Status = ResumeStatus.MoveFailed;
            resumeEntry.EncodedFilePath = currentPath;
            resumeEntry.EncodedFileDesiredName = desiredFileName;
        }
        else if (companionMoveFailed)
        {
            resumeEntry.Status = ResumeStatus.CompanionMoveFailed;
            resumeEntry.PendingCompanionSourceDirectory = file.DirectoryName;
            resumeEntry.PendingCompanionVideoDestPath = routedDestPath;
        }
        else if (routedDestPath is not null && (!arrCleanupSafe || cleanupFailed))
        {
            resumeEntry.Status = ResumeStatus.CleanupPending;
            resumeEntry.PendingCompanionSourceDirectory = file.DirectoryName;
            resumeEntry.PendingCompanionVideoDestPath = routedDestPath;
        }
        else
        {
            resumeEntry.Status = ResumeStatus.Completed;
            ResumeKeeping.NoteKeptSource(resumeEntry, config.Processing.DeleteAfterConvert);
        }

        return new FinishOutcome(endSizeGb, arrStatus, finalFileName ?? newFileName, moveFailed, diskFull, failureReason, errorCode, postProcessWarning, redirectedTo);
    }
}
