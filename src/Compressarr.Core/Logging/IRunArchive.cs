using Compressarr.Core.Reporting;

namespace Compressarr.Core.Logging;

/// <summary>One finished file's size and encode time, for the starting speed estimate (see ArchiveThroughputEstimator).</summary>
public sealed record FileSample(string? Preset, double BeforeGb, double Seconds);

/// <summary>What the run archive keeps beyond the history row (3.0): everything a report shows, so the report is built when someone opens it and no
/// HTML file per run is needed. Implemented by <see cref="SqliteRunHistoryStore"/>; see docs/design-sqlite-history.md.</summary>
public interface IRunArchive
{
    /// <summary>True when the database is in use. False while the CSV store is serving instead (a failed import): the caller then keeps writing
    /// report files as before.</summary>
    bool IsAvailable();

    /// <summary>Records a run's history row and totals, and returns its id (the address of its report). 0 when the CSV fallback took it.</summary>
    long RecordRun(RunHistoryRecord record);

    /// <summary>Stores what the report shows for a recorded run: the lane sections, every file's row and the Today / This month / This year figures.
    /// Replaces anything stored before for that run.</summary>
    void SaveReport(long runId, ReportModel report);

    /// <summary>Records a pass that only has a report (it hit a lane problem, or retried a deferred file): no history row and no totals.</summary>
    long RecordPass(ReportModel report);

    /// <summary>Notes that a copy of the run's report was also saved as a file, under this name.</summary>
    void SetSavedCopy(long runId, string fileName);

    /// <summary>The report rebuilt from its rows, or null when the run has no stored report detail (see <see cref="LoadLegacyReportHtml"/>).</summary>
    ReportModel? LoadReport(long runId);

    /// <summary>The html of an old report that was saved to disk before 3.0, kept as it was; null for any other run.</summary>
    byte[]? LoadLegacyReportHtml(long runId);

    /// <summary>The runs that have a report, newest first, from <paramref name="since"/> on, shaped for the History page's list.
    /// ReportFileName is the run's id: that is what the report link carries.</summary>
    IReadOnlyList<ReportListEntry> ListReports(DateTime since);

    /// <summary>The runs with a report that had an error or a warning, newest first (at most <paramref name="limit"/>): those newer than
    /// <paramref name="newerThanRunNumber"/> when it is given (the ones not yet viewed), and not before <paramref name="since"/>. The History page's
    /// "unseen errors and warnings" table and its "View recent errors" button.</summary>
    IReadOnlyList<ReportListEntry> ListProblemReports(int? newerThanRunNumber, DateTime since, int limit);

    /// <summary>Of the runs with a report newer than <paramref name="viewedThroughRunNumber"/> (and not before <paramref name="since"/>): how many had an error,
    /// how many had a warning, and how many there are in all. The sidebar's History badges, counted in the database so every page can ask on load without
    /// fetching the report list; the total tells the History page whether the list it is showing holds every run that is new.</summary>
    (int ErrorRuns, int WarningRuns, int UnseenRuns) CountUnseen(int viewedThroughRunNumber, DateTime since);

    /// <summary>The files that finished successfully in the most recent <paramref name="runs"/> runs, for the speed estimate.</summary>
    IReadOnlyList<FileSample> RecentFileSamples(int runs);
}
