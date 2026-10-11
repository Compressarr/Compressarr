namespace Compressarr.Core.Conversion;

/// <summary>Stable, numbered classification of why a file failed, shown on the HTML report as a
/// compact "ERROR &lt;code&gt;" badge with a help-bubble giving the full description - real bug
/// found live: the report's Status column used to just say plain "ERROR" for every failure with no
/// way to tell what actually went wrong without digging through the log, and for a move failure
/// specifically (the video converted fine, only the destination move failed) it linked to
/// HandBrake's own detail log, which is completely irrelevant since HandBrake had nothing to do
/// with the failure. Numbers are part of this app's own public-facing vocabulary once shipped -
/// treat them as stable identifiers, not an ordinal to reshuffle.</summary>
public enum ReportErrorCode
{
    /// <summary>HandBrake itself failed to convert the file - the linked detail log (HandBrake's
    /// own output) is genuinely relevant here and stays shown.</summary>
    EncodeFailed = 101,

    /// <summary>The video converted successfully, but the destination folder/drive couldn't be
    /// reached (an offline network share, a missing drive letter, a bad UNC path, etc.). The
    /// converted file remains in the processing folder and the move is retried automatically on
    /// the lane's next pass.</summary>
    MoveDestinationUnavailable = 102,

    /// <summary>The video converted successfully, but the destination drive was out of space.
    /// Monitoring stops itself automatically rather than repeating the same doomed move.</summary>
    MoveDiskFull = 103,

    /// <summary>The video converted successfully, but moving it to its destination failed for some
    /// other reason (permission denied, invalid credentials, a locked file, etc.). The converted
    /// file remains in the processing folder and the move is retried automatically on the lane's
    /// next pass.</summary>
    MoveFailedOther = 104,

    /// <summary>No TV or Movie preset (whichever this file's content type needed) is configured on
    /// this lane, so nothing was ever attempted for this file.</summary>
    NoPresetConfigured = 105,

    // 106+: lane/run-level conditions, not tied to any one file - shown as a notice on the lane's
    // own section (LaneReportSection.LaneProblems) rather than in a file row's Status column.
    // Real gap found live: these were log-only before - visible in the plain-text Summary log, but
    // completely invisible on the HTML report, which most people actually look at first (it can
    // even auto-open). A user who only ever checks the report had no way to know why a lane
    // produced nothing.

    /// <summary>The lane has no TV or Movie preset configured at all - it was skipped entirely
    /// this pass, nothing in it was even scanned.</summary>
    LaneNoPresetConfigured = 106,

    /// <summary>The lane has no Output folder configured, and "write output to same folder as
    /// input" is off - it was skipped entirely this pass, nothing in it was even scanned.</summary>
    LaneNoOutputConfigured = 107,

    /// <summary>The lane's configured TV preset name doesn't exist in Compressarr's profiles - the lane
    /// still ran, but any TV episodes in it were skipped this pass (Movies, if configured
    /// correctly, still processed normally).</summary>
    LaneTvPresetNotFound = 108,

    /// <summary>The lane's configured Movie preset name doesn't exist in Compressarr's profiles - the lane
    /// still ran, but any movies in it were skipped this pass (TV, if configured correctly, still
    /// processed normally).</summary>
    LaneMoviePresetNotFound = 109,

    /// <summary>FileBot pre-processing is enabled but its configured path wasn't found - the lane
    /// still ran normally, FileBot pre-processing was just skipped for this pass.</summary>
    FileBotPathNotFound = 110,

    /// <summary>An ffmpeg encode exited normally, but the file it wrote is not the length of the
    /// source (a corrupt source, a full disk). It was not kept and the original was left alone.</summary>
    EncodedLengthMismatch = 111,

    /// <summary>The source has Dolby Vision or HDR10+ dynamic metadata, which ffmpeg would silently
    /// drop, and there is no usable HandBrake profile to hand it to. The file was not touched.</summary>
    DynamicHdrNeedsHandBrake = 112,

    /// <summary>The lane's encoder (HandBrake or ffmpeg) isn't installed where Compressarr looks for it,
    /// so the lane was skipped this pass.</summary>
    LaneEncoderNotAvailable = 113,

