using System.Globalization;
using Compressarr.Core.Config;

namespace Compressarr.Core.Logging;

/// <summary>Ported from Add-CompressarrHistoryRecord/Get-CompressarrHistory and
/// Get-CompressarrRunCount/Set-CompressarrRunCount. Hand-rolled CSV (9 fixed numeric/date-part
/// columns, no embedded commas or quoting concerns) rather than a full CSV library — this is the
/// seam Phase 4 replaces with a structured store for the history-rollup/CPU/web-monitor features.</summary>
public sealed class CsvRunHistoryStore : IRunHistoryStore
{
    // 2.1.x kept this file in the Logs folder (the legacyLogFolder every method is still handed). 2.2 keeps it in the
    // app data folder (AppPaths.GetHistoryFilePath) and carries an existing one across the first time it is needed.
    private const string LegacyFileName = "Compressarr_History.csv";
    private static readonly object MigrationLock = new();

    /// <summary>The history file to use. If there isn't one in the app data folder yet but 2.1.x left one in the old Logs
    /// folder, it is copied across, then the old copy is removed so there is a single live file (a stale duplicate left
    /// behind would otherwise come back after the history is cleared).</summary>
    internal static string ResolveHistoryFile(string legacyLogFolder)
    {
        var target = AppPaths.GetHistoryFilePath();
        if (File.Exists(target) || string.IsNullOrWhiteSpace(legacyLogFolder)) return target;

        lock (MigrationLock)
        {
            if (File.Exists(target)) return target;
            var legacy = Path.Combine(legacyLogFolder, LegacyFileName);
            if (!File.Exists(legacy)) return target;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Copy to a temp name first so a half-copied file is never mistaken for the real one.
            var staging = target + ".copying";
            File.Copy(legacy, staging, overwrite: true);
            File.Move(staging, target);
            try { File.Delete(legacy); } catch { /* still readable there; nothing depends on it going */ }
        }

        return target;
    }
    private static readonly string[] Header =
    {
        "yyyy", "mm", "dd", "BegSize", "EndSize", "FileCount", "ProcessHours", "ProcessMinutes", "ProcessSeconds",
        "RunNumber", "ReportFileName", "ErrorCount", "WarningCount", "RedirectCount", "MovieCount", "TvCount",
        "AvgFps", "HandBrakeCount", "FfmpegCount", "PassthroughCount", "LaneCounts", "PresetCounts"
    };

    public void AppendRun(string legacyLogFolder, RunHistoryRecord record)
    {
        var historyFile = ResolveHistoryFile(legacyLogFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(historyFile)!);

        var isNew = !File.Exists(historyFile);
        using var writer = new StreamWriter(historyFile, append: true);
        if (isNew)
        {
            writer.WriteLine(string.Join(",", Header));
        }

        writer.WriteLine(Row(record));
    }

    /// <summary>The whole history as CSV text (the layout earlier versions kept on disk), for the History page's "Export as CSV".</summary>
    public static string Export(IEnumerable<RunHistoryRecord> runs) =>
        string.Join("\r\n", new[] { string.Join(",", Header) }.Concat(runs.Select(Row))) + "\r\n";

    private static string Row(RunHistoryRecord record)
    {
        var fields = new[]
        {
            record.Year.ToString(CultureInfo.InvariantCulture),
            record.Month.ToString("00", CultureInfo.InvariantCulture),
            record.Day.ToString("00", CultureInfo.InvariantCulture),
            record.BeginSizeGb.ToString(CultureInfo.InvariantCulture),
            record.EndSizeGb.ToString(CultureInfo.InvariantCulture),
            record.FileCount.ToString(CultureInfo.InvariantCulture),
            record.ProcessHours.ToString(CultureInfo.InvariantCulture),
            record.ProcessMinutes.ToString(CultureInfo.InvariantCulture),
            record.ProcessSeconds.ToString(CultureInfo.InvariantCulture),
            record.RunNumber.ToString(CultureInfo.InvariantCulture),
            record.ReportFileName,
            record.ErrorCount.ToString(CultureInfo.InvariantCulture),
            record.WarningCount.ToString(CultureInfo.InvariantCulture),
            record.RedirectCount.ToString(CultureInfo.InvariantCulture),
            // Blank = unknown (see RunHistoryRecord.MovieCount).
            record.MovieCount?.ToString(CultureInfo.InvariantCulture) ?? "",
            record.TvCount?.ToString(CultureInfo.InvariantCulture) ?? "",
            record.AvgFps?.ToString(CultureInfo.InvariantCulture) ?? "",
            record.HandBrakeCount?.ToString(CultureInfo.InvariantCulture) ?? "",
            record.FfmpegCount?.ToString(CultureInfo.InvariantCulture) ?? "",
            record.PassthroughCount?.ToString(CultureInfo.InvariantCulture) ?? "",
            RunBreakdown.Encode(record.LaneCounts),
            RunBreakdown.Encode(record.PresetCounts)
        };
        return string.Join(",", fields);
    }

