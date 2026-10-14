using Compressarr.Core.Conversion;

namespace Compressarr.Core.Reporting;

public sealed class LaneReportSection
{
    public required string LaneDisplayName { get; init; }
    public required IReadOnlyList<ConversionResult> Results { get; init; }

    /// <summary>Lane/run-level problems (missing preset, missing Output folder, FileBot path not
    /// found, etc) that aren't tied to any one file in Results - shown as their own notice on this
    /// lane's section, same ERROR-code-plus-tooltip treatment a file row gets. Empty for a
    /// perfectly healthy lane. See ReportErrorCode's 106+ values.</summary>
    public IReadOnlyList<ReportErrorCode> LaneProblems { get; init; } = Array.Empty<ReportErrorCode>();
}

/// <summary>TotalTimeSeconds defaults to 0 (rather than being required) so existing call sites
/// that only care about file counts/sizes don't all need updating.</summary>
public sealed record HistoryRollup(int FileCount, double BeforeGb, double AfterGb, double TotalTimeSeconds = 0);

public sealed class ReportModel
{
    public required DateTime GeneratedAt { get; init; }
    public required TimeSpan RunTime { get; init; }
    public required IReadOnlyList<LaneReportSection> Lanes { get; init; }

    /// <summary>Path to this run's own plain-text summary log - linked from the Status column for
    /// any non-encode failure (a move failure, a missing preset), where HandBrake's own detail log
    /// would be irrelevant or nonexistent. Null only in contexts (existing tests, mainly) that
    /// build a ReportModel without going through RunOrchestrator.</summary>
    public string? SummaryLogFilePath { get; init; }

    /// <summary>0 means "this pass found nothing to do" - only a run that actually processed
    /// files gets a permanent number (see RunOrchestrator/IRunHistoryStore.IncrementRunCount).
    /// The report shows a plain "Run:" label instead of "Run #N:" when this is 0.</summary>
    public int RunNumber { get; init; }

    /// <summary>Count of resume entries whose deferred/stranded work was successfully resolved
    /// this pass without a re-encode - a failed move, a failed companion-file move, or a
    /// CleanupPending entry whose deferred Sonarr/Radarr rescan or source-folder cleanup finally
    /// succeeded (see ResumeStatus.CleanupPending) - see RunResult.RetriesSucceeded. Shown as its
    /// own note on the report since these entries never went through Results (no re-encode
    /// happened, so there's no meaningful before/after size to report alongside a real
    /// conversion), but the recovery succeeding is still real, worth-recording activity.</summary>
    public int RetriesSucceeded { get; init; }

    public HistoryRollup? Today { get; init; }
    public HistoryRollup? ThisMonth { get; init; }
    public HistoryRollup? ThisYear { get; init; }

    public bool HasAnyLaneProblems => Lanes.Any(l => l.LaneProblems.Count > 0);

    public int TotalFiles => Lanes.Sum(l => l.Results.Count);
    public int ErrorCount => Lanes.Sum(l => l.Results.Count(r => !r.Success));

    /// <summary>Count of otherwise-successful files that still had a secondary post-process
    /// problem (companion-file move or Sonarr/Radarr unmonitor) - see
    /// <see cref="Conversion.ConversionResult.PostProcessWarning"/>. Distinct from
    /// <see cref="ErrorCount"/>: these files did convert and get filed correctly.</summary>
    public int WarningCount => Lanes.Sum(l => l.Results.Count(r => r.Success && !string.IsNullOrEmpty(r.PostProcessWarning)));

    /// <summary>Files that really landed in a different lane's library than their own - a deliberate
    /// user redirect (see ResumeEntry.DestinationLaneId), shown on the report as its own notice and
    /// counted in the run history so the History page can highlight the run.</summary>
    public IReadOnlyList<ConversionResult> RedirectedResults =>
        Lanes.SelectMany(l => l.Results).Where(r => r.RedirectedToLaneId is not null).ToList();

    public int RedirectedCount => RedirectedResults.Count;

    // A passed-through file is listed but its bytes are not counted (nothing was saved), so the run's savings stay the real ones.
    public double TotalBeforeGb =>
 Lanes.Sum(l => l.Results.Sum(r => r.CountedBeginGb));
    public double TotalAfterGb => Lanes.Sum(l => l.Results.Sum(r => r.CountedEndGb));
}
