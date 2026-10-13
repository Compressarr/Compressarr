using Compressarr.Core.Logging;

namespace Compressarr.Core.Notifications;

/// <summary>Aggregate stats for a digest period (a day or a week of runs), built from History
/// rather than tied to any single run - see DigestSummaryBuilder for how the period is chosen.</summary>
public sealed record DigestSummary(int TotalFiles, double BeginSizeGb, double EndSizeGb, TimeSpan TotalDuration)
{
    public double SavedGb => BeginSizeGb - EndSizeGb;
    public double SavedPercent => BeginSizeGb > 0 ? SavedGb / BeginSizeGb * 100 : 0;

    public string ToMessage()
    {
        var word = TotalFiles == 1 ? "file" : "files";
        return $"Compressed {TotalFiles} {word}, reducing original size from {BeginSizeGb:0.##} GB " +
               $"to {EndSizeGb:0.##} GB, saving {SavedPercent:0.#}% of original size.";
    }

    /// <summary>Shared with IDigestScheduler's real scheduled sends, so a "Test Digest" button
    /// (which fires immediately, bypassing the schedule entirely) produces byte-for-byte the same
    /// shape a real digest would.</summary>
    public NotificationEvent ToNotificationEvent(string periodLabel, string? machineName = null) =>
        new(NotificationOutcome.Success, $"Compressarr {periodLabel}", ToMessage(), TotalFiles, SavedGb, TotalDuration, ReportPath: null, MachineName: machineName);
}

/// <summary>Builds a DigestSummary from History over a fixed calendar window - not "since the last
/// digest was sent", which would be self-healing after downtime but unpredictable (a missed week
/// could balloon into a huge digest). Daily always means "yesterday", Weekly always means "the
/// trailing 7 calendar days" - regardless of when the app actually gets around to sending it.</summary>
public static class DigestSummaryBuilder
{
    public static DigestSummary BuildDaily(IReadOnlyList<RunHistoryRecord> history, DateOnly today) =>
        Build(history, today.AddDays(-1), today.AddDays(-1));

    public static DigestSummary BuildWeekly(IReadOnlyList<RunHistoryRecord> history, DateOnly today) =>
        Build(history, today.AddDays(-7), today.AddDays(-1));

    private static DigestSummary Build(IReadOnlyList<RunHistoryRecord> history, DateOnly start, DateOnly end)
    {
        var matches = history.Where(r => !r.IsAllFailed).Where(r =>
        {
            var d = new DateOnly(r.Year, r.Month, r.Day);
            return d >= start && d <= end;
        }).ToList();

        var totalDuration = matches.Aggregate(TimeSpan.Zero, (sum, r) =>
            sum + new TimeSpan(r.ProcessHours, r.ProcessMinutes, r.ProcessSeconds));

        return new DigestSummary(
            matches.Sum(r => r.FileCount),
            matches.Sum(r => r.BeginSizeGb),
            matches.Sum(r => r.EndSizeGb),
            totalDuration);
    }
}
