using Compressarr.Core.Arr;
using Compressarr.Core.Config;
using Compressarr.Core.Logging;
using Compressarr.Core.Routing;

namespace Compressarr.Core.Conversion;

/// <summary>What asking Sonarr/Radarr to rescan came back with. <see cref="Safe"/> is the only thing the
/// caller needs to decide whether removing the source folder is allowed.</summary>
/// <param name="Safe">The rescan was positively confirmed complete (or *arr isn't in use), so an empty
/// source folder may be removed. False for anything else, including a call that threw.</param>
/// <param name="Message">*arr's own status line ("unmonitored the matching movie..."), when it gave one.</param>
/// <param name="Outcome">The rescan outcome; null if the call itself threw.</param>
/// <param name="FailureMessage">The exception message when the call threw; otherwise null.</param>
internal sealed record RescanConfirmation(bool Safe, string? Message, ArrRescanOutcome? Outcome, string? FailureMessage);

/// <summary>
/// The "tell Sonarr/Radarr, confirm the rescan, only then remove the empty source folder" sequence that a
/// finished file goes through - used by the first attempt (ProcessOneFileAsync) and by all three kinds of
/// retry (RetryRecovery). The ORDER is the safety rule this app has been burned by before: if the source
/// folder is already gone when Sonarr/Radarr rescans, it reads as a disconnected root and never clears the
/// episode; and a rescan that never confirmed it finished can't prove it saw the folder at all. So the folder
/// is only removed after a positively-confirmed rescan, and a failure of either step is never swallowed -
/// callers keep the entry alive (CleanupPending) so it is retried.
///
/// Each caller used to carry its own copy of these two try/catch blocks, differing only in the wording of
/// the log lines and in what it did with the result; the wording is preserved through the
/// <c>context</c> argument so nothing the user reads has changed.
/// </summary>
internal sealed class SourceCleanupCoordinator
{
    private readonly IArrUnmonitorService _arrUnmonitor;
    private readonly ICompanionFileService _companionFiles;
    private readonly IRunLogger _logger;

    public SourceCleanupCoordinator(IArrUnmonitorService arrUnmonitor, ICompanionFileService companionFiles, IRunLogger logger)
    {
        _arrUnmonitor = arrUnmonitor;
        _companionFiles = companionFiles;
        _logger = logger;
    }

    /// <summary>Asks *arr to unmonitor the file and rescan, logging what it said. A thrown error is logged
    /// ("Arr unmonitor skipped{context}: ...") and reported as not safe - never rethrown, so one flaky *arr
    /// instance can't fail a file whose encode and move already succeeded.</summary>
    /// <param name="context">Appended to the failure line, e.g. " on move retry" (empty for the first attempt).</param>
    /// <param name="logDeferred">Whether to log "Source folder cleanup deferred..." when the rescan wasn't
    /// confirmed (the cleanup-retry loop logs its own, deduplicated, line instead).</param>
    public async Task<RescanConfirmation> ConfirmRescanAsync(
        CompressarrConfig config, string fileName, bool isTv, string context, bool logDeferred, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _arrUnmonitor.UnmonitorAsync(config, fileName, isTv, cancellationToken);
            if (result.Message is not null) _logger.Log($"  {result.Message}");
            if (!result.SafeToCleanUp && logDeferred)
            {
                _logger.Log($"  Source folder cleanup deferred - rescan was not positively confirmed complete ({result.Outcome}).");
            }
            return new RescanConfirmation(result.SafeToCleanUp, result.Message, result.Outcome, null);
        }
        catch (Exception ex)
        {
            _logger.Log($"  Arr unmonitor skipped{context}: {ex.Message}", LogSeverity.Error);
            return new RescanConfirmation(false, null, null, ex.Message);
        }
    }

    /// <summary>Removes the source folder if it is now empty (the cascade stops at <paramref name="inputPath"/>,
    /// always the file's OWN lane's Input root). Returns the failure message if it threw, or null on success.
    /// A failure is logged as "Source folder cleanup skipped{context}: ..." unless <paramref name="context"/>
    /// is null, in which case the caller logs it itself.
    /// <paramref name="videoPath"/> is the converted video's original path (the file itself may be gone by now): a wanted companion of it that is still in the folder was
    /// not moved (the destination already had one and the collision setting is Skip, or the video was not filed anywhere) and is not an "unmatched leftover" - the folder is
    /// then kept as it is, which is reported as done. With the collision setting on Skip the same holds for a wanted companion of any video, since the one left behind may be a
    /// video that finished earlier. Not with Maintain, which never removes anything and copies companions, so they always remain.</summary>
    public string? TryCleanUpSourceFolder(CompressarrConfig config, string directory, string inputPath, string? context, string? videoPath = null)
    {
        try
        {
            if (config.Processing.DeleteAfterConvert != DeleteAfterConvertMode.Maintain)
            {
                // The collision setting is Skip: a companion whose name the destination already had stays where it is, and it may belong to ANY video that was in this
                // folder (one that finished earlier, or a different one finishing now) - so no wanted companion in a Skip folder is swept. Otherwise only this video's own.
                var skipKeepsAll = config.Processing.OnDestinationCollision == DestinationCollisionMode.Skip && CompanionFileService.AnyWantedCompanionIn(directory, config.Processing.CompanionExtensions);
                if (skipKeepsAll || (videoPath is not null && CompanionFileService.WantedCompanionRemains(directory, videoPath, config.Processing.CompanionExtensions)))
                {
                    _logger.Log($"  Source folder kept: a companion file in it was not moved (for example the destination already had one and the collision setting is Skip), so it was left where it is.");
                    return null;
                }
            }

            _companionFiles.CleanUpEmptySourceFolder(directory, inputPath, config.Processing.VidTypes, config.Processing.DeleteAfterConvert, config.Processing.UnmatchedCompanionAction);
            return null;
        }
        catch (Exception ex)
        {
            if (context is not null) _logger.Log($"  Source folder cleanup skipped{context}: {ex.Message}", LogSeverity.Error);
            return ex.Message;
        }
    }
}
