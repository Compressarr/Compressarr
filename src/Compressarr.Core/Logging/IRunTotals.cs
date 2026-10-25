using Compressarr.Core.Reporting;

namespace Compressarr.Core.Logging;

/// <summary>The history's period figures read from the running totals the run archive keeps (one small row per day), instead of adding up every run
/// each time a page loads. Implemented by <see cref="SqliteRunHistoryStore"/>; the calculators fall back to summing the runs when a store does not
/// have totals (the CSV fallback, a test's fake store).</summary>
public interface IRunTotals
{
    /// <summary>The sum of the counted runs from <paramref name="from"/> to <paramref name="to"/> (both days inclusive; null = no limit),
    /// or null when the totals are not available.</summary>
    HistoryRollup? Sum(DateTime? from, DateTime? to);
}
