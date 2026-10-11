using Microsoft.AspNetCore.Http;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Diagnostics;
using Compressarr.Core.Logging;
using Compressarr.Core.Orchestration;
using Compressarr.Core.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

public static class MaintenanceEndpoints
{
    /// <summary>The run-history file(s) to remove when history is cleared: the live one in the app data folder, plus a
    /// leftover 2.1.x copy still in the Logs folder (if it stayed, the next start would copy it back).</summary>
    private static IEnumerable<string> HistoryFiles(string logPath)
    {
        yield return AppPaths.GetHistoryFilePath();
        if (!string.IsNullOrWhiteSpace(logPath)) yield return Path.Combine(logPath, "Compressarr_History.csv");
    }

    public static void MapMaintenanceEndpoints(this IEndpointRouteBuilder app)
    {
        // Every handler here just writes a file (or deletes old ones already past retention) -
        // nothing is cached in memory anywhere that would need an app restart to pick it up.
        // /api/settings, /api/run/status, etc. all re-read their file from disk on every request,
        // so the effect is visible immediately on the very next poll/load.

        app.MapPost("/api/maintenance/reset-resume", (IResumeStateStore resumeStore) =>
        {
            resumeStore.Update(AppPaths.GetResumeFilePath(), state =>
            {
                state.Clear();
                return true;
            });
            return Results.Ok();
        });

        app.MapPost("/api/maintenance/clear-config", (IConfigStore configStore) =>
        {
            configStore.Save(DefaultConfigFactory.Create(), AppPaths.GetConfigFilePath());
            return Results.Ok();
        });

        // Narrower than Clear Configuration below - wipes only the lane list, leaving every other
        // setting (HandBrake path, Sonarr/Radarr, processing options, etc.) untouched. Same
        // DisplayName convention POST /api/lanes already uses for a brand-new lane.
        app.MapPost("/api/maintenance/reset-lanes", (IConfigStore configStore) =>
        {
            configStore.Update(AppPaths.GetConfigFilePath(), config =>
            {
                config.Lanes.Clear();
                config.Lanes.Add(LaneConfig.CreateNew("New Lane 1"));
                return true;
            });
            return Results.Ok();
        });

        // Forces the same retention cleanup RunOrchestrator already does at the end of every real
        // pass (RetentionCleaner.CleanUp against Logging.RetentionDays) right now, instead of
        // waiting for the next one - files go to the Recycle Bin (DeleteAfterConvertMode.Recycle),
        // same as that automatic pass, not a permanent delete.
        app.MapPost("/api/maintenance/cleanup-now", (IConfigStore configStore, IPathExpander pathExpander, ITrashService trash) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var logPath = pathExpander.Expand(config.Logging.LogFilePath);
            var reportPath = pathExpander.Expand(config.Report.ReportPath);

            RetentionCleaner.CleanUp(trash, logPath, new[] { ".log", ".txt" }, config.Logging.RetentionDays, "log");
            RetentionCleaner.CleanUp(trash, reportPath, new[] { ".html" }, config.Logging.RetentionDays, "report");

            return Results.Ok();
        });