    public IReadOnlyList<RunHistoryRecord> GetHistory(string legacyLogFolder)
    {
        var historyFile = ResolveHistoryFile(legacyLogFolder);
        if (!File.Exists(historyFile)) return Array.Empty<RunHistoryRecord>();

        var lines = File.ReadAllLines(historyFile);
        var results = new List<RunHistoryRecord>();
        LastSkippedRows = 0;

        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            if (parts.Length < 9) { LastSkippedRows++; continue; }   // a truncated line (the app died mid-write) is skipped, not trusted

            // A row whose numbers do not parse is skipped on its own: one damaged line must not take the whole history down with it.
            try
            {

            // RunNumber/ReportFileName/ErrorCount/WarningCount were all added later - rows written
            // before then simply don't have columns 9-12, so default them rather than
            // rejecting/mis-parsing older rows.
            var runNumber = parts.Length > 9 && int.TryParse(parts[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rn) ? rn : 0;
            var reportFileName = parts.Length > 10 ? parts[10] : "";
            var errorCount = parts.Length > 11 && int.TryParse(parts[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ec) ? ec : 0;
            var warningCount = parts.Length > 12 && int.TryParse(parts[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out var wc) ? wc : 0;
            // Appended at the END (never inserted) so older builds keep reading every earlier column.
            var redirectCount = parts.Length > 13 && int.TryParse(parts[13], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rc) ? rc : 0;
            // Movie / TV counts: blank or missing = unknown, not zero.
            int? movieCount = parts.Length > 14 && int.TryParse(parts[14], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mc) ? mc : null;
            int? tvCount = parts.Length > 15 && int.TryParse(parts[15], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tc) ? tc : null;
            // Speed, engines, lanes and profiles: appended after the split, blank or missing = unknown.
            double? avgFps = parts.Length > 16 && double.TryParse(parts[16], NumberStyles.Float, CultureInfo.InvariantCulture, out var af) && af > 0 ? af : null;

            results.Add(new RunHistoryRecord(
                Year: int.Parse(parts[0], CultureInfo.InvariantCulture),
                Month: int.Parse(parts[1], CultureInfo.InvariantCulture),
                Day: int.Parse(parts[2], CultureInfo.InvariantCulture),
                BeginSizeGb: double.Parse(parts[3], CultureInfo.InvariantCulture),
                EndSizeGb: double.Parse(parts[4], CultureInfo.InvariantCulture),
                FileCount: int.Parse(parts[5], CultureInfo.InvariantCulture),
                ProcessHours: int.Parse(parts[6], CultureInfo.InvariantCulture),
                ProcessMinutes: int.Parse(parts[7], CultureInfo.InvariantCulture),
                ProcessSeconds: int.Parse(parts[8], CultureInfo.InvariantCulture),
                RunNumber: runNumber,
                ReportFileName: reportFileName,
                ErrorCount: errorCount,
                WarningCount: warningCount,
                RedirectCount: redirectCount,
                MovieCount: movieCount,
                TvCount: tvCount,
                AvgFps: avgFps,
                HandBrakeCount: OptionalCount(parts, 17),
                FfmpegCount: OptionalCount(parts, 18),
                PassthroughCount: OptionalCount(parts, 19),
                LaneCounts: parts.Length > 20 ? RunBreakdown.Decode(parts[20]) : null,
                PresetCounts: parts.Length > 21 ? RunBreakdown.Decode(parts[21]) : null));
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                LastSkippedRows++;
            }
        }

        return results;
    }

    /// <summary>How many lines the last <see cref="GetHistory"/> skipped because they were truncated or not numbers.</summary>
    public int LastSkippedRows { get; private set; }

    private static int? OptionalCount(string[] parts, int index) =>
        parts.Length > index && int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 ? n : null;

    public void ClearHistory()
    {
        try { File.Delete(AppPaths.GetHistoryFilePath()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use: the next clear tries again */ }
    }

    public int GetRunCount(string runCountPath)
    {
        if (!File.Exists(runCountPath)) return 0;
        var raw = File.ReadAllText(runCountPath).Trim();
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : 0;
    }

    public void IncrementRunCount(string runCountPath)
    {
        var count = GetRunCount(runCountPath) + 1;
        var folder = Path.GetDirectoryName(runCountPath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        File.WriteAllText(runCountPath, count.ToString(CultureInfo.InvariantCulture));
    }
}
