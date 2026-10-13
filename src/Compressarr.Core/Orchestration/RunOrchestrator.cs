using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Logging;
using Compressarr.Core.Notifications;
using Compressarr.Core.Presets;
using Compressarr.Core.Queue;
using Compressarr.Core.Scheduling;
using Compressarr.Core.Reporting;
using Compressarr.Core.Routing;

namespace Compressarr.Core.Orchestration;

public sealed class RunResult
{
    public required ReportModel Report { get; init; }
    public required string ReportFilePath { get; init; }
    public required int TotalFiles { get; init; }

    /// <summary>Count of MoveFailed/CompanionMoveFailed/CleanupPending entries this pass's own
    /// per-lane retry loops resolved (see LaneProcessingContext.RetriesSucceeded) - distinct from TotalFiles,
    /// which only counts files that went through ProcessOneFileAsync this pass. A pass whose only
    /// activity was a successful retry has TotalFiles == 0 but this greater than zero, and is
    /// treated the same as a pass that processed real files for the "don't discard this pass's own
    /// record of what happened" gates in RunOnceAsync/RunOnceCoreAsync - a real gap found via code
    /// review: without this, a purely-retry pass left literally no durable trace (log pruned,
    /// report never written) that anything happened at all.</summary>
    public required int RetriesSucceeded { get; init; }

    /// <summary>True if any file in this pass failed in a way that looked like the volume being
    /// out of space. The monitoring loop stops itself when this is true, rather than retrying the
    /// same doomed encode again on the next poll interval.</summary>
    public bool DiskFull { get; init; }
}

public interface IRunOrchestrator
{
    /// <summary>One full pass: validate HandBrakeCLI/presets paths, prepare every enabled lane with
    /// configured input, then process pending files in one global, cross-lane priority order (not
    /// lane-by-lane) - a file's explicit drag-set Order always wins regardless of which lane it's
    /// in, so a not-yet-started file from any lane can be prioritized ahead of any other lane's.
    /// Purges old logs/reports by retention, records history + increments the run counter (only if
    /// files were processed), runs the optional post-exec command, builds the HTML report, and
    /// fires a notification. Ported from Invoke-CompressarrRun. Returns null if HandBrakeCLI
    /// can't be found (the whole run aborts, matching v1).
    ///
    /// stopToken is a graceful "Stop Monitoring" signal, distinct from Abort's hard-kill token -
    /// checked before every file (across every lane, not just between lanes), so the file actively
    /// encoding right now always finishes before this pass unwinds, rather than being killed
    /// mid-encode.</summary>
    Task<RunResult?> RunOnceAsync(CompressarrConfig config, CancellationToken stopToken = default);
}

public sealed class RunOrchestrator : IRunOrchestrator
{
    // No config knob for this - a user-configured post-exec script hanging (waiting on input,
    // launching a GUI, a deadlocked .bat/.ps1) previously blocked that whole pass's report/
    // notification indefinitely with nothing (not even Abort) able to interrupt it. 5 minutes is
    // generous for what this is meant for (a quick notify-another-tool call) while still bounding
    // the worst case.
    private static readonly TimeSpan DefaultPostExecTimeout = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _postExecTimeout;

    private readonly IPathExpander _pathExpander;
    private readonly IEncoderPresetService _presets;
    private readonly IConversionOrchestrator _conversionOrchestrator;
    private readonly IMetadataService _metadata;
    private readonly IResumeStateStore _resumeStore;
    private readonly IRunLogger _logger;
    private readonly IRunHistoryStore _historyStore;
    private readonly IHistoryRollupCalculator _rollupCalculator;
    private readonly IHtmlReportGenerator _reportGenerator;
    private readonly IReportLauncher _reportLauncher;
    private readonly INotificationService _notifications;
    private readonly INotificationDispatcher _notificationDispatcher;
    private readonly ITrashService _trash;
    private readonly IRunProgressReporter _progress;
    private readonly IActiveRunController _activeRunController;
    private readonly IEncodeSchedule _schedule;

