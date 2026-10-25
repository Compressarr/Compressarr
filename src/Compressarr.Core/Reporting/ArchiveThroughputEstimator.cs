using Compressarr.Core.Logging;

namespace Compressarr.Core.Reporting;

/// <summary>The starting speed estimate (GB of source per minute, by profile and overall) from the files the run archive has stored for the most
/// recent runs. Before 3.0 this was read out of recent report HTML files with a regular expression; the rows are the same facts without the
/// scraping. With no stored files yet (a fresh install, or history imported without them) the file-based estimator is asked as before.</summary>
public sealed class ArchiveThroughputEstimator : IHistoryThroughputEstimator
{
    // Deliberately small and recency-biased, like the file-based one: a rough placeholder until the current session has live data.
    private const int RecentRuns = 5;

    // Below this a "conversion" is almost certainly a throwaway test file, which would badly skew the rate toward unrealistic speed.
    private const double MinSampleGb = 0.05;

    private readonly IRunArchive _archive;
    private readonly IHistoryThroughputEstimator _fallback;

    public ArchiveThroughputEstimator(IRunArchive archive, IHistoryThroughputEstimator fallback)
    {
        _archive = archive;
        _fallback = fallback;
    }

    public HistoricalThroughput Estimate(string reportPath)
    {
        var samples = _archive.IsAvailable()
            ? _archive.RecentFileSamples(RecentRuns).Where(s => s.BeforeGb >= MinSampleGb && s.Seconds > 0).ToList()
            : new List<FileSample>();
        if (samples.Count == 0) return _fallback.Estimate(reportPath);

        var byPreset = samples
            .Where(s => !string.IsNullOrWhiteSpace(s.Preset))
            .GroupBy(s => s.Preset!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(s => s.BeforeGb) / (g.Sum(s => s.Seconds) / 60.0), StringComparer.OrdinalIgnoreCase);
        double? global = samples.Sum(s => s.BeforeGb) / (samples.Sum(s => s.Seconds) / 60.0);
        return new HistoricalThroughput(byPreset, global);
    }
}
