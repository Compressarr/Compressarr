using Compressarr.Core.Conversion;
using Compressarr.Core.Reporting;

namespace Compressarr.Core.Notifications;

/// <summary>Fills {token} placeholders in a NotificationSettings message template (Title or Body)
/// with this run's actual numbers - the one substitution path behind every NotificationMessageStyle,
/// built-in or Custom, so neither can drift from the other's token vocabulary. An unrecognized
/// {token} is left as literal text rather than throwing or being blanked out, so a typo in a
/// user's Custom template degrades to slightly odd-looking text instead of losing the notification
/// entirely - matching every notifier's own "must never fail an otherwise-successful run"
/// contract.</summary>
public static class NotificationMessageRenderer
{
    /// <summary>machineName is the cleaned name for {machine} (null or empty renders nothing). The caller passes it
    /// only when "Include machine name" is ticked, so an unticked box never leaks a saved name into a template.</summary>
    public static string Render(string template, NotificationOutcome outcome, ReportModel report, string reportFilePath, string? machineName = null)
    {
        var savedGb = report.TotalBeforeGb - report.TotalAfterGb;
        var savedPct = report.TotalBeforeGb > 0 ? 100 - (report.TotalAfterGb / report.TotalBeforeGb * 100) : 0;

        var result = WithErrorToken(template, ErrorSummary(report));
        foreach (var (token, value) in Tokens(outcome, report, reportFilePath, savedGb, savedPct, machineName))
        {
            result = result.Replace(token, value);
        }
        return result;
    }

    private const string ErrorToken = "{error}";
    private const int ErrorKindsShown = 2;

    /// <summary>The short text for {error}: every error this run, one entry per kind ("Error 102: File move failure", with " (x3)" when
    /// it happened more than once), in code order, the first two kinds and then "(+N more)". Both a failed file and a lane that could not
    /// run count. A failure that has no numbered code reads "Error: see the report". Empty when the run had no errors.</summary>
    public static string ErrorSummary(ReportModel report)
    {
        var counts = new SortedDictionary<int, int>();   // code, or int.MaxValue for "no code"
        void Count(ReportErrorCode? code)
        {
            var key = code is { } c ? (int)c : int.MaxValue;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        foreach (var lane in report.Lanes)
        {
            foreach (var problem in lane.LaneProblems) Count(problem);
            foreach (var result in lane.Results.Where(r => !r.Success)) Count(result.ErrorCode);
        }

        if (counts.Count == 0) return "";

        var parts = counts.Take(ErrorKindsShown).Select(kv =>
        {
            var text = kv.Key == int.MaxValue ? "Error: see the report" : $"Error {kv.Key}: {((ReportErrorCode)kv.Key).ShortLabel()}";
            return kv.Value > 1 ? $"{text} (x{kv.Value})" : text;
        });
        var summary = string.Join("; ", parts);
        return counts.Count > ErrorKindsShown ? $"{summary} (+{counts.Count - ErrorKindsShown} more)" : summary;
    }

    /// <summary>Puts the error text into {error}. With no errors the token is nothing: in a template of several lines the whole line it is on
    /// is left out (so "Problem: {error}" leaves no dangling "Problem:"), and in a one-line template (a title, a short body) the token is
    /// simply removed, so the message is never emptied entirely.</summary>
    private static string WithErrorToken(string template, string errorText)
    {
        if (!template.Contains(ErrorToken, StringComparison.Ordinal)) return template;
        if (errorText.Length > 0) return template.Replace(ErrorToken, errorText);

        if (!template.Contains('\n')) return template.Replace(ErrorToken, "").Trim();

        return string.Join('\n', template.Split('\n').Where(line => !line.Contains(ErrorToken, StringComparison.Ordinal)));
    }

    private static IEnumerable<(string Token, string Value)> Tokens(
        NotificationOutcome outcome, ReportModel report, string reportFilePath, double savedGb, double savedPct, string? machineName)
    {
        yield return ("{machine}", machineName ?? "");
        yield return ("{run_number}", report.RunNumber.ToString());
        yield return ("{files}", report.TotalFiles.ToString());
        yield return ("{saved_gb}", savedGb.ToString("0.##"));
        yield return ("{saved_pct}", savedPct.ToString("0.##"));
        yield return ("{before_gb}", report.TotalBeforeGb.ToString("0.##"));
        yield return ("{after_gb}", report.TotalAfterGb.ToString("0.##"));
        yield return ("{duration}", NotificationFormatting.FormatDuration(report.RunTime));
        yield return ("{outcome}", NotificationFormatting.FormatOutcome(outcome));
        yield return ("{error_count}", report.ErrorCount.ToString());
        yield return ("{warning_count}", report.WarningCount.ToString());
        yield return ("{retries_succeeded}", report.RetriesSucceeded.ToString());
        yield return ("{report_path}", reportFilePath);
        yield return ("{today_files}", (report.Today?.FileCount ?? 0).ToString());
        yield return ("{today_saved_gb}", RollupSavedGb(report.Today).ToString("0.##"));
        yield return ("{month_files}", (report.ThisMonth?.FileCount ?? 0).ToString());
        yield return ("{month_saved_gb}", RollupSavedGb(report.ThisMonth).ToString("0.##"));
        yield return ("{year_files}", (report.ThisYear?.FileCount ?? 0).ToString());
        yield return ("{year_saved_gb}", RollupSavedGb(report.ThisYear).ToString("0.##"));
        // Deliberately absent from every built-in NotificationMessagePresets template - the only
        // token that can put a media filename in front of a third-party service, so it only ever
        // reaches a real message if a user's own Custom template asks for it by name. Every
        // channel type still gets it for free the moment a Custom template uses it, same as every
        // other token here - this isn't a channel-level opt-in, just a template-content one.
        yield return ("{file_list}", string.Join('\n', report.Lanes.SelectMany(l => l.Results).Select(r => r.FileName)));
    }

    private static double RollupSavedGb(HistoryRollup? rollup) => rollup is null ? 0 : rollup.BeforeGb - rollup.AfterGb;
}
