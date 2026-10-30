namespace Compressarr.Core.Conversion;

/// <summary>
/// The report's numbered classification of a WARNING: a file that converted and was filed, but something around it did not go
/// as planned. Errors (ReportErrorCode, 101-114) mean a file or a lane did not get done; a warning never does, so it lives in its own
/// series, starting at 201 so no number means two things ("ERROR 102" and "WARN 201" can never be mixed up, even said out loud).
/// A file's warnings are kept in ConversionResult.WarningCodes beside the free-text ConversionResult.PostProcessWarning, which still
/// carries the specifics (a path, the reason).
/// </summary>
public enum ReportWarningCode
{
    /// <summary>The collision setting is "Skip" and the destination already had a file of that name: the converted file was not moved there.</summary>
    MoveSkippedAlreadyExists = 201,

    /// <summary>A companion file (subtitles and the like) could not be moved, so it is still in the source folder.</summary>
    CompanionFilesNotMoved = 202,

    /// <summary>Sonarr or Radarr could not be told to stop monitoring the file.</summary>
    ArrUnmonitorFailed = 203,

    /// <summary>Removing the empty source folder waits for a later pass, because the Sonarr/Radarr rescan was not confirmed complete.</summary>
    CleanupDeferredRescan = 204,

    /// <summary>Removing the empty source folder failed (a locked file, permissions, antivirus) and is retried on a later pass.</summary>
    CleanupDeferredFailed = 205,

    /// <summary>The file at the source's path was not the one that was converted (it changed while the encode ran, or while a move waited), so it was kept instead of deleted.</summary>
    SourceChangedKept = 206,
}

public static class ReportWarningCodeExtensions
{
    /// <summary>A few words for a notification's {warning} token.</summary>
    public static string ShortLabel(this ReportWarningCode code) => code switch
    {
        ReportWarningCode.MoveSkippedAlreadyExists => "Move skipped (already exists)",
        ReportWarningCode.CompanionFilesNotMoved => "Companion files not moved",
        ReportWarningCode.ArrUnmonitorFailed => "Arr unmonitor failed",
        ReportWarningCode.CleanupDeferredRescan => "Cleanup deferred (rescan not confirmed)",
        ReportWarningCode.CleanupDeferredFailed => "Cleanup deferred (cleanup failed)",
        ReportWarningCode.SourceChangedKept => "Source changed, kept",
        _ => "Warning",
    };

    /// <summary>The full sentence shown in the report's help-bubble for this code - HTML-encoded by the caller, not here.</summary>
    public static string Describe(this ReportWarningCode code) => code switch
    {
        ReportWarningCode.MoveSkippedAlreadyExists =>
            "The destination already has a file with this name and the collision setting is Skip, so the converted file was not moved there. It stays in the Output folder. Change the collision setting on the Settings page, or remove the existing file, if you want it filed.",
        ReportWarningCode.CompanionFilesNotMoved =>
            "The video was converted and filed, but its companion files (subtitles and the like) could not be moved. They are still in the source folder, which is kept so nothing is lost, and the move is retried on the lane's next pass.",
        ReportWarningCode.ArrUnmonitorFailed =>
            "The video was converted and filed, but Sonarr or Radarr could not be told to stop monitoring it (not reachable, or it refused). The file itself is fine. Check the Sonarr/Radarr connection on the Settings page.",
        ReportWarningCode.CleanupDeferredRescan =>
            "The video was converted and filed. Sonarr or Radarr has not confirmed its rescan yet, so the now-empty source folder is left for a later pass to remove.",
        ReportWarningCode.CleanupDeferredFailed =>
            "The video was converted and filed, but the empty source folder could not be removed (a locked file, permissions, antivirus). It is retried on the next pass.",
        ReportWarningCode.SourceChangedKept =>
            "The video was converted and filed, but the file at the source's path is no longer the one that was converted (Sonarr or Radarr replaced it, or a different file was copied in while it converted). It was kept instead of deleted, and is queued as new work on a later scan.",
        _ => "An unspecified warning.",
    };
}