    /// <summary>Per-lane encoder choice (2.2); null = HandBrake only, as before.</summary>
    private readonly IEncoderResolver? _encoders;

    public RunOrchestrator(
        IPathExpander pathExpander,
        IEncoderPresetService presets,
        IConversionOrchestrator conversionOrchestrator,
        IMetadataService metadata,
        IResumeStateStore resumeStore,
        IRunLogger logger,
        IRunHistoryStore historyStore,
        IHistoryRollupCalculator rollupCalculator,
        IHtmlReportGenerator reportGenerator,
        IReportLauncher reportLauncher,
        INotificationService notifications,
        INotificationDispatcher notificationDispatcher,
        ITrashService trash,
        IRunProgressReporter progress,
        IActiveRunController activeRunController,
        IEncodeSchedule schedule,
        IEncoderResolver? encoders = null)
        : this(pathExpander, presets, conversionOrchestrator, metadata, resumeStore, logger, historyStore,
              rollupCalculator, reportGenerator, reportLauncher, notifications, notificationDispatcher, trash,
              progress, activeRunController, schedule, DefaultPostExecTimeout, encoders)
    {
    }

    /// <summary>Test-only seam for PostExecTimeout - real production wiring always resolves the
    /// public constructor above (only one PUBLIC constructor exists, so DI can't get confused
    /// between the two), which fixes it at DefaultPostExecTimeout. A real end-to-end test of the
    /// timeout/kill path can't wait out a real 5 minutes, so this lets it substitute a
    /// millisecond-scale value instead - same "public ctor with a real default, internal ctor with
    /// an override" shape RunLoopController's own TimeProvider seam already uses.</summary>
    internal RunOrchestrator(
        IPathExpander pathExpander,
        IEncoderPresetService presets,
        IConversionOrchestrator conversionOrchestrator,
        IMetadataService metadata,
        IResumeStateStore resumeStore,
        IRunLogger logger,
        IRunHistoryStore historyStore,
        IHistoryRollupCalculator rollupCalculator,
        IHtmlReportGenerator reportGenerator,
        IReportLauncher reportLauncher,
        INotificationService notifications,
        INotificationDispatcher notificationDispatcher,
        ITrashService trash,
        IRunProgressReporter progress,
        IActiveRunController activeRunController,
        IEncodeSchedule schedule,
        TimeSpan postExecTimeout,
        IEncoderResolver? encoders = null)
    {
        _encoders = encoders;
        _postExecTimeout = postExecTimeout;
        _pathExpander = pathExpander;
        _presets = presets;
        _conversionOrchestrator = conversionOrchestrator;
        _metadata = metadata;
        _resumeStore = resumeStore;
        _logger = logger;
        _historyStore = historyStore;
        _rollupCalculator = rollupCalculator;
        _reportGenerator = reportGenerator;
        _reportLauncher = reportLauncher;
        _notifications = notifications;
        _notificationDispatcher = notificationDispatcher;
        _trash = trash;
        _progress = progress;
        _activeRunController = activeRunController;
        _schedule = schedule;
    }

    public async Task<RunResult?> RunOnceAsync(CompressarrConfig config, CancellationToken stopToken = default)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var beginTime = DateTime.Now;

        var logFilePath = _pathExpander.Expand(config.Logging.LogFilePath);
        var reportPath = _pathExpander.Expand(config.Report.ReportPath);
        var summaryLogFilePath = _logger.Initialize(logFilePath, timestamp);

        _logger.Log($"Compressarr - run started {timestamp}");
        _logger.Log(new string('-', 80));
        _progress.RunStarted(timestamp);

        var token = _activeRunController.Begin();
        RunResult? result;
        try
        {
            result = await RunOnceCoreAsync(config, timestamp, beginTime, logFilePath, reportPath, summaryLogFilePath, token, stopToken);
        }
        finally
        {
            _activeRunController.End();
        }

