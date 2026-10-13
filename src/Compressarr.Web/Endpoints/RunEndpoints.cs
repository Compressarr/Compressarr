using Microsoft.AspNetCore.Http;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Diagnostics;
using Compressarr.Core.Orchestration;
using Compressarr.Core.Posters;
using Compressarr.Core.Queue;
using Compressarr.Core.Scheduling;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

// FullName (the file's real absolute path) is what every queue-mutation request identifies a row
// by - the bare leaf FileName is NOT unique (lanes support recursive Input folders, so two files can
// share a name in different subfolders). All of the queue's rules live in Compressarr.Core.Queue
// (QueueRules / QueueService); these endpoints only translate HTTP to and from them.
public sealed record RemoveErrorQueueEntryRequest(string LaneId, string FullName);
public sealed record RemoveQueueEntryRequest(string LaneId, string FullName);
public sealed record SkipQueueEntryRequest(string LaneId, string FullName, bool Skipped);
public sealed record ReorderQueueItem(string LaneId, string FullName);
public sealed record ReorderQueueRequest(List<ReorderQueueItem> Items);
public sealed record PresetOverrideRequest(string LaneId, string FullName, string? Preset);
public sealed record DestinationRequest(string LaneId, string FullName, string? DestinationLaneId);

public static class RunEndpoints
{
    private static IResult ToResult(QueueEditResult result) =>
        result == QueueEditResult.Ok ? Results.Ok() : Results.NotFound();

