using System.Net;
using System.Reflection;
using System.Text;
using Compressarr.Core.Conversion;

namespace Compressarr.Core.Reporting;

public interface IHtmlReportGenerator
{
    /// <summary>Builds a fully self-contained HTML report — inline CSS, base64-embedded logo/
    /// favicon, no external references. All user-controlled text is HTML-encoded before
    /// interpolation. Layout ported from v1.1's New-CompressarrReport/
    /// Get-CompressarrLaneReportSection (Compressarr.Reporting.psm1) - the user preferred that
    /// format over v2's original dark-theme report.</summary>
    string Generate(ReportModel model);
}

public sealed class HtmlReportGenerator : IHtmlReportGenerator
{
    private const string LogoResourceName = "Compressarr.Core.Assets.CompressarrLogo.png";
    private const string FaviconResourceName = "Compressarr.Core.Assets.CompressarrFavicon.ico";

    public string Generate(ReportModel model)
    {
        var totalFiles = model.Lanes.Sum(l => l.Results.Count);
        var totalBeg = Math.Round(model.TotalBeforeGb, 3);
        var totalEnd = Math.Round(model.TotalAfterGb, 3);
        var totalSavings = Math.Round(totalBeg - totalEnd, 3);
        var savingsPct = totalBeg > 0 ? Math.Round(100 - (totalEnd / totalBeg) * 100, 2) : 0;
        var errorCount = model.ErrorCount;
        var hasAnyProblem = errorCount > 0 || model.HasAnyLaneProblems;

        var logoTag = LoadEmbeddedBase64(LogoResourceName) is { } logoB64
            ? $"<img src=\"data:image/png;base64,{logoB64}\" alt=\"Compressarr\" class=\"logo\" />"
            : "";
        var faviconTag = LoadEmbeddedBase64(FaviconResourceName) is { } faviconB64
            ? $"<link rel=\"icon\" type=\"image/x-icon\" href=\"data:image/x-icon;base64,{faviconB64}\">"
            : "";
        var runLabel = model.RunNumber > 0 ? $"Run #{model.RunNumber}:" : "Run:";
        var timestamp = model.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss");

        string statusBanner;
        if (hasAnyProblem)
        {
            var bannerParts = new List<string>();
            if (errorCount > 0) bannerParts.Add($"{errorCount} error(s) occurred");
            var laneProblemCount = model.Lanes.Sum(l => l.LaneProblems.Count);
            if (laneProblemCount > 0) bannerParts.Add($"{laneProblemCount} lane problem(s)");
            statusBanner = $"<div class=\"banner err\">{string.Join(", ", bannerParts)} - see the per-lane sections below.</div>";
        }
        else
        {
            statusBanner = "<div class=\"banner ok\">Run completed with no errors.</div>";
        }

        var retryBanner = model.RetriesSucceeded > 0
            ? $"<div class=\"banner info\">{model.RetriesSucceeded} previously deferred/stranded file(s) (a failed move, a failed companion-file move, or a deferred Sonarr/Radarr rescan or source-folder cleanup) were retried successfully this pass.</div>"
            : "";

        // A deliberate lane redirect is neither an error nor a warning, so it gets its own banner and its own
        // colour (never the red/yellow of a problem): which files, and from which lane to which.
        var redirected = model.RedirectedResults;
        var redirectBanner = redirected.Count == 0
            ? ""
            : "<div class=\"banner redirect\">" + redirected.Count + " file(s) redirected to a different lane" +
              "<ul class=\"redirect-list\">" +
              string.Concat(redirected.Select(r => "<li>" + WebUtility.HtmlEncode(r.FileName) + ": " + WebUtility.HtmlEncode(r.HomeLaneName ?? r.LaneId) + " &rarr; " + WebUtility.HtmlEncode(r.RedirectedToLaneName ?? r.RedirectedToLaneId ?? "")  + "</li>")) +
              "</ul></div>";

        var sb = new StringBuilder();
        sb.Append($@"<!DOCTYPE html>

<html>
<head>
<meta charset=""utf-8"" />
<title>Compressarr Report - {WebUtility.HtmlEncode(timestamp)}</title>
{faviconTag}
<style>
  body {{ font-family: Segoe UI, Verdana, sans-serif; margin: 2rem; color: #1c1c1c; background: #fafafa; }}
  h1 {{ margin-bottom: 0; }}
  .header {{ display: flex; align-items: center; gap: 0.75rem; }}
  .header h1 {{ line-height: 1; position: relative; top: -12px; }}
  .logo {{ width: 48px; height: 48px; }}
  .muted {{ color: #666; font-weight: normal; font-size: 0.85em; }}
  .banner {{ padding: 0.75rem 1rem; border-radius: 6px; margin: 1rem 0; font-weight: 600; }}
  .banner.ok {{ background: #e3f7e8; color: #16693a; }}
  .banner.err {{ background: #fdeaea; color: #a1231e; }}
  .banner.info {{ background: #e8f1fb; color: #205081; }}
  .banner.redirect {{ background: #efeafc; color: #3f3399; }}
  .redirect-list {{ margin: 0.4rem 0 0; padding-left: 1.2rem; font-weight: 400; }}
  .table-wrap {{ overflow-x: auto; margin: 0.5rem 0 1.5rem 0; }}
  table {{ border-collapse: collapse; width: 100%; min-width: 640px; margin: 0; background: #fff; }}
  th, td {{ border: 1px solid #ddd; padding: 6px 10px; text-align: left; font-size: 0.9em; }}
  th {{ background: #2c3e50; color: #fff; }}
  tr.err {{ background: #fdeaea; }}
  tr.warn {{ background: #fdf6e3; }}
  tr.redir {{ background: #f3effd; }}
  .redir-text {{ color: #4a3fa8; }}
  .warn-text {{ color: #8a6414; }}
  .err-code {{ border-bottom: 1px dotted #a1231e; cursor: help; }}
  .enc-tag {{ font-size: 0.7rem; font-weight: 700; padding: 1px 7px; border-radius: 999px; background: #fdf3e0; color: #8a6414; white-space: nowrap; }}
  .pt-tag {{ font-size: 0.7rem; font-weight: 700; padding: 1px 7px; border-radius: 999px; background: #d6f3ef; color: #0b6b63; white-space: nowrap; }}
  .lane-problem {{ background: #fdeaea; color: #a1231e; padding: 0.4rem 0.75rem; border-radius: 6px; margin: 0.25rem 0; }}
  .summary-grid {{ display: flex; gap: 2rem; flex-wrap: wrap; margin: 1rem 0; }}
  .stat {{ background: #fff; border: 1px solid #ddd; border-radius: 6px; padding: 0.75rem 1.25rem; min-width: 140px; }}
  .stat .label {{ font-size: 0.8em; color: #666; }}
  .stat .value {{ font-size: 1.4em; font-weight: 700; }}
  .close-btn {{
    position: fixed; top: 1rem; right: 1rem; width: 2.25rem; height: 2.25rem;
    border-radius: 50%; border: 1px solid #ddd; background: #fff; color: #666;
    font-size: 1.25rem; line-height: 1; cursor: pointer; box-shadow: 0 1px 4px rgba(0,0,0,0.15);
  }}
  .close-btn:hover {{ background: #f0f0f0; color: #1c1c1c; }}
</style>
</head>
<body>
<!-- Reports have no way to get back to Compressarr's own UI, and there's nothing else on the
     page that closes the tab - most noticeable in PWA/standalone mode, which has no browser
     chrome at all. window.close() is a plain browser API with no fallback: most browsers only
     honor it on a tab the page itself opened via script, so it may silently no-op on a tab opened
     via a plain link (which is how every report link in this app opens one) depending on the
     browser - harmless either way, just not guaranteed universally. -->
<button type=""button"" class=""close-btn"" title=""Close this tab"" onclick=""window.close()"">&times;</button>
<div class=""header"">
  {logoTag}
  <h1>Compressarr Report</h1>
</div>
<p class=""muted"">{WebUtility.HtmlEncode(runLabel)} {WebUtility.HtmlEncode(timestamp)} &nbsp;|&nbsp; Duration: {model.RunTime.Hours}h {model.RunTime.Minutes}m {model.RunTime.Seconds}s</p>
{statusBanner}
{retryBanner}
{redirectBanner}
<div class=""summary-grid"">
  <div class=""stat""><div class=""label"">Files processed</div><div class=""value"">{totalFiles}</div></div>
  <div class=""stat""><div class=""label"">Before</div><div class=""value"">{totalBeg} GB</div></div>
  <div class=""stat""><div class=""label"">After</div><div class=""value"">{totalEnd} GB</div></div>
  <div class=""stat""><div class=""label"">Saved</div><div class=""value"">{totalSavings} GB ({savingsPct}%)</div></div>
  <div class=""stat""><div class=""label"">Errors</div><div class=""value"">{errorCount}</div></div>
</div>
<h2>By lane</h2>
");

        foreach (var lane in model.Lanes)
        {
            AppendLaneSection(sb, lane, model.SummaryLogFilePath);
        }

        if (model.Today is not null || model.ThisMonth is not null || model.ThisYear is not null)
        {
            AppendHistorySection(sb, model);
        }

        // Viewed through the app (http), the log links go to /api/logs/ instead of the file:// path a browser would block.
        sb.Append("<script>(function(){if(!/^https?:$/.test(location.protocol))return;document.querySelectorAll('a[data-log]').forEach(function(a){a.href='/api/logs/'+encodeURIComponent(a.getAttribute('data-log'));});})();</script>\n");
        sb.Append("</body>\n</html>\n");
        return sb.ToString();
    }

    private static void AppendLaneSection(StringBuilder sb, LaneReportSection lane, string? summaryLogFilePath)
    {
        // Lane/run-level problems (missing preset, missing Output folder, FileBot path not found)
        // aren't tied to any one file, so they're shown here instead of in the Status column - a
        // lane can have one of these AND still have processed files normally otherwise (e.g. a
        // missing TV preset doesn't stop movies in the same lane from working), so this renders
        // regardless of whether Results is empty.
        if (lane.LaneProblems.Count > 0)
        {
            sb.Append($"<h3>{WebUtility.HtmlEncode(lane.LaneDisplayName)}</h3>\n");
            foreach (var problem in lane.LaneProblems)
            {
                var problemCodeNumber = (int)problem;
                var problemDescription = WebUtility.HtmlEncode(problem.Describe());
                sb.Append($"<p class=\"lane-problem\"><span class=\"err-code\" title=\"{problemDescription}\">ERROR {problemCodeNumber}</span></p>\n");
            }
        }
        else
        {
            sb.Append($"<h3>{WebUtility.HtmlEncode(lane.LaneDisplayName)}</h3>\n");
        }

        if (lane.Results.Count == 0)
        {
            sb.Append("<p class=\"muted\">No files processed.</p>\n");
            return;
        }

        var beg = Math.Round(lane.Results.Sum(r => r.CountedBeginGb), 3);
        var end = Math.Round(lane.Results.Sum(r => r.CountedEndGb), 3);
        var passedThrough = lane.Results.Count(r => r.IsPassthrough);
        var passthroughNote = passedThrough > 0 ? $" ({passedThrough} passed through, not counted in the sizes)" : "";

        sb.Append($"<p class=\"muted\">{lane.Results.Count} file(s){passthroughNote}, {beg} GB &rarr; {end} GB</p>\n");
        sb.Append("<div class=\"table-wrap\">\n<table>\n  <thead><tr><th>File</th><th>Type</th><th>Preset</th><th>Before</th><th>After</th><th>Savings</th><th>Duration</th><th>Status</th><th>Sonarr/Radarr</th></tr></thead>\n  <tbody>\n");

        foreach (var r in lane.Results)
        {
            var hasWarning = r.Success && !string.IsNullOrEmpty(r.PostProcessWarning);
            var wasRedirected = r.RedirectedToLaneId is not null;
            var rowClass = !r.Success ? " class=\"err\"" : hasWarning ? " class=\"warn\"" : wasRedirected ? " class=\"redir\"" : "";
            var savings = Math.Round(r.BeginSizeGb - r.EndSizeGb, 3);
            // EndTime can land a hair before StartTime on some systems (clock adjustment mid-encode) -
            // same negate-if-negative clamp RunOrchestrator already uses for the run-level duration.
            var duration = r.EndTime - r.StartTime;
            if (duration < TimeSpan.Zero) duration = duration.Negate();
            string statusHtml;
            if (r.Success)
            {
                statusHtml = "OK";
            }
            else if (r.ErrorCode is { } code)
            {
                // A compact "ERROR <code>" badge with the full description in a native title
                // tooltip (a "help bubble") keeps the table itself clean regardless of how long
                // the description is - see ReportErrorCode's own doc comment for why this replaced
                // plain "ERROR" here.
                var codeNumber = (int)code;
                var description = WebUtility.HtmlEncode(code.Describe());
                statusHtml = $"<span class=\"err-code\" title=\"{description}\">ERROR {codeNumber}</span>";

                // HandBrake's own detail log is only relevant for a genuine encode failure - for
                // every other code (a move failure, a missing preset) HandBrake had nothing to do
                // with the problem, and linking its log there was actively misleading (a real bug
                // found live: the report always linked it for any failure, even when HandBrake had
                // succeeded fine and the actual problem was a destination move). Those codes link
                // this run's own Compressarr summary log instead - it has the real exception
                // message for this file (search it by filename), which HandBrake's log never did.
                var (linkTarget, linkText) = code is ReportErrorCode.EncodeFailed or ReportErrorCode.EncodedLengthMismatch
                    ? (r.DetailLogFile, "Full Details")
                    : (summaryLogFilePath, "Full Details (Compressarr log)");
                if (!string.IsNullOrEmpty(linkTarget) && File.Exists(linkTarget))
                {
                    // The link is a file:// path, which is right when the report is opened straight from disk (the
                    // "open report after run" case). A browser refuses to follow a file:// link from a page served over
                    // http though, so the log's name is carried along and the script at the end of the report points
                    // the link at the app's own /api/logs/ route when the report is viewed through the app (the History
                    // page, or from another device).
                    var detailUri = new Uri(linkTarget).AbsoluteUri;
                    var logName = WebUtility.HtmlEncode(Path.GetFileName(linkTarget));
                    statusHtml += $"<br><a href=\"{detailUri}\" data-log=\"{logName}\" target=\"_blank\" rel=\"noopener\">{linkText}</a>";
                }
            }
            else
            {
                // No classified code (shouldn't normally happen now that every failure branch
                // sets one, but kept as a safe fallback) - same plain "ERROR" the report always
                // showed before this classification existed.
                statusHtml = "ERROR";
            }
            if (hasWarning)
            {
                // A secondary post-process step (companion files, Sonarr/Radarr unmonitor) had a
                // problem even though the conversion itself succeeded - flagged distinctly from a
                // real failure (no red row, doesn't count toward the error total) so it doesn't
                // get missed, without overstating what actually went wrong.
                statusHtml += $"<br><span class=\"warn-text\">&#9888; {WebUtility.HtmlEncode(r.PostProcessWarning)}</span>";
            }
            if (wasRedirected)
            {
                // A lane change the user chose, so it is stated plainly rather than hidden: where it landed
                // and where it came from.
                statusHtml += $"<br><span class=\"redir-text\">&#8618; Landed in {WebUtility.HtmlEncode(r.RedirectedToLaneName ?? r.RedirectedToLaneId ?? "")} - redirected from {WebUtility.HtmlEncode(r.HomeLaneName ?? r.LaneId)}</span>";
            }
            // Em dash, matching v1.1: blank whenever Sonarr/Radarr integration wasn't attempted

            // for this file - either the conversion failed, or neither service is enabled for
            // this content type - not just when it succeeded/failed.
            var arrStatus = string.IsNullOrEmpty(r.ArrStatus) ? "—" : r.ArrStatus;

            sb.Append($"    <tr{rowClass}>");
            sb.Append($"<td>{WebUtility.HtmlEncode(r.FileName)}</td>");
            sb.Append($"<td>{WebUtility.HtmlEncode(r.ContentType)}</td>");
            var encoderTag = string.IsNullOrEmpty(r.EncoderLabel) ? "" : $" <span class=\"enc-tag\">{WebUtility.HtmlEncode(r.EncoderLabel)}</span>";
            // A passed-through file shows the teal Passthrough badge instead of a preset name.
            sb.Append(r.IsPassthrough
                ? "<td><span class=\"pt-tag\">Passthrough</span></td>"
                : $"<td>{WebUtility.HtmlEncode(r.PresetName ?? "")}{encoderTag}</td>");
            sb.Append($"<td>{r.BeginSizeGb} GB</td>");
            sb.Append($"<td>{r.EndSizeGb} GB</td>");
            sb.Append($"<td>{savings} GB</td>");
            sb.Append($"<td>{duration.Hours}h {duration.Minutes}m {duration.Seconds}s</td>");
            sb.Append($"<td>{statusHtml}</td>");
            sb.Append($"<td>{WebUtility.HtmlEncode(arrStatus)}</td>");
            sb.Append("</tr>\n");
        }

        sb.Append("  </tbody>\n</table>\n</div>\n");
    }

    private static void AppendHistorySection(StringBuilder sb, ReportModel model)
    {
        sb.Append("<h2>History</h2>\n<div class=\"table-wrap\">\n<table>\n  <thead><tr><th>Period</th><th>Before</th><th>After</th><th>Savings</th><th>Files</th><th>Time</th></tr></thead>\n  <tbody>\n");

        void Row(string label, HistoryRollup? rollup)
        {
            if (rollup is null) return;
            var pct = rollup.BeforeGb > 0 ? Math.Round(100 - (rollup.AfterGb / rollup.BeforeGb) * 100, 2) : 0;
            var time = TimeSpan.FromSeconds(rollup.TotalTimeSeconds);
            sb.Append($"    <tr><td>{WebUtility.HtmlEncode(label)}</td><td>{rollup.BeforeGb} GB</td><td>{rollup.AfterGb} GB</td><td>{pct}%</td><td>{rollup.FileCount}</td><td>{(int)time.TotalHours}h {time.Minutes}m {time.Seconds}s</td></tr>\n");
        }

        Row("Today", model.Today);
        Row("This month", model.ThisMonth);
        Row("This year", model.ThisYear);

        sb.Append("  </tbody>\n</table>\n</div>\n");
    }

    private static string? LoadEmbeddedBase64(string resourceName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null) return null;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Convert.ToBase64String(ms.ToArray());
    }
}
