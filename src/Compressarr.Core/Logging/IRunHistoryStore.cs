namespace Compressarr.Core.Logging;

public sealed record RunHistoryRecord(
    int Year,
    int Month,
    int Day,
    double BeginSizeGb,
    double EndSizeGb,
    int FileCount,
    int ProcessHours,
    int ProcessMinutes,
    int ProcessSeconds,
    /// <summary>The persistent, permanent "this was the Nth run ever" number (matches
    /// IRunHistoryStore.GetRunCount at the moment this run was recorded) - 0 for rows written
    /// before this field existed, since old CSV rows don't carry it.</summary>
    int RunNumber = 0,
    /// <summary>Just the report's file name (e.g. "Compressarr_2026-08-29_12-00-00_Report.html"),
    /// not a full path - the web UI resolves it against the *current* Report.ReportPath at
    /// request time, so a later change to that setting doesn't strand old links. Empty for rows
    /// written before this field existed.</summary>
    string ReportFileName = "",
    /// <summary>How many files in this run failed outright (ReportModel.ErrorCount at record
    /// time). 0 for rows written before this field existed - indistinguishable from a genuinely
    /// clean run, same tradeoff RunNumber/ReportFileName already accept for old rows.</summary>
    int ErrorCount = 0,
    /// <summary>How many files succeeded but had a secondary post-process problem
    /// (ReportModel.WarningCount at record time - see ConversionResult.PostProcessWarning).</summary>
    int WarningCount = 0,
    /// <summary>How many files in this run landed in a different lane's library than their own
    /// (see ResumeEntry.DestinationLaneId) - a deliberate user redirect, not a problem. The History
    /// page highlights such a run so a lane change is visible after the fact. 0 for rows written
    /// before this field existed (a 2.1.x build also ignores this extra trailing column).</summary>
    int RedirectCount = 0,
    /// <summary>How many of this run's counted files (FileCount) were movies, and how many were TV episodes. Null = unknown: the row
    /// was written before these existed (or repaired without its report). A run's two counts add up to FileCount when known. Written as
    /// the last two columns, so older builds ignore them.</summary>
    int? MovieCount = null,
    int? TvCount = null,
    /// <summary>A rough average frames per second for the run (each file's own average weighted by how long it took; see RunBreakdown.AverageFps). Null =
    /// unknown: an older row, or a run with no file that gave a reading (all copies, say). Never 0.</summary>
    double? AvgFps = null,
    /// <summary>How many of the counted files each tool produced (they add up to FileCount when known; null = unknown, an older row). HandBrake includes a file an
    /// ffmpeg lane handed to it; PassthroughCount are the copies.</summary>
    int? HandBrakeCount = null,
    int? FfmpegCount = null,
    int? PassthroughCount = null,
    /// <summary>How many counted files each lane and each profile handled, by the names they had at the time (null = unknown; an empty list is a known "none",
    /// as for a run of only copies, which have no profile). Written as the last two columns.</summary>
    IReadOnlyDictionary<string, int>? LaneCounts = null,
    IReadOnlyDictionary<string, int>? PresetCounts = null,
    /// <summary>The run's id in the archive (the report's address) and whether its report can be opened. Filled in when read back from the database; 0 / false from the CSV store.</summary>
    long Id = 0,
    bool HasReport = false)
{
    /// <summary>A row written before failed encodes were left out of the totals, for a run in which every file
    /// failed and nothing was produced (it recorded the files and their size as "before" with 0 "after", i.e.
    /// 100% saved). The rollups skip these so old failures stop inflating the figures.</summary>
    public bool IsAllFailed => FileCount > 0 && ErrorCount >= FileCount && EndSizeGb <= 0;
}


/// <summary>
/// The run history and the run counter. 3.0 keeps them in SQLite (<see cref="SqliteRunHistoryStore"/>); the CSV store (<see cref="CsvRunHistoryStore"/>)
/// is what the first 3.0 start imports from, and what a failed import falls back to.
/// </summary>
public interface IRunHistoryStore
{
    /// <summary>Deletes every run and everything derived from them. The run counter stays: it is a lifetime figure, not history.</summary>
    void ClearHistory() { }

    /// <summary>Appends one row for a run that processed at least one file — an empty pass is
    /// never recorded, mirroring v1's Invoke-CompressarrRun gating.</summary>
    /// <param name="legacyLogFolder">Where 2.1.x kept the history file (the Logs folder). Only used to find and carry
    /// over an existing file - the history now lives in the app data folder.</param>
    void AppendRun(string legacyLogFolder, RunHistoryRecord record);

    IReadOnlyList<RunHistoryRecord> GetHistory(string legacyLogFolder);

    /// <summary>Persistent, cumulative count of runs that processed at least one file. Returns 0
    /// if never run before — also how the app recognizes "first launch ever" to decide which UI
    /// to show.</summary>
    int GetRunCount(string runCountPath);

    void IncrementRunCount(string runCountPath);
}
