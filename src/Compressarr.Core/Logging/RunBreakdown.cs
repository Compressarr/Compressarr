using System.Text;
using Compressarr.Core.Conversion;

namespace Compressarr.Core.Logging;

/// <summary>What a run's counted files were made of, worked out when the run finishes and recorded with it in the history: how many each
/// encoder produced, how many each lane and profile handled, and a rough average speed. The History page's Busiest Lane, HandBrake vs ffmpeg,
/// Passed Through, Most Used Profile and Average Speed cards are made from these. The three engine counts add up to the run's FileCount; the
/// lane counts too; the profile counts leave out passed-through files (a copy is not a profile).</summary>
public sealed record RunBreakdown(
    int HandBrake,
    int FFmpeg,
    int Passthrough,
    IReadOnlyDictionary<string, int> Lanes,
    IReadOnlyDictionary<string, int> Presets,
    double? AvgFps)
{
    /// <summary>Breaks down the files a history row counts (see ConversionResult.CountsTowardTotals). A file belongs to the lane that processed it,
    /// not the one whose library it landed in, and to the profile that really encoded it (the HandBrake fallback for a Dolby Vision file an ffmpeg
    /// lane handed over).</summary>
    public static RunBreakdown From(IReadOnlyCollection<ConversionResult> counted)
    {
        var lanes = new Dictionary<string, int>(StringComparer.Ordinal);
        var presets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in counted)
        {
            var lane = r.HomeLaneName ?? r.LaneId;
            lanes[lane] = lanes.GetValueOrDefault(lane) + 1;
            if (!r.IsPassthrough && !string.IsNullOrWhiteSpace(r.PresetName)) presets[r.PresetName!] = presets.GetValueOrDefault(r.PresetName!) + 1;
        }

        return new RunBreakdown(
            counted.Count(r => r.Engine == ResultEngine.HandBrake),
            counted.Count(r => r.Engine == ResultEngine.FFmpeg),
            counted.Count(r => r.Engine == ResultEngine.Passthrough),
            lanes, presets, AverageFps(counted));
    }

    /// <summary>One rough speed for a set of files: each file's own average fps weighted by how long it took, which is the same as total frames over
    /// total time. Files with no reading (a copy, a failure, an encoder that gave none) are left out; null when none has one. Never 0.</summary>
    public static double? AverageFps(IEnumerable<ConversionResult> results)
    {
        double weighted = 0, seconds = 0;
        foreach (var r in results)
        {
            if (r.AvgFps is not { } fps || fps <= 0) continue;
            var took = Math.Max(Math.Abs((r.EndTime - r.StartTime).TotalSeconds), 1);
            weighted += fps * took;
            seconds += took;
        }
        return seconds > 0 ? Math.Round(weighted / seconds, 2) : null;
    }

    // ---- the history file's text form: "name:count|name:count", biggest first. Names may hold anything, so the characters the format and
    // the CSV itself use are written as %XX. A known-empty list is "-" and a blank is unknown (a row written before these columns existed).
    private const string KnownEmpty = "-";

    public static string Encode(IReadOnlyDictionary<string, int>? counts)
    {
        if (counts is null) return "";
        if (counts.Count == 0) return KnownEmpty;
        return string.Join("|", counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => Escape(kv.Key) + ":" + kv.Value));
    }

    public static IReadOnlyDictionary<string, int>? Decode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (text == KnownEmpty) return result;
        foreach (var part in text.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = part.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(part[(colon + 1)..], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 0) continue;
            var name = Uri.UnescapeDataString(part[..colon]);
            result[name] = result.GetValueOrDefault(name) + n;
        }
        return result;
    }

    private static string Escape(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is '%' or ',' or '|' or ':' or '\r' or '\n' or '"') sb.Append('%').Append(((int)c).ToString("X2"));
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
