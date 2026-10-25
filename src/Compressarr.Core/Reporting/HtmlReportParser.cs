using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Compressarr.Core.Reporting;

/// <summary>One file row read back out of a saved report.</summary>
public sealed record ParsedReportFile(
    string Lane, string Name, string ContentType, string? Preset, string? EncoderLabel, bool Passthrough, double BeforeGb, double AfterGb, double Seconds,
    bool Success, int? ErrorCode, string? WarningText, IReadOnlyList<int> WarningCodes, string? ArrStatus, string? RedirectedToLane, string? HomeLane)
{
    /// <summary>The tool: passthrough, ffmpeg, or HandBrake (no tag, or HandBrake's own Dolby Vision fallback tag).</summary>
    public string Engine => Passthrough ? "passthrough" : EncoderLabel is not null && EncoderLabel.StartsWith("ffmpeg", StringComparison.OrdinalIgnoreCase) ? "ffmpeg" : "handbrake";

    /// <summary>Whether the history counts the file: converted and filed (OK), or converted but not moved (ERROR 102-104).</summary>
    public bool Counted => Success || ErrorCode is >= 102 and <= 104;
}

/// <summary>Reads the file rows of a report HTML file saved by an earlier version. Only the 3.0 import uses it, to fill in the lane, tool and profile of
/// runs recorded before those were kept (the report is the only place that knows them), and to give those runs their per-file rows.</summary>
public static partial class HtmlReportParser
{
    [GeneratedRegex("<h3>(.*?)</h3>|<tr([^>]*)>(.*?)</tr>", RegexOptions.Singleline)]
    private static partial Regex Blocks();

    [GeneratedRegex("<td[^>]*>(.*?)</td>", RegexOptions.Singleline)]
    private static partial Regex Cells();

    [GeneratedRegex("<span class=\"enc-tag\">(.*?)</span>", RegexOptions.Singleline)]
    private static partial Regex EncoderTag();

    [GeneratedRegex(@"^(\d+)h (\d+)m (\d+)s$")]
    private static partial Regex Duration();

    [GeneratedRegex(@"WARN (\d+)")]
    private static partial Regex WarnCode();

    [GeneratedRegex(@"^ERROR (\d+)")]
    private static partial Regex ErrorCode();

    public static IReadOnlyList<ParsedReportFile> Parse(string html)
    {
        var files = new List<ParsedReportFile>();
        var lane = "";
        foreach (Match block in Blocks().Matches(html))
        {
            if (block.Groups[1].Success) { lane = Text(block.Groups[1].Value); continue; }

            var cells = Cells().Matches(block.Groups[3].Value).Select(m => m.Groups[1].Value).ToList();
            if (cells.Count < 8) continue;                                   // the History table has 6 columns; a header row has none
            var type = Text(cells[1]);
            if (type != "Movie" && type != "TV Show") continue;

            var presetCell = cells[2];
            var passthrough = presetCell.Contains("class=\"pt-tag\"", StringComparison.Ordinal);
            string? label = null, preset = null;
            if (!passthrough)
            {
                var tag = EncoderTag().Match(presetCell);
                if (tag.Success) label = WebUtility.HtmlDecode(tag.Groups[1].Value).Trim();
                preset = Text(EncoderTag().Replace(presetCell, ""));
            }

            var status = Text(cells[7]);
            var success = status.StartsWith("OK", StringComparison.Ordinal);
            var errorMatch = ErrorCode().Match(status);
            var warnCodes = WarnCode().Matches(cells[7]).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().ToList();
            var warningText = cells[7].Contains("warn-text", StringComparison.Ordinal) ? WarningTextOf(cells[7]) : null;
            var redirect = Regex.Match(Text(cells[7]), @"Landed in (.+?) - redirected from (.+)$");

            files.Add(new ParsedReportFile(
                Lane: lane, Name: Text(cells[0]), ContentType: type,
                Preset: string.IsNullOrWhiteSpace(preset) ? null : preset, EncoderLabel: label, Passthrough: passthrough,
                BeforeGb: Gb(cells[3]), AfterGb: Gb(cells[4]), Seconds: Seconds(cells[6]),
                Success: success, ErrorCode: errorMatch.Success ? int.Parse(errorMatch.Groups[1].Value, CultureInfo.InvariantCulture) : null,
                WarningText: warningText, WarningCodes: warnCodes,
                ArrStatus: cells.Count > 8 && Text(cells[8]) is { Length: > 0 } arr && arr != "—" && arr != "—" ? arr : null,
                RedirectedToLane: redirect.Success ? redirect.Groups[1].Value : null,
                HomeLane: redirect.Success ? redirect.Groups[2].Value : null));
        }
        return files;
    }

    private static string Text(string cell) => WebUtility.HtmlDecode(Regex.Replace(cell, "<[^>]+>", " ")).Replace("  ", " ").Trim();

    private static string? WarningTextOf(string statusCell)
    {
        // Greedy to the last </span>: the badges inside are spans too, so the first closing tag is not the end of the warning.
        var span = Regex.Match(statusCell, "<span class=\"warn-text\">(.*)</span>", RegexOptions.Singleline);
        if (!span.Success) return null;
        var inner = Regex.Replace(span.Groups[1].Value, "<span class=\"warn-code\"[^>]*>.*?</span>", "", RegexOptions.Singleline);
        var text = WebUtility.HtmlDecode(Regex.Replace(inner, "<[^>]+>", " ")).Replace("⚠", "").Trim();
        return text.Length == 0 ? null : text;
    }

    private static double Gb(string cell)
    {
        var text = Text(cell).Replace("GB", "").Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static double Seconds(string cell)
    {
        var m = Duration().Match(Text(cell));
        return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60 + int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
    }
}