        // A pass that found nothing new and hit no problem worth flagging shouldn't leave a
        // near-empty file behind forever - confirmed live: with a normal polling interval this is
        // by far the most common outcome of a pass, and it was generating thousands of
        // essentially-content-free log files (one per idle poll) that made the genuinely useful
        // ones hard to find. A pass that processed real files, resolved a stranded retry (see
        // RunResult.RetriesSucceeded), or hit a real error even while processing none (a
        // misconfigured lane, a missing HandBrakeCLI, etc), still keeps its log.
        if ((result?.TotalFiles ?? 0) == 0 && (result?.RetriesSucceeded ?? 0) == 0 && !_logger.HasLoggedError)
        {
            try { if (File.Exists(summaryLogFilePath)) File.Delete(summaryLogFilePath); }
            catch (Exception ex) { _logger.Log($"Unable to remove empty run log '{summaryLogFilePath}': {ex.Message}", LogSeverity.Error); }
        }

        return result;
    }

    private async Task<RunResult?> RunOnceCoreAsync(CompressarrConfig config, string timestamp, DateTime beginTime, string logFilePath, string reportPath, string summaryLogFilePath, CancellationToken token, CancellationToken stopToken)
    {
        var hbloc = _pathExpander.Expand(config.HandBrake.CliPath);

        // A setup with an ffmpeg lane checks each lane's own encoder below instead (a missing
        // HandBrake must not stop the ffmpeg lanes, nor the other way round). A HandBrake-only setup
        // keeps the original rule: no HandBrakeCLI, no run.
        var hasFfmpegLane = _encoders is not null && config.Lanes.Any(l => l.Enabled && l.Engine == EncoderEngine.FFmpeg);
        // ... and a setup whose enabled lanes only use Passthrough never starts an encoder, so it needs no HandBrakeCLI.
        var onlyPassthroughLanes = config.Lanes.Any(l => l.Enabled)
            && config.Lanes.Where(l => l.Enabled).All(l => PassthroughPreset.OnlyPassthrough(l.TvPreset, l.MoviePreset));
        if (!hasFfmpegLane && !onlyPassthroughLanes && !_pathExpander.PathExists(config.HandBrake.CliPath))
        {
            _logger.LogProblem("handbrake-cli-missing", $"HandBrakeCLI.exe not found at {hbloc}. Download it from https://handbrake.fr/downloads2.php");
            _progress.RunCompleted(0);
            return null;
        }
        _logger.ClearProblem("handbrake-cli-missing");

        var resumeFilePath = AppPaths.GetResumeFilePath();
        var resumeState = _resumeStore.Load(resumeFilePath);

        // Must run before the tracked-count log line below - an entry under a since-deleted lane
        // is invisible to every one of PrepareLaneAsync's own per-lane cleanup loops (a lane no
        // longer in config.Lanes is never iterated), so left unpruned it inflates this count and
        // permanently blocks the end-of-pass stillOutstanding wipe, even once every real lane is
        // fully caught up.
        _conversionOrchestrator.PruneOrphanedLaneEntries(config, resumeState, resumeFilePath);

        if (resumeState.Count > 0)
        {
            _logger.Log($"Resuming previous incomplete run ({resumeState.Count} file(s) tracked).");
        }

        // One-time migration, cheap no-op on every call after the first - see its own doc comment.
        // Run here, before ANY lane is prepared, so it captures the queue's own effective order
        // exactly as it looked walking into this pass, not after Phase 1 below could have already
        // added fresh Pending entries of its own.
        _conversionOrchestrator.BackfillMissingOrder(config, resumeState, resumeFilePath);

        _metadata.Enabled = config.Processing.ClearTitleMetadata;

        var laneResults = new Dictionary<string, List<ConversionResult>>();
        // Every configured lane gets an entry (even one that ends up empty), populated as
        // problems are found below - merged into the report as LaneReportSection.LaneProblems so
        // a lane/run-level issue (a missing preset, a missing Output folder, FileBot's path not
        // found) is visible on the report itself, not just log-only. See ReportErrorCode 106+.
        var laneProblems = new Dictionary<string, List<ReportErrorCode>>();
        // Accumulated across every lane's own PrepareLaneAsync call (see
        // LaneProcessingContext.RetriesSucceeded) - declared outside the try block below so a
        // mid-pass Abort/Stop (caught further down) doesn't discard whatever retries this pass
        // already resolved before it was interrupted.
        var totalRetriesSucceeded = 0;
        try
        {
            // Phase 1: per-lane prep, unchanged validation/logging - every enabled, valid lane gets
            // scanned and seeded up front, before any file from any lane starts converting. Lanes
            // that don't pass validation are simply never added to laneContexts/laneOrderIndex, so
            // Phase 2's cross-lane picking query below naturally never considers their entries.
            var laneContexts = new Dictionary<string, LaneProcessingContext>();
            var laneOrderIndex = new Dictionary<string, int>();
            var configLaneIndex = 0;
            foreach (var lane in config.Lanes)
            {
                token.ThrowIfCancellationRequested();
                stopToken.ThrowIfCancellationRequested();

                if (!lane.Enabled)
                {
                    _logger.Log($"Skipping lane [{lane.DisplayName}] - lane is disabled.");
                    configLaneIndex++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(lane.Input)) { configLaneIndex++; continue; }

                var thisLaneProblems = new List<ReportErrorCode>();
                laneProblems[lane.Id] = thisLaneProblems;

                if (string.IsNullOrWhiteSpace(lane.TvPreset) && string.IsNullOrWhiteSpace(lane.MoviePreset))
                {
                    _logger.LogProblem($"lane-no-preset:{lane.Id}", $"Skipping lane [{lane.DisplayName}] - no TV or Movie preset configured.");
                    thisLaneProblems.Add(ReportErrorCode.LaneNoPresetConfigured);
                    configLaneIndex++;
                    continue;
                }
                _logger.ClearProblem($"lane-no-preset:{lane.Id}");

                // A lane whose presets are all Passthrough never starts an encoder, so a missing HandBrake / ffmpeg is not its problem.
                if (_encoders is not null && !PassthroughPreset.OnlyPassthrough(lane.TvPreset, lane.MoviePreset)
                    && _encoders.NotReadyReason(lane.Engine, config) is { } engineProblem)
                {
                    _logger.LogProblem($"lane-encoder:{lane.Id}", $"Skipping lane [{lane.DisplayName}] - {engineProblem}");
                    thisLaneProblems.Add(ReportErrorCode.LaneEncoderNotAvailable);
                    configLaneIndex++;
                    continue;
                }
                _logger.ClearProblem($"lane-encoder:{lane.Id}");

                // LaneValidator is the single source of truth for "does this lane's own configured
                // TV/Movie preset actually exist in Compressarr's profiles" - shared with the Lanes page's
                // own validation (LaneEndpoints.cs), so the two can never disagree. At this point
                // at least one of TvPreset/MoviePreset is non-empty (the no-preset-at-all case
                // above already continued), so a "tvPreset"/"moviePreset" issue coming back here
                // can only mean "set, but not found in Compressarr's profiles" - the log/report wording
                // below is kept exactly as it was before this was factored out, only the
                // PresetExists condition itself moved.
                var laneIssues = LaneValidator.Validate(lane, config, _pathExpander, _encoders?.PresetsFor(lane.Engine) ?? _presets);

                if (laneIssues.Any(i => i.Field == "tvPreset"))
                {
                    _logger.LogProblem($"lane-tv-preset-missing:{lane.Id}", $"Lane [{lane.DisplayName}] - TV preset '{lane.TvPreset}' not found in Compressarr's profiles. TV episodes in this lane will be skipped.");
                    thisLaneProblems.Add(ReportErrorCode.LaneTvPresetNotFound);
                }
                else
                {
                    _logger.ClearProblem($"lane-tv-preset-missing:{lane.Id}");
                }
                if (laneIssues.Any(i => i.Field == "moviePreset"))
                {
                    _logger.LogProblem($"lane-movie-preset-missing:{lane.Id}", $"Lane [{lane.DisplayName}] - Movie preset '{lane.MoviePreset}' not found in Compressarr's profiles. Movies in this lane will be skipped.");
                    thisLaneProblems.Add(ReportErrorCode.LaneMoviePresetNotFound);
                }
                else
                {
                    _logger.ClearProblem($"lane-movie-preset-missing:{lane.Id}");
                }

                _logger.Log($"\nScanning lane [{lane.DisplayName}] - {_pathExpander.Expand(lane.Input)}");
                // Captured from resumeState before PrepareLane touches it - a lane starting clean
                // writes its own fresh Pending entries for bookkeeping before converting anything,
                // which would otherwise make it look "resumed" a moment later even though nothing
                // was ever interrupted. Also excludes CreatedByQueueEdit entries - matching
                // RunEndpoints.ComputeUpNext's own equivalent fallback check - since an entry that
                // only exists because a skip/preset-override/remove/reorder action touched a
                // never-before-tracked file isn't genuinely resumed work either; without this, one
                // such entry sitting Pending when this lane's next real pass begins mislabels every
                // other file in the lane "Resumed" too, even though nothing was ever interrupted.
                var laneIsResumed = resumeState.Any(e => e.LaneId == lane.Id && e.Status == ResumeStatus.Pending && !e.CreatedByQueueEdit);
                _progress.LaneStarted(lane.Id, lane.DisplayName, laneIsResumed);

                var context = await _conversionOrchestrator.PrepareLaneAsync(lane, config, resumeState, resumeFilePath, thisLaneProblems, token);
                configLaneIndex++;
                if (context is null) continue;

                laneContexts[lane.Id] = context;
                laneOrderIndex[lane.Id] = configLaneIndex - 1;
                laneResults[lane.Id] = new List<ConversionResult>();
                totalRetriesSucceeded += context.RetriesSucceeded;
            }

            // Phase 2: one global loop across every prepared lane, picking whichever eligible entry
            // is highest priority regardless of which lane it belongs to. The picking rule itself
            // (QueueRules.SelectNext) is the very same one the Monitor page's queue display
            // (QueueRules.BuildUpNext) sorts by, so the two can never disagree about "what's next."
            while (true)
            {
                token.ThrowIfCancellationRequested();
                // Graceful Stop Monitoring - checked before every file, across every lane, so a
                // stop requested while other lanes still have queued files doesn't go on to start
                // an entirely different file's worth of work before honoring it.
                stopToken.ThrowIfCancellationRequested();

                _conversionOrchestrator.RefreshResumeState(resumeState, resumeFilePath);

                var next = QueueRules.SelectNext(
                    resumeState,
                    laneOrderIndex,
                    (laneId, fullName) => laneContexts[laneId].NaturalOrderIndex.TryGetValue(fullName, out var idx) ? idx : int.MaxValue);

                if (next is null) break;

                // Optional off-hours-only schedule: during the daytime window no NEW file starts
                // (one already encoding is handled separately - finished or suspended - by
                // IEncodeSchedule). Lane prep above still ran, so new files keep being tracked in
                // arrival order while they wait. Checked fresh before every file, so a window that
                // begins mid-pass holds the very next file, and "run anyway" releases it at once.
                var scheduleStatus = _schedule.GetStatus();
                if (scheduleStatus.IsHeld)
                {
                    if (_schedule.TryMarkHoldNoticed(scheduleStatus))
                    {
                        var resumeAt = scheduleStatus.NextChange?.ToString("t") ?? "off-hours";
                        _logger.Log($"\nOff-hours only is on - encoding is held for the daytime window and resumes at {resumeAt}. Use \"Run anyway\" on the Monitor page to start now.");
                    }
                    break;
                }

                var context = laneContexts[next.LaneId];
                var result = await _conversionOrchestrator.ProcessOneFileAsync(context, next, logFilePath, timestamp, resumeState, resumeFilePath, token);
                laneResults[next.LaneId].Add(result);
            }
        }
        catch (OperationCanceledException)
        {
            // token (Abort) kills the in-flight encode; stopToken (Stop Monitoring) only ever
            // stops this pass between files/lanes, letting whatever was already encoding finish -
            // worth distinguishing here since "aborted"/Error severity would be actively
            // misleading for the latter, much more common, entirely-expected case.
            if (token.IsCancellationRequested)
            {
                _logger.Log("\nRun aborted by user.", LogSeverity.Error);
            }
            else
            {
                _logger.Log("\nMonitoring stopped - remaining queued files will run on the next pass.");
            }
        }

        var stillOutstanding = resumeState.Any(e => e.Status != ResumeStatus.Completed);
        if (!stillOutstanding && File.Exists(resumeFilePath))
        {
            // Nothing left to do. A converted file whose source was kept (Maintain) is still remembered, or it would be converted
            // again on the next pass; everything else is forgotten as before.
            var kept = resumeState.Where(ResumeKeeping.StillNeeded).ToList();
            if (kept.Count == 0) File.Delete(resumeFilePath); else _resumeStore.Save(kept, resumeFilePath);
        }

        RetentionCleaner.CleanUp(_trash, logFilePath, new[] { ".log", ".txt" }, config.Logging.RetentionDays, "log");
        RetentionCleaner.CleanUp(_trash, reportPath, new[] { ".html" }, config.Logging.RetentionDays, "report");

        var endTime = DateTime.Now;
        var runTime = endTime - beginTime;
        if (runTime < TimeSpan.Zero) runTime = runTime.Negate();

        var allResults = laneResults.Values.SelectMany(r => r).ToList();
        var totalFiles = allResults.Count;

        var runCountPath = AppPaths.GetRunCountFilePath();
        var reportFileName = $"Compressarr_{timestamp}_Report.html";
        // 0 means "this pass found nothing to do" - the report shows a plain "Run:" label
        // instead of "Run #N:" for one of those, matching v1.1.
        var runNumber = 0;
        if (totalFiles > 0)
        {
            // History totals count only files that actually produced an encode (see CountsTowardTotals); a run
            // in which every file failed is still recorded (it has a report and a run number) with 0 files.
            var counted = allResults.Where(r => r.CountsTowardTotals).ToList();
            // The file count includes a passed-through file; the bytes do not (nothing was saved - see ConversionResult.IsPassthrough).
            var totalBeg = counted.Where(r => !r.IsPassthrough).Sum(r => r.BeginSizeGb);
            var totalEnd = counted.Where(r => !r.IsPassthrough).Sum(r => r.EndSizeGb);
            // Mirrors ReportModel.ErrorCount/WarningCount - computed here too since the history
            // record is written before reportModel exists below.
            var errorCount = allResults.Count(r => !r.Success);
            var warningCount = allResults.Count(r => r.Success && !string.IsNullOrEmpty(r.PostProcessWarning));
            // The run's own permanent number is "how many runs came before it, plus one" - read
            // before IncrementRunCount below so the record and the counter agree on the same value.
            runNumber = _historyStore.GetRunCount(runCountPath) + 1;
            _historyStore.AppendRun(logFilePath, new RunHistoryRecord(
                endTime.Year, endTime.Month, endTime.Day, totalBeg, totalEnd, counted.Count,
                runTime.Hours, runTime.Minutes, runTime.Seconds,
                RunNumber: runNumber, ReportFileName: reportFileName,
                ErrorCount: errorCount, WarningCount: warningCount,
                RedirectCount: allResults.Count(r => r.RedirectedToLaneId is not null)));

            // Only a pass that actually processed files counts as a "run" - an empty scan
            // (including every quiet monitor-mode poll) never moves this counter.
            _historyStore.IncrementRunCount(runCountPath);
        }

        var postExecCmd = _pathExpander.Expand(config.PostExec.Cmd);
        if (!string.IsNullOrWhiteSpace(postExecCmd) && File.Exists(postExecCmd))
        {
            _logger.Log($"\nRunning post-execution command: {postExecCmd} {config.PostExec.Args}");
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(postExecCmd, config.PostExec.Args) { UseShellExecute = false, CreateNoWindow = true });
            if (process is not null)
            {
                // token here is Abort's hard-kill token (see RunOnceAsync/_activeRunController.Begin
                // above), so Abort can now actually interrupt a hung post-exec script too, not just
                // the encode itself - previously nothing reached this process at all.
                using var timeoutCts = new CancellationTokenSource(_postExecTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
                try
                {
                    await process.WaitForExitAsync(linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    var reason = token.IsCancellationRequested
                        ? "the run was aborted"
                        : $"it didn't finish within {_postExecTimeout.TotalMinutes:0.##} minutes";
                    _logger.Log($"Post-execution command was killed - {reason}.", LogSeverity.Error);
                    try { process.Kill(entireProcessTree: true); }
                    catch (Exception ex) { _logger.Log($"Failed to kill post-execution command: {ex.Message}", LogSeverity.Error); }
                }
            }
        }

        _logger.Log(totalFiles == 0
            ? "\nCompressarr run completed. No new files to process."
            : $"\nCompressarr run completed. {totalFiles} file(s) processed in {runTime.Hours}h {runTime.Minutes}m {runTime.Seconds}s.");

        var (today, thisMonth, thisYear) = _rollupCalculator.Calculate(logFilePath);

        var reportModel = new ReportModel
        {
            GeneratedAt = endTime,
            RunTime = runTime,
            RunNumber = runNumber,
            // Every configured lane gets a section - matching v1.1, a lane that wasn't touched
            // this pass (disabled, or enabled but nothing to do) still shows up with a "No files
            // processed." placeholder rather than being silently absent from the report.
            Lanes = config.Lanes.Select(lane => new LaneReportSection
            {
                LaneDisplayName = lane.DisplayName,
                Results = laneResults.TryGetValue(lane.Id, out var results) ? results : Array.Empty<ConversionResult>(),
                LaneProblems = laneProblems.TryGetValue(lane.Id, out var problems) ? problems : Array.Empty<ReportErrorCode>()
            }).ToList(),
            Today = today,
            ThisMonth = thisMonth,
            ThisYear = thisYear,
            SummaryLogFilePath = summaryLogFilePath,
            RetriesSucceeded = totalRetriesSucceeded
        };

        var reportFilePath = Path.Combine(reportPath, reportFileName);

        // Called unconditionally, every pass, for every configured lane (not just ones with a
        // problem this time) - the memory has to track "went from broken to clear" and "clear to
        // broken" too, not just "still broken with the same code set", the same way LogProblem's
        // own per-key memory is always updated regardless of what else happens this pass.
        var anyLaneProblemsChanged = false;
        foreach (var lane in config.Lanes)
        {
            var problems = laneProblems.TryGetValue(lane.Id, out var found) ? found : new List<ReportErrorCode>();
            if (_logger.HasLaneProblemsChanged(lane.Id, problems.Select(p => p.ToString()).ToList()))
            {
                anyLaneProblemsChanged = true;
            }
        }

        // Broader than the history record/run-counter gate above (deliberately): a pass that
        // processed nothing AND hit no lane/run-level problem gets no report on disk at all, same
        // "don't leave empty artifacts behind" reasoning as the log-file cleanup. But a pass that
        // processed nothing BECAUSE a lane is misconfigured (no preset, no Output folder, etc)
        // still gets a report, so that's visible on the report itself and not just log-only - the
        // whole point of ReportErrorCode's 106+ values. reportModel/reportFilePath still get
        // built either way since RunResult always needs them (and building the model itself is
        // free - no I/O), but nothing writes them out for a genuinely empty, problem-free pass -
        // or, same reasoning extended, for a pass whose only "activity" is the exact same lane
        // problem(s) as the pass that already wrote the last report (real gap found live: this
        // gate had no memory of its own, so a persistently misconfigured lane could force a fresh
        // report on literally every single poll forever, even though the matching log line was
        // already correctly downgraded to Info by LogProblem). A pass whose only activity was a
        // successful MoveFailed/CompanionMoveFailed/CleanupPending retry also gets a report - see
        // RunResult.RetriesSucceeded - so that activity has a durable, visible record too, not
        // just a log line.
        if (totalFiles > 0 || (reportModel.HasAnyLaneProblems && anyLaneProblemsChanged) || totalRetriesSucceeded > 0)
        {
            Directory.CreateDirectory(reportPath);
            var html = _reportGenerator.Generate(reportModel);
            File.WriteAllText(reportFilePath, html);

            var shouldOpen = config.Report.OpenAfterRun switch
            {
                OpenReportMode.Always => true,
                OpenReportMode.OnError => reportModel.ErrorCount > 0 || reportModel.HasAnyLaneProblems,
                _ => false
            };
            if (shouldOpen)
            {
                _reportLauncher.Open(reportFilePath);
            }
        }

        // Fires independent of OpenAfterRun (which can be Never/OnError-with-no-errors, in which
        // case the report never opens on its own and this is the only completion signal the user
        // gets), but skipped entirely for an empty pass so idle polling never spams a notification.
        if (totalFiles > 0)
        {
            if (config.Notifications.ToastEnabled)
            {
                var toastBeg = allResults.Where(r => !r.IsPassthrough).Sum(r => r.BeginSizeGb);
                var toastEnd = allResults.Where(r => !r.IsPassthrough).Sum(r => r.EndSizeGb);

                _notifications.NotifyRunComplete(
                    new RunCompletionSummary(totalFiles, toastBeg, toastEnd, runTime),
                    reportFilePath);
            }

            // Separate from the toast above - toast is a local-OS-only channel, this is the
            // pluggable list of external destinations (webhook, and later Discord/Slack/etc)
            // configured on the Notifications page. Outcome/counts come straight off reportModel,
            // already built above, rather than recomputing anything.
            var outcome = reportModel.ErrorCount > 0 ? NotificationOutcome.Error
                : reportModel.WarningCount > 0 ? NotificationOutcome.Warning
                : NotificationOutcome.Success;
            var isCustomStyle = config.Notifications.MessageStyle == NotificationMessageStyle.Custom;
            var (titleTemplate, bodyTemplate) = isCustomStyle
                ? (config.Notifications.CustomTitleTemplate, config.Notifications.CustomBodyTemplate)
                : NotificationMessagePresets.Templates[config.Notifications.MessageStyle];
            // Null unless "Include machine name" is ticked and a name is set. The built-in styles get it as a
            // title prefix; a Custom template places {machine} itself, so it is not prefixed on top.
            var machineName = MachineLabel.For(config.Notifications);
            var notifyEvent = new NotificationEvent(
                outcome,
                Title: NotificationMessageRenderer.Render(titleTemplate, outcome, reportModel, reportFilePath, machineName),
                Body: NotificationMessageRenderer.Render(bodyTemplate, outcome, reportModel, reportFilePath, machineName),
                totalFiles,
                SavedGb: reportModel.TotalBeforeGb - reportModel.TotalAfterGb,
                runTime,
                reportFilePath,
                MachineName: machineName,
                PrefixMachineName: !isCustomStyle);
            await _notificationDispatcher.DispatchAsync(config.Notifications, notifyEvent);
        }

        _progress.RunCompleted(totalFiles);

        return new RunResult
        {
            Report = reportModel,
            ReportFilePath = reportFilePath,
            TotalFiles = totalFiles,
            RetriesSucceeded = totalRetriesSucceeded,
            DiskFull = allResults.Any(r => r.DiskFull)
        };
    }
}