        // Unconditional versions of the two pieces Clean Up Now only removes once they're past the
        // retention setting - for a "start completely fresh" reset regardless of age. Recycle Bin,
        // not a permanent delete, same convention as everywhere else in this file.
        app.MapPost("/api/maintenance/clear-logs", (IConfigStore configStore, IPathExpander pathExpander, ITrashService trash) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var logPath = pathExpander.Expand(config.Logging.LogFilePath);
            if (!Directory.Exists(logPath)) return Results.Ok();

            var extensions = new HashSet<string>(new[] { ".log", ".txt" }, StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(logPath).Where(f => extensions.Contains(Path.GetExtension(f))))
            {
                trash.DeleteFile(file, DeleteAfterConvertMode.Recycle);
            }

            return Results.Ok();
        });

        // Everything the History page shows: every HTML report, plus the run-history CSV rollup
        // (kept in the app data folder - see AppPaths.GetHistoryFilePath - so clearing Logs never touches it).
        // Deliberately leaves the run counter alone - a separate lifetime stat, not "history."
        app.MapPost("/api/maintenance/clear-history", (IConfigStore configStore, IPathExpander pathExpander, ITrashService trash) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var reportPath = pathExpander.Expand(config.Report.ReportPath);
            if (Directory.Exists(reportPath))
            {
                foreach (var file in Directory.EnumerateFiles(reportPath, "*.html"))
                {
                    trash.DeleteFile(file, DeleteAfterConvertMode.Recycle);
                }
            }

            var logPath = pathExpander.Expand(config.Logging.LogFilePath);
            foreach (var historyFile in HistoryFiles(logPath).Where(File.Exists))
            {
                trash.DeleteFile(historyFile, DeleteAfterConvertMode.Recycle);
            }

            return Results.Ok();
        });

        // Same footprint as Clear Logs + Clear History combined (every log, every HTML report,
        // and the run-history CSV), but a real permanent delete instead of Recycle Bin - for a
        // library with years of accumulated logs/reports where recycling thousands of files one
        // at a time (see ITrashService.DeleteFile - there's no bulk Recycle Bin API, only a
        // per-file one) is itself slow enough to matter. Deliberately doesn't go through
        // ITrashService at all, unlike every other maintenance action.
        app.MapPost("/api/maintenance/purge-logs-reports", (IConfigStore configStore, IPathExpander pathExpander, IRunLogger logger) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());

            var logPath = pathExpander.Expand(config.Logging.LogFilePath);
            if (Directory.Exists(logPath))
            {
                var logExtensions = new HashSet<string>(new[] { ".log", ".txt", ".csv" }, StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(logPath).Where(f => logExtensions.Contains(Path.GetExtension(f))))
                {
                    try { File.Delete(file); }
                    catch (Exception ex) { logger.Log($"Purge Logs & Reports: unable to remove '{file}': {ex.Message}", LogSeverity.Error); }
                }
            }

            var reportPath = pathExpander.Expand(config.Report.ReportPath);
            if (Directory.Exists(reportPath))
            {
                foreach (var file in Directory.EnumerateFiles(reportPath, "*.html"))
                {
                    try { File.Delete(file); }
                    catch (Exception ex) { logger.Log($"Purge Logs & Reports: unable to remove '{file}': {ex.Message}", LogSeverity.Error); }
                }
            }

            foreach (var historyFile in HistoryFiles(logPath).Where(File.Exists))
            {
                try { File.Delete(historyFile); }
                catch (Exception ex) { logger.Log($"Purge Logs & Reports: unable to remove '{historyFile}': {ex.Message}", LogSeverity.Error); }
            }

            return Results.Ok();
        });

        // Everything except settings and lanes (both live in compressarr.settings.json, untouched
        // here): every file in Reports and Logs (any extension - not just clear-logs'/clear-history's
        // own narrower .log/.txt/.html/.csv scopes) and tracked resume state - a full reset for
        // starting genuinely fresh testing. Deliberately leaves the run counter alone - it's a
        // lifetime stat of the install, not test/report data, and there's no way to reconstruct it
        // once cleared (unlike reports/logs, which just regenerate from the next real pass).
        app.MapPost("/api/maintenance/clear-all", (IConfigStore configStore, IPathExpander pathExpander, ITrashService trash, IResumeStateStore resumeStore) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());

            var reportPath = pathExpander.Expand(config.Report.ReportPath);
            if (Directory.Exists(reportPath))
            {
                foreach (var file in Directory.EnumerateFiles(reportPath))
                {
                    trash.DeleteFile(file, DeleteAfterConvertMode.Recycle);
                }
            }

            var logPath = pathExpander.Expand(config.Logging.LogFilePath);
            if (Directory.Exists(logPath))
            {
                foreach (var file in Directory.EnumerateFiles(logPath))
                {
                    trash.DeleteFile(file, DeleteAfterConvertMode.Recycle);
                }
            }

            // The run history is no longer one of the files in the Logs folder, but "everything except settings
            // and lanes" still means it.
            foreach (var historyFile in HistoryFiles(logPath).Where(File.Exists).Where(f => !f.StartsWith(logPath, StringComparison.OrdinalIgnoreCase)))
            {
                trash.DeleteFile(historyFile, DeleteAfterConvertMode.Recycle);
            }

            resumeStore.Update(AppPaths.GetResumeFilePath(), state =>
            {
                state.Clear();
                return true;
            });

            return Results.Ok();
        });
    }
}
