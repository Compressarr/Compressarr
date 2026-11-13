namespace Compressarr.Core.Logging;

/// <summary>One run as the Automation API lists it. <see cref="EndedAt"/> is the server's local time (older runs only know the day: the time is then 00:00:00).</summary>
public sealed record RunSummary(
    long Id, int Number, DateTime EndedAt, int DurationSeconds, int Files, double BeforeGb, double AfterGb, int Errors, int Warnings, int Redirects,
    int? Movies, int? Tvs, double? AvgFps, int? HandBrakeFiles, int? FfmpegFiles, int? PassthroughFiles, bool Counted, bool HasReport, int Videos = 0);

/// <summary>One file of a run, as stored. The file's long detail log is deliberately not here.</summary>
public sealed record RunFileDetail(
    string Name, string ContentType, string? Preset, string? Encoder, string Engine, bool Passthrough, double BeforeGb, double AfterGb, double Seconds,
    DateTime? StartedAt, DateTime? EndedAt, bool Success, bool Counted, double? AvgFps, int? ErrorCode, string? FailureReason, string? Warning, string? ArrStatus,
    string? RedirectedToLane, string? HomeLane, bool DiskFull);

public sealed record RunLaneDetail(string Name, IReadOnlyList<string> Problems, IReadOnlyList<RunFileDetail> Files);

public sealed record RunDetail(RunSummary Run, IReadOnlyList<RunLaneDetail> Lanes);

public sealed record NameCount(string Name, int Files);

/// <summary>The figures for a period, summed from the running totals (one small row per day), not from the runs.</summary>
public sealed record PeriodStats(
    int Runs, int Files, double BeforeGb, double AfterGb, long Seconds, int Movies, int Tvs, int UnknownSplit, int Errors, int Warnings, int Redirects,
    int HandBrakeFiles, int FfmpegFiles, int PassthroughFiles, double? AvgFps, IReadOnlyList<NameCount> Lanes, IReadOnlyList<NameCount> Profiles, int Videos = 0);

/// <summary>Read-only questions the Automation API asks of the run history database. Implemented by <see cref="SqliteRunHistoryStore"/>.</summary>
public interface IRunQueries
{
    /// <summary>True when the history database is in use (false while the CSV fallback is serving, which cannot answer these).</summary>
    bool IsAvailable();

    /// <summary>The runs (not the report-only passes), newest first, whose day is within <paramref name="fromDay"/> to <paramref name="toDay"/> inclusive (null = no
    /// limit); one page of them, with the count of all that match. A page past the end is the last page.</summary>
    (IReadOnlyList<RunSummary> Runs, int Total, int Page) ListRuns(DateTime? fromDay, DateTime? toDay, int page, int pageSize);

    /// <summary>A run with its lanes and files, or null when there is no such run.</summary>
    RunDetail? GetRun(long id);

    /// <summary>The counted runs' figures from <paramref name="fromDay"/> to <paramref name="toDay"/> inclusive (null = no limit).</summary>
    PeriodStats GetStats(DateTime? fromDay, DateTime? toDay);
}