    /// <summary>A HandBrake lane met an HDR / Dolby Vision file whose profile's video encoder would lose the HDR (an SDR-only H.264
    /// profile, or a hardware encoder that can't keep Dolby Vision / HDR10+). The file was not touched.</summary>
    ProfileCannotKeepHdr = 114,
}

public static class ReportErrorCodeExtensions
{
    /// <summary>The full sentence shown in the report's help-bubble for this code - HTML-encoded by
    /// the caller along with everything else, not here.</summary>
    public static string Describe(this ReportErrorCode code) => code switch
    {
        ReportErrorCode.EncodeFailed =>
            "The video failed to convert. See the linked detail log for HandBrake's own error output.",
        ReportErrorCode.MoveDestinationUnavailable =>
            "File move failure: the destination folder or drive couldn't be reached (offline network share, missing drive, etc.). The converted file is safe in the processing folder and will be moved automatically once the destination is reachable again.",
        ReportErrorCode.MoveDiskFull =>
            "File move failure: the destination drive is out of space. Monitoring has stopped itself automatically - free up space, then start it again.",
        ReportErrorCode.MoveFailedOther =>
            "File move failure: the converted file couldn't be moved to its destination (permissions, invalid credentials, a locked file, etc.). The converted file is safe in the processing folder and will be retried automatically on the next pass.",
        ReportErrorCode.NoPresetConfigured =>
            "No preset is configured on this lane for this file's content type (TV or Movie). Set one on the Lanes page.",
        ReportErrorCode.LaneNoPresetConfigured =>
            "This lane has no TV or Movie preset configured, so it was skipped entirely this pass. Set one on the Lanes page.",
        ReportErrorCode.LaneNoOutputConfigured =>
            "This lane has no Output folder configured, and 'write output to same folder as input' is off, so it was skipped entirely this pass. Set an Output folder on the Lanes page, or enable that setting.",
        ReportErrorCode.LaneTvPresetNotFound =>
            "This lane's configured TV preset wasn't found in Compressarr's profiles, so TV episodes in it were skipped this pass. Check the preset name on the Lanes page, or add it on the Profiles page.",
        ReportErrorCode.LaneMoviePresetNotFound =>
            "This lane's configured Movie preset wasn't found in Compressarr's profiles, so movies in it were skipped this pass. Check the preset name on the Lanes page, or add it on the Profiles page.",
        ReportErrorCode.FileBotPathNotFound =>
            "FileBot pre-processing is enabled but its configured path wasn't found, so it was skipped for this pass. Check the FileBot path on the Settings page.",
        ReportErrorCode.EncodedLengthMismatch =>
            "The encoded file is much shorter (or longer) than the source, so it was not kept and the original was left alone. This can mean a corrupt source or a full disk. Open the encode details for ffmpeg's own output.",
        ReportErrorCode.DynamicHdrNeedsHandBrake =>
            "This file has Dolby Vision or HDR10+ dynamic metadata, which ffmpeg would silently drop. The ffmpeg profile has no usable HandBrake fallback profile (or HandBrake is not installed), so the file was left alone and nothing was encoded. To encode such files, name a HandBrake fallback profile on the ffmpeg profile (Profiles page), or put them in a HandBrake lane. A profile can also be set to encode them anyway without the Dolby Vision / HDR10+ data, but never a Dolby Vision profile 5 file, which would show wrong colours.",
        ReportErrorCode.ProfileCannotKeepHdr =>
            "This file is HDR (HDR10, HLG, Dolby Vision or HDR10+), and the lane's profile would lose it - an H.264 profile can only produce SDR, and a hardware encoder can't keep Dolby Vision or HDR10+ - so the file was left alone and nothing was encoded. Use a profile that keeps HDR (x265 or SVT-AV1), or tick 'Allow HDR files to lose their HDR' on the lane (Advanced). A Dolby Vision profile 5 file is always refused, because it would show wrong colours.",
        ReportErrorCode.LaneEncoderNotAvailable =>
            "This lane's encoder (HandBrake or ffmpeg) wasn't found where Compressarr looks for it, so the lane was skipped this pass. Check the Encoder page.",
        _ => "An unspecified error occurred.",
    };
}
