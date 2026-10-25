using System.Globalization;

namespace Compressarr.Core.Logging;

/// <summary>When a run ended, for the rows that only know the DAY: the history file keeps the day a run ended, but a report is named for the
/// moment the run STARTED ("Compressarr_2026-10-24_07-09-21_Report.html"), so the end is that moment plus the run's time.</summary>
public static class RunTimes
{
    /// <summary>The time of day the run ended (on the record's own date), or null for a row with no timestamped report name (older rows).</summary>
    public static DateTime? EndedAt(RunHistoryRecord run)
    {
        var name = run.ReportFileName;
        if (string.IsNullOrEmpty(name) || !name.StartsWith("Compressarr_", StringComparison.Ordinal) || name.Length < 12 + 19) return null;
        if (!DateTime.TryParseExact(name.AsSpan(12, 19), "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)) return null;

        var end = start.AddSeconds(run.ProcessHours * 3600 + run.ProcessMinutes * 60 + run.ProcessSeconds);
        try { return new DateTime(run.Year, run.Month, run.Day).Add(end.TimeOfDay); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    /// <summary>Minutes after midnight, as the Today chart wants them.</summary>
    public static int? EndMinuteOfDay(RunHistoryRecord run) => EndedAt(run) is { } end ? (int)end.TimeOfDay.TotalMinutes : null;
}
