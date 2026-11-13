using Compressarr.Core.Logging;

namespace Compressarr.Core.Reporting;

public interface IHistoryRollupCalculator
{
    (HistoryRollup Today, HistoryRollup ThisMonth, HistoryRollup ThisYear) Calculate(string logFilePath);
}

/// <summary>Ported from Get-CompressarrHistoryRollups.</summary>
public sealed class HistoryRollupCalculator : IHistoryRollupCalculator
{
    private readonly IRunHistoryStore _historyStore;

    public HistoryRollupCalculator(IRunHistoryStore historyStore)
    {
        _historyStore = historyStore;
    }

    public (HistoryRollup Today, HistoryRollup ThisMonth, HistoryRollup ThisYear) Calculate(string logFilePath)
    {
        // From the running totals when the store keeps them: a few small rows instead of every run in the history.
        if (_historyStore is IRunTotals totals)
        {
            var day = DateTime.Now.Date;
            if (totals.Sum(day, day) is { } fromTotals)
            {
                return (fromTotals,
                    totals.Sum(new DateTime(day.Year, day.Month, 1), day) ?? fromTotals,
                    totals.Sum(new DateTime(day.Year, 1, 1), day) ?? fromTotals);
            }
        }

        var history = _historyStore.GetHistory(logFilePath).Where(r => !r.IsAllFailed).ToList();
        var now = DateTime.Now;

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
                UnknownCount: rowList.Where(r => r.MovieCount is null).Sum(r => r.FileCount),
                VideoCount: rowList.Sum(r => r.VideoCount ?? 0));
        }

        var today = Rollup(history.Where(r => r.Year == now.Year && r.Month == now.Month && r.Day == now.Day));
        var thisMonth = Rollup(history.Where(r => r.Year == now.Year && r.Month == now.Month));
        var thisYear = Rollup(history.Where(r => r.Year == now.Year));

        return (today, thisMonth, thisYear);
    }
}
