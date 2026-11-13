using Compressarr.Core.Conversion;

namespace Compressarr.Core.Reporting;

/// <summary>
/// A copy of a report that is safe to send to someone else (3.1): every file name, path, free-text failure or warning message and log link is replaced
/// by "Removed". What stays is what makes the report useful for support or bragging - counts, sizes, savings, durations, lanes, profiles, the numbered
/// ERROR and WARNING codes and which encoder ran. The original model is never changed.
/// </summary>
public static class ReportRedactor
{
    /// <summary>What a removed name or message reads as. The same word the History page uses for a scrubbed file.</summary>
    public const string Removed = "Removed";

    public static ReportModel Redact(ReportModel model) => new()
    {
        GeneratedAt = model.GeneratedAt,
        RunTime = model.RunTime,
        Lanes = model.Lanes.Select(lane => new LaneReportSection
        {
            LaneDisplayName = lane.LaneDisplayName,
            LaneProblems = lane.LaneProblems,
            Results = lane.Results.Select(RedactResult).ToList()
        }).ToList(),
        SummaryLogFilePath = null,                   // a path on this machine, and a link that would not work elsewhere
        RunNumber = model.RunNumber,
        RetriesSucceeded = model.RetriesSucceeded,
        Today = model.Today,
        ThisMonth = model.ThisMonth,
        ThisYear = model.ThisYear
    };

    private static ConversionResult RedactResult(ConversionResult r) => new()
    {
        LaneId = r.LaneId,
        FileName = Removed,
        FullName = Removed,
        NewFileName = null,
        ContentType = r.ContentType,
        PresetName = r.PresetName,
        EncoderLabel = r.EncoderLabel,
        IsPassthrough = r.IsPassthrough,
        AvgFps = r.AvgFps,
        BeginSizeGb = r.BeginSizeGb,
        EndSizeGb = r.EndSizeGb,
        Success = r.Success,
        DetailLogFile = null,
        StartTime = r.StartTime,
        EndTime = r.EndTime,
        ArrStatus = string.IsNullOrEmpty(r.ArrStatus) ? r.ArrStatus : Removed,     // Sonarr/Radarr messages can name the title
        DiskFull = r.DiskFull,
        FailureReason = string.IsNullOrEmpty(r.FailureReason) ? r.FailureReason : Removed,
        ErrorCode = r.ErrorCode,
        PostProcessWarning = string.IsNullOrEmpty(r.PostProcessWarning) ? r.PostProcessWarning : Removed,
        WarningCodes = r.WarningCodes,
        RedirectedToLaneId = r.RedirectedToLaneId,
        RedirectedToLaneName = r.RedirectedToLaneName,
        HomeLaneName = r.HomeLaneName
    };
}
