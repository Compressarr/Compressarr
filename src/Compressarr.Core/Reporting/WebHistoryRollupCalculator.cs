using Compressarr.Core.Logging;

namespace Compressarr.Core.Reporting;

public sealed record WebHistoryRollups(
    HistoryRollup Today,
    HistoryRollup Last7Days,
    HistoryRollup Last30Days,
    HistoryRollup LastYear,
    HistoryRollup AllTime);

/// <summary>
/// Rolling windows anchored to "now" (Today, Last 7 Days, Last 30 Days, Last Year, All Time) for
/// the web UI's history page. Deliberately separate from HistoryRollupCalculator, which computes
/// calendar-aligned windows (Today/ThisMonth/ThisYear, resetting on the 1st/Jan 1st) for the
/// HTML report - a different, unrelated feature. Both read from the same IRunHistoryStore.
/// </summary>
public interface IWebHistoryRollupCalculator
{
    WebHistoryRollups Calculate(string logFilePath);
}

public sealed class WebHistoryRollupCalculator : IWebHistoryRollupCalculator
{
    private readonly IRunHistoryStore _historyStore;

    /// <summary>Settable seam for tests to pin "now" instead of depending on the real clock -
    /// production code never sets this. Unpinned, it reads the clock on every call: this calculator is a
    /// singleton, and capturing the time once at construction anchored "Today" and "Last 7 Days" to the day
    /// the app happened to start (a long-running install showed days-old figures).</summary>
    internal DateTime Now
    {
        get => _pinnedNow ?? DateTime.Now;
        set => _pinnedNow = value;
    }

    private DateTime? _pinnedNow;

    public WebHistoryRollupCalculator(IRunHistoryStore historyStore)
    {
        _historyStore = historyStore;
    }

    public WebHistoryRollups Calculate(string logFilePath)
    {
        var history = _historyStore.GetHistory(logFilePath).Where(r => !r.IsAllFailed).ToList();
        var today = Now.Date;

        static DateTime RecordDate(RunHistoryRecord r) => new(r.Year, r.Month, r.Day);

        HistoryRollup Rollup(IEnumerable<RunHistoryRecord> rows)
        {
            var rowList = rows.ToList();
            return new HistoryRollup(
                FileCount: rowList.Sum(r => r.FileCount),
                BeforeGb: Math.Round(rowList.Sum(r => r.BeginSizeGb), 3),
                AfterGb: Math.Round(rowList.Sum(r => r.EndSizeGb), 3),
                TotalTimeSeconds: rowList.Sum(r => r.ProcessHours * 3600 + r.ProcessMinutes * 60 + r.ProcessSeconds),
                MovieCount: rowList.Sum(r => r.MovieCount ?? 0),
                TvCount: rowList.Sum(r => r.TvCount ?? 0),
                UnknownCount: rowList.Where(r => r.MovieCount is null).Sum(r => r.FileCount));
        }

        var todayRollup = Rollup(history.Where(r => RecordDate(r) == today));
        var last7Days = Rollup(history.Where(r => RecordDate(r) > today.AddDays(-7)));
        var last30Days = Rollup(history.Where(r => RecordDate(r) > today.AddDays(-30)));
        var lastYear = Rollup(history.Where(r => RecordDate(r) > today.AddYears(-1)));
        var allTime = Rollup(history);

        return new WebHistoryRollups(todayRollup, last7Days, last30Days, lastYear, allTime);
    }
}
