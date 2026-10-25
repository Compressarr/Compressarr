using Microsoft.AspNetCore.Http;
using Compressarr.Core.Config;
using Compressarr.Core.Logging;
using Compressarr.Core.Reporting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

public static class HistoryEndpoints
{
    public static void MapHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/history", (
            IConfigStore configStore,
            IPathExpander pathExpander,
            IWebHistoryRollupCalculator rollupCalculator,
            IRunHistoryStore historyStore) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var logFilePath = pathExpander.Expand(config.Logging.LogFilePath);

            var rollups = rollupCalculator.Calculate(logFilePath);
            var runCount = historyStore.GetRunCount(AppPaths.GetRunCountFilePath());

            return Results.Json(new
            {
                rollups.Today,
                rollups.Last7Days,
                rollups.Last30Days,
                rollups.LastYear,
                rollups.AllTime,
                totalRunCount = runCount
            });
        });

        // Every run that produced results, oldest first, for the Overview's stat cards and charts - one compact array per run so a long
        // history stays small: [year, month, day, beforeGb, afterGb, files, seconds, runNumber, errors, warnings, redirected, movies|null,
        // tv|null, reportFile|null, endMinute|null]. A null movies/tv means the run's split is unknown. reportFile is only filled for the
        // most recent runs (the ones the page can link to) and only when the report still exists. endMinute is the minute of the day the
        // run ended (see RunTimes.EndMinuteOfDay), for the Today chart's three-hour blocks. Then avgFps|null, handBrakeFiles|null, ffmpegFiles|null,
        // passedThroughFiles|null, lanes|null and profiles|null (each [[name, files], ...]); null means the run predates them. The same "every file failed, nothing produced" rows the
        // period totals skip are left out here too, so the cards and the table always agree. today is the server's date, which the
        // period windows (Today, Last 7 days...) are measured from - the same windows /api/history uses.
        app.MapGet("/api/history/runs", (
            IConfigStore configStore,
            IPathExpander pathExpander,
            IRunHistoryStore historyStore) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var logFilePath = pathExpander.Expand(config.Logging.LogFilePath);
            var reportPath = pathExpander.Expand(config.Report.ReportPath);

            var runs = historyStore.GetHistory(logFilePath).Where(r => !r.IsAllFailed).ToList();
            const int linkable = 60;
            var firstLinkable = Math.Max(0, runs.Count - linkable);

            var rows = runs.Select((r, i) => new object?[]
            {
                r.Year, r.Month, r.Day,
                Math.Round(r.BeginSizeGb, 3), Math.Round(r.EndSizeGb, 3),
                r.FileCount,
                r.ProcessHours * 3600 + r.ProcessMinutes * 60 + r.ProcessSeconds,
                r.RunNumber, r.ErrorCount, r.WarningCount, r.RedirectCount,
                r.MovieCount, r.TvCount,
                // The report's link key: the run's id when the archive holds its report, else (CSV fallback) a saved copy that still exists.
                i < firstLinkable ? null
                    : r.HasReport ? r.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : !string.IsNullOrEmpty(r.ReportFileName) && File.Exists(Path.Combine(reportPath, r.ReportFileName)) ? r.ReportFileName : null,
                RunTimes.EndMinuteOfDay(r),
                r.AvgFps, r.HandBrakeCount, r.FfmpegCount, r.PassthroughCount, Pairs(r.LaneCounts), Pairs(r.PresetCounts)
            }).ToList();

            var now = DateTime.Now;
            return Results.Json(new { today = now.ToString("yyyy-MM-dd"), now = now.ToString("yyyy-MM-dd'T'HH:mm"), runs = rows });
        });

        // The Reports list. ?period= (today, d7, d30, year, all) is what the History page's Reports buttons ask for - the same windows as the
        // Overview's - and returns one page of it, newest first: ?pageSize= rows (25 by default, 200 at most) from ?page= (1 by default; one past the
        // end gives the last page). The full count is in X-Total-Count and the page returned in X-Page. Without a period it is every run within
        // the retention window, as before. A report's link is its run id.
        app.MapGet("/api/history/reports", (
            HttpContext http,
            string? period,
            int? page,
            int? pageSize,
            IConfigStore configStore,
            IPathExpander pathExpander,
            IRunHistoryStore historyStore,
            IRunArchive archive) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var logFilePath = pathExpander.Expand(config.Logging.LogFilePath);
            var reportPath = pathExpander.Expand(config.Report.ReportPath);
            var retention = config.Report.EffectiveUnseenErrorDays(config.Logging.RetentionDays);
            var today = DateTime.Now.Date;

            // The first day the list covers: "after today minus N days", like the Overview's windows.
            DateTime? periodStart = period switch
            {
                "today" => today,
                "d7" => today.AddDays(-6),
                "d30" => today.AddDays(-29),
                "year" => today.AddYears(-1).AddDays(1),
                "all" => DateTime.MinValue,
                _ => null
            };

            IReadOnlyList<ReportListEntry> entries;
            if (archive.IsAvailable())
            {
                entries = archive.ListReports(periodStart ?? (retention > 0 ? today.AddDays(-retention) : DateTime.MinValue));
            }
            else
            {
                entries = ReportListBuilder.Build(historyStore.GetHistory(logFilePath), retention, DateTime.Now, fileName => File.Exists(Path.Combine(reportPath, fileName)));
                if (periodStart is { } from) entries = entries.Where(e => e.Date >= from).ToList();
            }

            http.Response.Headers["X-Total-Count"] = entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (periodStart is null) return Results.Json(entries);

            var size = Math.Clamp(pageSize ?? 25, 1, 200);
            var pages = Math.Max(1, (entries.Count + size - 1) / size);
            var current = Math.Clamp(page ?? 1, 1, pages);
            http.Response.Headers["X-Page"] = current.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.Json(entries.Skip((current - 1) * size).Take(size).ToList());
        });

        // The runs that had an error or a warning, newest first, as list entries. ?scope=unseen is the ones not yet viewed (the sidebar badges count
        // the same runs); ?scope=recent is the latest 50 whether they were seen or not - the History page's "View recent errors".
        app.MapGet("/api/history/problems", (
            string? scope,
            IConfigStore configStore,
            IPathExpander pathExpander,
            IRunHistoryStore historyStore,
            IRunArchive archive) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var retention = config.Report.EffectiveUnseenErrorDays(config.Logging.RetentionDays);
            var unseen = !string.Equals(scope, "recent", StringComparison.OrdinalIgnoreCase);
            var viewed = config.UiState.HistoryViewedThroughRunNumber;
            var since = unseen ? (retention > 0 ? DateTime.Now.Date.AddDays(-retention) : DateTime.MinValue) : DateTime.MinValue;
            var limit = unseen ? 100 : 50;

            if (archive.IsAvailable()) return Results.Json(archive.ListProblemReports(unseen ? viewed : null, since, limit));

            var reportPath = pathExpander.Expand(config.Report.ReportPath);
            var all = ReportListBuilder.Build(historyStore.GetHistory(pathExpander.Expand(config.Logging.LogFilePath)), retention, DateTime.Now,
                fileName => File.Exists(Path.Combine(reportPath, fileName)));
            return Results.Json(all.Where(e => (e.ErrorCount > 0 || e.WarningCount > 0) && (!unseen || e.RunNumber > viewed)).Take(limit).ToList());
        });

        // The whole run history as a CSV file to keep, open in a spreadsheet or hand to a script (the layout earlier versions stored on disk).
        app.MapGet("/api/history/export.csv", (IConfigStore configStore, IPathExpander pathExpander, IRunHistoryStore historyStore) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var csv = CsvRunHistoryStore.Export(historyStore.GetHistory(pathExpander.Expand(config.Logging.LogFilePath)));
            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", $"Compressarr_History_{DateTime.Now:yyyy-MM-dd}.csv");
        });

        // The sidebar's History badges: runs not yet viewed that had an error, and that had a warning. Every page asks for this on load, so it is one
        // counted query rather than the whole report list.
        app.MapGet("/api/history/badges", (IConfigStore configStore, IPathExpander pathExpander, IRunHistoryStore historyStore, IRunArchive archive) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var viewed = config.UiState.HistoryViewedThroughRunNumber;
            var retention = config.Report.EffectiveUnseenErrorDays(config.Logging.RetentionDays);

            if (archive.IsAvailable())
            {
                var (errors, warnings, unseenCount) = archive.CountUnseen(viewed, retention > 0 ? DateTime.Now.Date.AddDays(-retention) : DateTime.MinValue);
                return Results.Json(new { errorRuns = errors, warningRuns = warnings, unseenRuns = unseenCount });
            }

            var reportPath = pathExpander.Expand(config.Report.ReportPath);
            var unseen = ReportListBuilder.Build(historyStore.GetHistory(pathExpander.Expand(config.Logging.LogFilePath)), retention, DateTime.Now,
                    fileName => File.Exists(Path.Combine(reportPath, fileName)))
                .Where(e => e.RunNumber > viewed).ToList();
            return Results.Json(new { errorRuns = unseen.Count(e => e.ErrorCount > 0), warningRuns = unseen.Count(e => e.WarningCount > 0), unseenRuns = unseen.Count });
        });

        // The sidebar's error/warning badges only count runs newer than this watermark.
        app.MapGet("/api/history/viewed-through", (IConfigStore configStore) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            return Results.Json(new { runNumber = config.UiState.HistoryViewedThroughRunNumber });
        });

        // Called once the History page has actually rendered its Reports list - advances the
        // watermark above to "every run that exists right now", clearing the sidebar badges.
        app.MapPost("/api/history/mark-viewed", (IConfigStore configStore, IRunHistoryStore historyStore) =>
        {
            var runNumber = configStore.Update(AppPaths.GetConfigFilePath(), config =>
            {
                config.UiState.HistoryViewedThroughRunNumber = historyStore.GetRunCount(AppPaths.GetRunCountFilePath());
                return config.UiState.HistoryViewedThroughRunNumber;
            });
            return Results.Json(new { runNumber });
        });

        // Serves a report HTML file by name only (never a full/relative path) resolved against
        // the *current* Report.ReportPath, so a later change to that setting doesn't strand
        // already-generated links - Path.GetFileName strips any directory component an attacker
        // (or a stale link) might try to smuggle in, so this can never escape the reports folder.
        // Serves a run's text log (the Compressarr log, or a HandBrake/ffmpeg detail log) by name only, from the Logs
        // folder, for the "Full Details" links on a report viewed through the app. Only .log/.txt files, and
        // Path.GetFileName strips any folder component, so it can't reach anything else on disk. Opened with shared
        // access because the current run's log may still be being written.
        app.MapGet("/api/logs/{fileName}", (string fileName, IConfigStore configStore, IPathExpander pathExpander) =>
        {
            var safeName = Path.GetFileName(fileName);
            var extension = Path.GetExtension(safeName);
            if (!extension.Equals(".log", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound();
            }

            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var fullPath = Path.Combine(pathExpander.Expand(config.Logging.LogFilePath), safeName);
            if (!File.Exists(fullPath)) return Results.NotFound();

            return Results.Stream(new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), "text/plain; charset=utf-8");
        });

        // A report. A number is a run's id in the run archive: the report is built from its rows now (or, for a run imported from before 3.0, the
        // html it was saved with is served as it was). Anything else is the name of a saved copy in the report folder, as before. ?download=1
        // hands the same page over as a file to keep or send: it is self-contained (styles and logo inline).
        app.MapGet("/api/reports/{key}", (string key, string? download, IConfigStore configStore, IPathExpander pathExpander, IRunArchive archive, IHtmlReportGenerator generator) =>
        {
            if (long.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var runId))
            {
                var legacy = archive.LoadLegacyReportHtml(runId);
                var model = legacy is null ? archive.LoadReport(runId) : null;
                if (legacy is null && model is null) return Results.NotFound();

                var bytes = legacy ?? System.Text.Encoding.UTF8.GetBytes(generator.Generate(model!));
                var name = $"Compressarr_Report_{runId}.html";
                return download is "1" or "true"
                    ? Results.File(bytes, "text/html; charset=utf-8", model is null ? name : $"Compressarr_{model.GeneratedAt:yyyy-MM-dd_HH-mm-ss}_Report.html")
                    : Results.File(bytes, "text/html; charset=utf-8");
            }

            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var reportPath = pathExpander.Expand(config.Report.ReportPath);
            var safeName = Path.GetFileName(key);
            var fullPath = Path.Combine(reportPath, safeName);

            return File.Exists(fullPath) ? Results.File(fullPath, "text/html") : Results.NotFound();
        });
    }

    // [[name, count], ...] biggest first, or null when the run has no such breakdown (an older row).
    private static object? Pairs(IReadOnlyDictionary<string, int>? counts) =>
        counts?.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new object[] { kv.Key, kv.Value }).ToList();
}
