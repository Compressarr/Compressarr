namespace Compressarr.Core.Conversion;

public sealed class ConversionResult
{
    public required string LaneId { get; init; }
    public required string FileName { get; init; }
    public required string FullName { get; init; }
    public string? NewFileName { get; init; }
    public required string ContentType { get; init; }
    public string? PresetName { get; init; }

    /// <summary>Which encoder actually encoded this file, when that is worth saying: "ffmpeg", or
    /// "HandBrake (Dolby Vision fallback)" for a file an ffmpeg lane handed to HandBrake. Null for an
    /// ordinary HandBrake encode, so a HandBrake-only setup's report reads exactly as before.</summary>
    public string? EncoderLabel { get; init; }

    /// <summary>True when the file was passed through (the Passthrough preset): copied and filed like any other, but not re-encoded.
    /// It still counts as a file in History and the report, but its bytes do not - nothing was saved, and counting them would only dilute
    /// the real savings.</summary>
    public bool IsPassthrough { get; init; }

    /// <summary>The sizes that go into totals (History, the report, the toast): zero for a passed-through file, so no total can forget the rule.</summary>
    public double CountedBeginGb => IsPassthrough ? 0 : BeginSizeGb;

    /// <summary>See <see cref="CountedBeginGb"/>.</summary>
    public double CountedEndGb => IsPassthrough ? 0 : EndSizeGb;

    public double BeginSizeGb { get; init; }
    public double EndSizeGb { get; init; }
    public bool Success { get; init; }
    public string? DetailLogFile { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public string? ArrStatus { get; init; }

    /// <summary>True if this file's failure (encode or move) looked like the volume being out of
    /// space, rather than some other error. Bubbles up through RunResult so the run loop can stop
    /// monitoring automatically instead of repeatedly retrying a failure that won't resolve
    /// itself on the next poll.</summary>
    public bool DiskFull { get; init; }

    /// <summary>Short, human-readable reason logged to the plain-text run log in place of the
    /// generic "ERROR" - this is deliberately not a catch-all "why did this fail" field, only set
    /// for the specific failure modes Compressarr can actually diagnose.</summary>
    public string? FailureReason { get; init; }

    /// <summary>The report's own numbered classification of this failure (see ReportErrorCode) -
    /// null for a success. Encode failures link the HandBrake detail log as before; every move
    /// failure shows this code + a help-bubble description instead of that irrelevant link.</summary>
    public ReportErrorCode? ErrorCode { get; init; }

    /// <summary>Whether this file belongs in the History totals (files, sizes, savings). A file whose encode
    /// never produced output (an encode failure, no preset, ...) does not - counting it put its size in
    /// "Before" with nothing in "After", so every failure showed as 100% saved. A file that DID encode but
    /// could not be moved to its destination (ERROR 102-104) does count: its sizes are real.</summary>
    public bool CountsTowardTotals => Success
        || ErrorCode is ReportErrorCode.MoveDestinationUnavailable or ReportErrorCode.MoveDiskFull or ReportErrorCode.MoveFailedOther;

    /// <summary>Set when a successful conversion still had a problem in a secondary post-process
    /// step - moving companion files (subtitles, .nfo, artwork) or the Sonarr/Radarr unmonitor
    /// call. Deliberately doesn't flip <see cref="Success"/> or count toward the report's error
    /// total: the video itself converted and was filed correctly, this just flags that something
    /// downstream of that needs a look, shown as a distinct marker rather than lumped in with
    /// "ERROR". Null when nothing went wrong.</summary>
    public string? PostProcessWarning { get; init; }

    /// <summary>The lane the user assigned this file to land in instead (see ResumeEntry.
    /// DestinationLaneId), set only when the file really was routed into that lane's library -
    /// null for every ordinary file. HomeLaneName is the lane it came from (always set), so the
    /// report can say "redirected from X".</summary>
    public string? RedirectedToLaneId { get; init; }
    public string? RedirectedToLaneName { get; init; }
    public string? HomeLaneName { get; init; }
}