    public static void MapRunEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/run/once", async (IConfigStore configStore, IRunOrchestrator runOrchestrator) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var result = await runOrchestrator.RunOnceAsync(config);
            return result is null
                ? Results.BadRequest(new { message = "Run aborted - check the HandBrakeCLI and presets.json paths." })
                : Results.Json(new { totalFiles = result.TotalFiles });
        });

        app.MapPost("/api/run/start", (IConfigStore configStore, IRunLoopController loopController) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            loopController.Start(config, TimeSpan.FromSeconds(Math.Max(5, config.Repeat.PollIntervalSeconds)));
            return Results.Ok();
        });

        app.MapPost("/api/run/stop", async (IRunLoopController loopController) =>
        {
            await loopController.StopAsync();
            return Results.Ok();
        });

        app.MapPost("/api/run/abort", (IRunLoopController loopController) =>
        {
            loopController.Abort();
            return Results.Ok();
        });

        // The Monitor page's Run Now. With monitoring on and idle it skips the countdown; with monitoring OFF it runs
        // one pass without starting monitoring. Nothing starts if a pass is already running.
        app.MapPost("/api/run/run-now", (IRunLoopController loopController, IConfigStore configStore) =>
        {
            if (loopController.TriggerNow()) return Results.Json(new { started = true, mode = "countdown" });
            if (loopController.IsRunning) return Results.Json(new { started = false, mode = "busy" });

            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var started = loopController.RunOnceInBackground(config);
            return Results.Json(new { started, mode = started ? "single-pass" : "busy" });
        });

        app.MapPost("/api/run/trigger-now", (IRunLoopController loopController) =>
        {
            var triggered = loopController.TriggerNow();
            return Results.Json(new { triggered });
        });

        // Clears a single Error-status resume entry, surfaced on the In Queue list's Error badge -
        // the file itself is left untouched on disk, only its tracking entry is dropped so it stops
        // showing up here. A later fresh scan of the lane's Input folder can pick the file back up
        // as a brand-new Pending entry, same as any other file that was never tracked before.
        app.MapPost("/api/run/queue/remove-error", (RemoveErrorQueueEntryRequest request, IQueueService queue) =>
            Results.Json(new { removed = queue.RemoveError(request.LaneId, request.FullName) }));

        // Drag-to-reorder (and Move to top/bottom) on the Monitor page's In Queue list - sets Order
        // sequentially across the WHOLE submitted list, spanning every lane. An item whose LaneId
        // doesn't resolve to a configured lane is skipped rather than failing the whole request.
        app.MapPost("/api/run/queue/reorder", (ReorderQueueRequest request, IConfigStore configStore, IQueueService queue) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            queue.Reorder(config, request.Items.Select(i => (i.LaneId, i.FullName)).ToList());
            return Results.Ok();
        });

        // "Skip" from the queue's 3-dot menu - the entry stays visible (dimmed) but the engine
        // excludes it from what actually gets encoded. Persists until toggled back off from the same
        // menu, not a true one-shot skip (see ResumeEntry.Skipped).
        app.MapPost("/api/run/queue/skip", (SkipQueueEntryRequest request, IConfigStore configStore, IQueueService queue) =>
            ToResult(queue.Skip(configStore.Load(AppPaths.GetConfigFilePath()), request.LaneId, request.FullName, request.Skipped)));

        // "Remove from queue" for a regular (non-Error) queue item - marks the entry Removed (and
        // implicitly Skipped) rather than deleting it, so the live rescan keeps treating the file as
        // already tracked instead of rediscovering it. The file itself is left untouched on disk.
        app.MapPost("/api/run/queue/remove", (RemoveQueueEntryRequest request, IConfigStore configStore, IQueueService queue) =>
            ToResult(queue.Remove(configStore.Load(AppPaths.GetConfigFilePath()), request.LaneId, request.FullName)));

        // Per-file preset override, set by clicking the preset name on a queue row - overrides the
        // lane's TvPreset/MoviePreset for this one file only. Passing null/empty Preset clears the
        // override back to the lane default.
        app.MapPost("/api/run/queue/preset-override", (PresetOverrideRequest request, IConfigStore configStore, IQueueService queue) =>
            ToResult(queue.OverridePreset(configStore.Load(AppPaths.GetConfigFilePath()), request.LaneId, request.FullName, request.Preset)));

        // "Lands in" lane picker on a queue row - routes the finished file into another lane's library without
        // moving the source (see ResumeEntry.DestinationLaneId). A null/blank DestinationLaneId clears it.
        app.MapPost("/api/run/queue/destination", (DestinationRequest request, IConfigStore configStore, IQueueService queue) =>
        {
            var result = queue.SetDestination(configStore.Load(AppPaths.GetConfigFilePath()), request.LaneId, request.FullName, request.DestinationLaneId);
            return result == QueueEditResult.UnknownDestinationLane
                ? Results.BadRequest(new { message = "That lane no longer exists." })
                : ToResult(result);
        });

        app.MapPost("/api/run/pause", (IActiveEncodeProcess activeProcess) =>
        {
            activeProcess.Pause();
            return Results.Ok();
        });

        app.MapPost("/api/run/resume", (IActiveEncodeProcess activeProcess, IEncodeSchedule schedule) =>
        {
            // Resuming by hand while the off-hours schedule has the encode frozen would just be frozen
            // again on its next tick - so it counts as choosing to run anyway for the rest of the window.
            if (schedule.GetStatus().IsHeld) schedule.RunAnyway();
            activeProcess.Resume();
            return Results.Ok();
        });

        // "Run anyway" - releases the off-hours hold for the rest of the current daytime window (see
        // IEncodeSchedule.RunAnyway). Not saved; the hold returns at the next window change.
        app.MapPost("/api/run/run-anyway", (IEncodeSchedule schedule) =>
        {
            schedule.RunAnyway();
            return Results.Ok();
        });

        app.MapGet("/api/run/status", async (
            IRunLoopController loopController,
            CurrentRunStateService runState,
            ICpuUsageSampler cpuSampler,
            IConfigStore configStore,
            IQueueService queue,
            IEncodeSchedule schedule,
            IActiveEncodeProcess activeProcess,
            IResumeStateStore resumeStore,
            IPosterService posters) =>
        {
            var snapshot = runState.GetSnapshot();
            var cpu = await cpuSampler.SampleAsync();

            var nextRunUtc = loopController.NextRunUtc;
            var secondsUntilNextRun = nextRunUtc is null
                ? (int?)null
                : Math.Max(0, (int)Math.Ceiling((nextRunUtc.Value - DateTimeOffset.UtcNow).TotalSeconds));

            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var upNext = queue.GetUpNext(config, new QueueRunContext(snapshot.FileFullName, snapshot.LaneIsResumedById));
            var scheduleStatus = schedule.GetStatus(config);
            var queueEtaText = ComputeQueueEtaText(upNext, runState, schedule, scheduleStatus);

            // Poster pictures (Settings > Show posters on queue): each row, and the file being encoded, gets a poster id once TMDb has been
            // asked about it. Answered from memory - a title not looked up yet is queued in the background and shows up on a later poll.
            var posterSettings = config.Posters;
            var upNextShown = posterSettings.Enabled
                ? upNext.Select(i => i with { PosterKey = posters.GetPosterId(posterSettings, i.FileName) }).ToList()
                : upNext;
            var currentPosterKey = posterSettings.Enabled && snapshot.IsRunning && snapshot.FileName is not null
                ? posters.GetPosterId(posterSettings, snapshot.FileName)
                : null;

            // Where the file being encoded will end up: the lane it was assigned to land in, else its own lane.
            string? landsInLaneName = null;
            if (snapshot.IsRunning && snapshot.FileFullName is not null)
            {
                var homeLane = config.Lanes.FirstOrDefault(l => l.DisplayName == snapshot.LaneDisplayName);
                var assigned = resumeStore.Load(AppPaths.GetResumeFilePath())
                    .FirstOrDefault(e => string.Equals(e.FullName, snapshot.FileFullName, StringComparison.OrdinalIgnoreCase))?.DestinationLaneId;
                var destination = string.IsNullOrWhiteSpace(assigned) ? null : config.Lanes.FirstOrDefault(l => l.Id == assigned);
                landsInLaneName = (destination ?? homeLane)?.DisplayName;
            }

            return Results.Json(new
            {
                isMonitoring = loopController.IsRunning,
                isStopping = loopController.IsStopping,
                isRunning = snapshot.IsRunning,
                isRenaming = snapshot.IsRenaming,
                runStartedUtc = snapshot.RunStartedUtc,
                isPaused = activeProcess.IsPaused,
                laneDisplayName = snapshot.LaneDisplayName,
                engine = (config.Lanes.FirstOrDefault(l => l.DisplayName == snapshot.LaneDisplayName)?.Engine ?? EncoderEngine.HandBrake).ToString(),
                fileName = snapshot.FileName,
                presetName = snapshot.PresetName,
                isPassthrough = PassthroughPreset.Is(snapshot.PresetName),
                landsInLaneName,
                fileIndex = snapshot.FileIndex,
                fileTotal = snapshot.FileTotal,
                progressPercent = snapshot.ProgressPercent,
                progressFps = snapshot.ProgressFps,
                progressEta = snapshot.ProgressEta,
                recentLogLines = snapshot.RecentLogLines,
                cpuUsagePercent = cpu,
                secondsUntilNextRun,
                schedule = new
                {
                    enabled = scheduleStatus.Enabled,
                    isDaytime = scheduleStatus.IsDaytime,
                    priority = scheduleStatus.Priority?.ToString(),
                    onlyOffHours = scheduleStatus.OnlyOffHours,
                    isHeld = scheduleStatus.IsHeld,
                    overrideActive = scheduleStatus.OverrideActive,
                    nextChange = scheduleStatus.NextChange
                },
                upNext = upNextShown,
                queueEtaText,
                posters = new
                {
                    enabled = posterSettings.Enabled,
                    size = posterSettings.Size.ToString(),
                    smaller = posterSettings.SmallerImages,
                    currentKey = currentPosterKey
                }
            });
        });
    }

    /// <summary>Projected wall-clock completion time for everything still left to process -
    /// whatever's actively encoding right now (the queue list deliberately excludes it, since it's
    /// not "up next", it's already running - GetCurrentFileRemaining fills that gap) plus
    /// everything still in upNext (skipped/error items excluded, same as the engine itself never
    /// processes them). Summed per-item using that item's own preset's throughput rate - a queue
    /// can mix presets with very different encode speed (e.g. HD/UHD lanes interleaved via
    /// cross-lane priority), so one blended rate for the whole queue would be less accurate than
    /// resolving each item separately. Returns the literal string "Estimating" if any item's rate
    /// can't be resolved yet (a genuinely fresh install, or a preset that's never appeared in a
    /// report or a live sample); otherwise an ISO 8601 timestamp for the client to format into a
    /// local date/time. Returns null if there's genuinely nothing left to estimate (idle, empty
    /// queue). With the optional off-hours-only schedule on, time the queue is held for the daytime
    /// window is counted too (see IEncodeSchedule.ProjectCompletion).</summary>
    private static string? ComputeQueueEtaText(IReadOnlyList<UpNextItem> upNext, CurrentRunStateService runState, IEncodeSchedule schedule, ScheduleStatus scheduleStatus)
    {
        var remaining = upNext.Where(i => !i.IsSkipped && !i.IsError).ToList();
        var current = runState.GetCurrentFileRemaining();
        if (remaining.Count == 0 && current is null) return null;

        var currentMinutes = 0.0;
        var queuedMinutes = 0.0;

        if (current is { } c)
        {
            var currentRate = runState.GetRateGbPerMinute(c.PresetName);
            if (currentRate is null || currentRate <= 0) return "Estimating";
            currentMinutes = c.RemainingGb / currentRate.Value;
        }

        foreach (var item in remaining)
        {
            var rate = runState.GetRateGbPerMinute(item.Preset);
            if (rate is null || rate <= 0) return "Estimating";
            queuedMinutes += item.SizeGb / rate.Value;
        }

        if (scheduleStatus.Enabled && scheduleStatus.OnlyOffHours && schedule.ProjectCompletion(currentMinutes, queuedMinutes) is { } held)
        {
            return DateTime.SpecifyKind(held, DateTimeKind.Local).ToString("o");
        }

        return DateTime.Now.AddMinutes(currentMinutes + queuedMinutes).ToString("o");
    }
}
