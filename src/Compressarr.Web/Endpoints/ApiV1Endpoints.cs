using System.Globalization;
using Compressarr.Core.Api;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Diagnostics;
using Compressarr.Core.Logging;
using Compressarr.Core.Orchestration;
using Compressarr.Core.Queue;
using Compressarr.Core.Reporting;
using Compressarr.Core.Scheduling;
using System.Text.Json.Nodes;
using Compressarr.Web.Dtos;
using Compressarr.Web.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

/// <summary>The Automation API: <c>/api/v1</c>. Stable, versioned JSON for scripts and Home Assistant, every call behind an API key (see
/// <see cref="ApiKeyFilter"/>). Additive changes only (new fields, new endpoints); a breaking change would be <c>/api/v2</c>. The web UI's own endpoints are not
/// part of it. Every response here is built field by field from this file, never from a UI DTO, so a change to a page cannot change the contract.
/// Times carry the server's UTC offset (<c>2026-10-25T09:30:00-04:00</c>). See docs/design-api.md.</summary>
public static class ApiV1Endpoints
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 200;

    public static void MapApiV1Endpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1").AddEndpointFilter<ApiKeyFilter>();

        // Who the key is and which version answered: the first call to try with a new key.
        v1.MapGet("/whoami", (HttpContext http) =>
        {
            var key = (ApiKeyInfo)http.Items[ApiKeyFilter.ItemKey]!;
            return Results.Json(new { name = key.Name, scope = ApiKeys.ScopeName(key.Scope), version = AboutEndpoints.InstalledVersion });
        }).RequireApiScope(ApiKeyScope.Read);

        // Everything a dashboard card needs in one call: what is running, the queue's size and projected finish, the schedule.
        v1.MapGet("/status", async (
            IRunLoopController loopController,
            CurrentRunStateService runState,
            ICpuUsageSampler cpuSampler,
            IConfigStore configStore,
            IQueueService queue,
            IEncodeSchedule schedule,
            IActiveEncodeProcess activeProcess,
            IResumeStateStore resumeStore) =>
        {
            var snapshot = runState.GetSnapshot();
            var cpu = await cpuSampler.SampleAsync();
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var upNext = queue.GetUpNext(config, new QueueRunContext(snapshot.FileFullName, snapshot.LaneIsResumedById));
            var scheduleStatus = schedule.GetStatus(config);
            var etaText = RunEndpoints.ComputeQueueEtaText(upNext, runState, schedule, scheduleStatus);

            var nextRunUtc = loopController.NextRunUtc;
            var secondsUntilNextRun = nextRunUtc is null ? (int?)null : Math.Max(0, (int)Math.Ceiling((nextRunUtc.Value - DateTimeOffset.UtcNow).TotalSeconds));

            object? current = null;
            if (snapshot.IsRunning)
            {
                var homeLane = config.Lanes.FirstOrDefault(l => l.DisplayName == snapshot.LaneDisplayName);
                string? landsIn = null;
                if (snapshot.FileFullName is not null)
                {
                    var assigned = resumeStore.Load(AppPaths.GetResumeFilePath())
                        .FirstOrDefault(e => string.Equals(e.FullName, snapshot.FileFullName, StringComparison.OrdinalIgnoreCase))?.DestinationLaneId;
                    var destination = string.IsNullOrWhiteSpace(assigned) ? null : config.Lanes.FirstOrDefault(l => l.Id == assigned);
                    landsIn = (destination ?? homeLane)?.DisplayName;
                }

                current = new
                {
                    lane = snapshot.LaneDisplayName,
                    engine = (homeLane?.Engine ?? EncoderEngine.HandBrake).ToString().ToLowerInvariant(),
                    file = snapshot.FileName,
                    type = snapshot.FileName is null ? null : LaneKinds.KindOf(homeLane, snapshot.FileName),
                    preset = snapshot.PresetName,
                    passthrough = PassthroughPreset.Is(snapshot.PresetName),
                    fileIndex = snapshot.FileIndex,
                    fileTotal = snapshot.FileTotal,
                    progressPercent = snapshot.ProgressPercent,
                    fps = snapshot.ProgressFps,
                    eta = snapshot.ProgressEta,
                    landsInLane = landsIn
                };
            }

            return Results.Json(new
            {
                version = AboutEndpoints.InstalledVersion,
                monitoring = loopController.IsRunning,
                stopping = loopController.IsStopping,
                running = snapshot.IsRunning,
                paused = activeProcess.IsPaused,
                runStartedAt = snapshot.RunStartedUtc is { } started ? new DateTimeOffset(DateTime.SpecifyKind(started, DateTimeKind.Utc)).ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) : null,
                current,
                secondsUntilNextRun,
                cpuPercent = cpu,
                schedule = ScheduleStateOf(scheduleStatus),
                hold = HoldOf(scheduleStatus.Hold),
                queue = new
                {
                    pending = upNext.Count(i => !i.IsError && !i.IsSkipped),
                    skipped = upNext.Count(i => !i.IsError && i.IsSkipped),
                    errors = upNext.Count(i => i.IsError),
                    etaState = scheduleStatus.Hold is not null ? "held" : etaText is null ? "none" : etaText == "Estimating" ? "estimating" : "projected",
                    eta = scheduleStatus.Hold is not null || etaText is null || etaText == "Estimating" ? null : etaText
                }
            });
        }).RequireApiScope(ApiKeyScope.Read);

        // The In Queue list in the order it will be processed. ?lane= (id or name) and ?status= (pending, skipped, error) narrow it; position is the file's
        // place in the whole queue whatever the filter.
        v1.MapGet("/queue", (string? lane, string? status, int? page, int? pageSize, IConfigStore configStore, IQueueService queue, CurrentRunStateService runState) =>
        {
            var wanted = (status ?? "").Trim().ToLowerInvariant();
            if (wanted.Length > 0 && wanted is not ("pending" or "skipped" or "error"))
                return ApiErrors.Result(StatusCodes.Status400BadRequest, "bad_request", "status must be pending, skipped or error.");

            var snapshot = runState.GetSnapshot();
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var upNext = queue.GetUpNext(config, new QueueRunContext(snapshot.FileFullName, snapshot.LaneIsResumedById));

            static string StatusOf(UpNextItem i) => i.IsError ? "error" : i.IsSkipped ? "skipped" : "pending";
            var numbered = upNext.Select((item, index) => (Item: item, Position: index + 1));
            if (!string.IsNullOrWhiteSpace(lane))
                numbered = numbered.Where(x => string.Equals(x.Item.LaneId, lane.Trim(), StringComparison.OrdinalIgnoreCase)
                                               || string.Equals(x.Item.LaneDisplayName, lane.Trim(), StringComparison.OrdinalIgnoreCase));
            if (wanted.Length > 0) numbered = numbered.Where(x => StatusOf(x.Item) == wanted);

            var all = numbered.ToList();
            var (shown, size, total) = PageOf(all, page, pageSize, 100, 500);
            return Results.Json(new
            {
                total,
                page = shown.Page,
                pageSize = size,
                items = shown.Items.Select(x => new
                {
                    position = x.Position,
                    laneId = x.Item.LaneId,
                    lane = x.Item.LaneDisplayName,
                    file = x.Item.FileName,
                    type = x.Item.Kind,
                    status = StatusOf(x.Item),
                    preset = x.Item.Preset,
                    engine = x.Item.Engine.ToString().ToLowerInvariant(),
                    priority = x.Item.Priority > 0 ? "higher" : x.Item.Priority < 0 ? "lower" : "normal",
                    sizeGb = Math.Round(x.Item.SizeGb, 3),
                    passthrough = x.Item.IsPassthrough,
                    resumed = x.Item.IsResumed,
                    unmatched = x.Item.IsFileBotUnmatched,
                    landsInLane = x.Item.DestinationLaneName
                }).ToList()
            });
        }).RequireApiScope(ApiKeyScope.Read);

        // The lanes, with how much waits in each.
        v1.MapGet("/lanes", (IConfigStore configStore, IQueueService queue, CurrentRunStateService runState) =>
        {
            var snapshot = runState.GetSnapshot();
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var upNext = queue.GetUpNext(config, new QueueRunContext(snapshot.FileFullName, snapshot.LaneIsResumedById));
            return Results.Json(new
            {
                lanes = config.Lanes.Select(l => new
                {
                    id = l.Id,
                    name = l.DisplayName,
                    enabled = l.Enabled,
                    engine = l.Engine.ToString().ToLowerInvariant(),
                    tvPreset = l.TvPreset,
                    moviePreset = l.MoviePreset,
                    queue = new
                    {
                        pending = upNext.Count(i => i.LaneId == l.Id && !i.IsError && !i.IsSkipped),
                        skipped = upNext.Count(i => i.LaneId == l.Id && !i.IsError && i.IsSkipped),
                        errors = upNext.Count(i => i.LaneId == l.Id && i.IsError)
                    }
                }).ToList()
            });
        }).RequireApiScope(ApiKeyScope.Read);

        // The schedule settings and where the schedule is right now.
        v1.MapGet("/schedule", (IConfigStore configStore, IEncodeSchedule schedule) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            return Results.Json(ScheduleBody(config.Schedule, schedule.GetStatus(config)));
        }).RequireApiScope(ApiKeyScope.Read);

        // The runs, newest first. ?period= (today, d7, d30, year, all) is the History page's windows; ?from= and ?to= (yyyy-MM-dd) give your own and win.
        v1.MapGet("/runs", (string? period, string? from, string? to, int? page, int? pageSize, IRunQueries history) =>
        {
            if (!history.IsAvailable()) return HistoryUnavailable();
            if (!TryWindow(period, from, to, out var fromDay, out var toDay, out var problem)) return ApiErrors.Result(StatusCodes.Status400BadRequest, "bad_request", problem!);

            var size = Math.Clamp(pageSize ?? DefaultPageSize, 1, MaxPageSize);
            var (runs, total, shownPage) = history.ListRuns(fromDay, toDay, page ?? 1, size);
            return Results.Json(new { total, page = shownPage, pageSize = size, runs = runs.Select(RunOf).ToList() });
        }).RequireApiScope(ApiKeyScope.Read);

        // One run, with its lanes and every file.
        v1.MapGet("/runs/{id:long}", (long id, IRunQueries history) =>
        {
            if (!history.IsAvailable()) return HistoryUnavailable();
            var detail = history.GetRun(id);
            if (detail is null) return ApiErrors.Result(StatusCodes.Status404NotFound, "not_found", $"There is no run {id}.");

            return Results.Json(new
            {
                run = RunOf(detail.Run),
                lanes = detail.Lanes.Select(l => new
                {
                    name = l.Name,
                    problems = l.Problems,
                    files = l.Files.Select(f => new
                    {
                        name = f.Name,
                        contentType = f.ContentType,
                        preset = f.Preset,
                        encoder = f.Encoder,
                        engine = f.Engine,
                        passthrough = f.Passthrough,
                        beforeGb = Math.Round(f.BeforeGb, 3),
                        afterGb = Math.Round(f.AfterGb, 3),
                        seconds = Math.Round(f.Seconds, 1),
                        startedAt = Time(f.StartedAt),
                        endedAt = Time(f.EndedAt),
                        success = f.Success,
                        counted = f.Counted,
                        avgFps = f.AvgFps is { } fps ? Math.Round(fps, 1) : (double?)null,
                        errorCode = f.ErrorCode,
                        failureReason = f.FailureReason,
                        warning = f.Warning,
                        arrStatus = f.ArrStatus,
                        redirectedToLane = f.RedirectedToLane,
                        homeLane = f.HomeLane,
                        diskFull = f.DiskFull
                    }).ToList()
                }).ToList()
            });
        }).RequireApiScope(ApiKeyScope.Read);

        // The Overview's figures for a period, from the running totals. ?period= as above (all by default).
        v1.MapGet("/stats", (string? period, IRunQueries history) =>
        {
            if (!history.IsAvailable()) return HistoryUnavailable();
            var chosen = string.IsNullOrWhiteSpace(period) ? "all" : period;
            if (!TryWindow(chosen, null, null, out var fromDay, out var toDay, out var problem)) return ApiErrors.Result(StatusCodes.Status400BadRequest, "bad_request", problem!);

            var s = history.GetStats(fromDay, toDay);
            return Results.Json(new
            {
                period = chosen.Trim().ToLowerInvariant(),
                from = fromDay is { } f ? f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                to = toDay is { } t ? t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                runs = s.Runs,
                files = s.Files,
                beforeGb = s.BeforeGb,
                afterGb = s.AfterGb,
                savedGb = Math.Round(s.BeforeGb - s.AfterGb, 3),
                seconds = s.Seconds,
                movies = s.Movies,
                videos = s.Videos,
                tvEpisodes = s.Tvs,
                splitUnknown = s.UnknownSplit,
                errors = s.Errors,
                warnings = s.Warnings,
                redirects = s.Redirects,
                avgFps = s.AvgFps,
                engines = new { handbrake = s.HandBrakeFiles, ffmpeg = s.FfmpegFiles, passthrough = s.PassthroughFiles },
                lanes = s.Lanes.Select(l => new { name = l.Name, files = l.Files }).ToList(),
                profiles = s.Profiles.Select(p => new { name = p.Name, files = p.Files }).ToList()
            });
        }).RequireApiScope(ApiKeyScope.Read);

        // A run's report page, the same as the web UI's. ?download=1 hands it over as a file.
        v1.MapGet("/reports/{id:long}", (long id, string? download, IRunArchive archive, IHtmlReportGenerator generator) =>
            HistoryEndpoints.RunReport(id, download is "1" or "true", archive, generator)
            ?? ApiErrors.Result(StatusCodes.Status404NotFound, "not_found", $"Run {id} has no report.")).RequireApiScope(ApiKeyScope.Read);

        // ---- control (needs a control key) ---------------------------------------------------------------------------------------------------

        // The hold in force, or null.
        v1.MapGet("/hold", (IExternalHold hold) => Results.Json(new { hold = HoldOf(hold.Current()) })).RequireApiScope(ApiKeyScope.Read);

        // Holds the queue: no new file starts until it is released (or lapses at "until"). The file in flight finishes unless suspendCurrent is true. Placing it
        // again changes the one in force and keeps its start time, so an automation can assert it as often as it likes.
        v1.MapPost("/hold", (HttpContext http, HoldRequest? request, IExternalHold hold, IEncodeSchedule schedule, IRunLogger logger) =>
        {
            var reason = request?.Reason?.Trim();
            if (reason is { Length: > MaxHoldReason }) return ApiErrors.Result(StatusCodes.Status400BadRequest, "bad_request", $"reason can be at most {MaxHoldReason} characters.");

            DateTimeOffset? until = null;
            if (!string.IsNullOrWhiteSpace(request?.Until))
            {
                if (!DateTimeOffset.TryParse(request.Until, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
                    return ApiErrors.Result(StatusCodes.Status400BadRequest, "bad_request", "until must be a date and time such as 2026-10-25T23:00:00-04:00 (no offset means the server's local time).");
                if (parsed <= DateTimeOffset.UtcNow) return ApiErrors.Result(StatusCodes.Status400BadRequest, "bad_request", "until is in the past.");
                if (parsed > DateTimeOffset.UtcNow.AddYears(1)) return ApiErrors.Result(StatusCodes.Status400BadRequest, "bad_request", "until is more than a year away; leave it out to hold until released.");
                until = parsed;
            }

            var placed = hold.Set(reason, until, request?.SuspendCurrent ?? false, KeyName(http));
            logger.Log($"API: the queue is held by '{placed.SetBy}'{(placed.Reason is null ? "" : $" ({placed.Reason})")}{(placed.Until is { } end ? $" until {end.ToLocalTime():g}" : "")}{(placed.SuspendCurrent ? "; the file in flight is suspended" : "")}.");
            schedule.Tick();   // freezes the file in flight now, if that was asked for
            return Results.Json(new { hold = HoldOf(placed) });
        }).RequireApiScope(ApiKeyScope.Control);

        v1.MapDelete("/hold", (HttpContext http, IExternalHold hold, IEncodeSchedule schedule, IRunLoopController loopController, IRunLogger logger) =>
        {
            var released = hold.Release();
            if (released)
            {
                logger.Log($"API: the hold was released by '{KeyName(http)}'.");
                schedule.Tick();             // thaws a file the hold froze
                loopController.TriggerNow(); // with monitoring on, start the next pass now instead of waiting out the countdown
            }

            return Results.Json(new { released, hold = (object?)null });
        }).RequireApiScope(ApiKeyScope.Control);

        // Runs one pass now: with monitoring on and idle it skips the countdown; with monitoring off it runs one pass without starting monitoring.
        v1.MapPost("/run", (HttpContext http, IRunLoopController loopController, IConfigStore configStore, IExternalHold hold, IRunLogger logger) =>
        {
            logger.Log($"API: run now, asked by '{KeyName(http)}'.");
            var held = hold.Current() is not null;
            if (loopController.TriggerNow()) return Results.Json(new { started = true, mode = "countdown", held }, statusCode: StatusCodes.Status202Accepted);
            if (loopController.IsRunning) return Results.Json(new { started = false, mode = "busy", held });

            var started = loopController.RunOnceInBackground(configStore.Load(AppPaths.GetConfigFilePath()));
            return Results.Json(new { started, mode = started ? "single-pass" : "busy", held }, statusCode: started ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
        }).RequireApiScope(ApiKeyScope.Control);

        v1.MapPost("/monitoring/start", (HttpContext http, IRunLoopController loopController, IConfigStore configStore, IRunLogger logger) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            loopController.Start(config, TimeSpan.FromSeconds(Math.Max(5, config.Repeat.PollIntervalSeconds)));
            logger.Log($"API: monitoring started by '{KeyName(http)}'.");
            return Results.Json(new { monitoring = loopController.IsRunning });
        }).RequireApiScope(ApiKeyScope.Control);

        // Stopping waits for the file in flight to finish, which can take a long time, so the call answers at once (202) and status shows "stopping".
        v1.MapPost("/monitoring/stop", (HttpContext http, IRunLoopController loopController, IRunLogger logger) =>
        {
            if (!loopController.IsRunning) return Results.Json(new { monitoring = false, stopping = false });

            logger.Log($"API: monitoring stop asked by '{KeyName(http)}'.");
            _ = Task.Run(async () => { try { await loopController.StopAsync(); } catch { /* the loop reports its own problems */ } });
            return Results.Json(new { monitoring = true, stopping = true }, statusCode: StatusCodes.Status202Accepted);
        }).RequireApiScope(ApiKeyScope.Control);

        v1.MapPost("/pause", (HttpContext http, IActiveEncodeProcess activeProcess, IRunLogger logger) =>
        {
            if (!activeProcess.IsRunning) return ApiErrors.Result(StatusCodes.Status409Conflict, "not_running", "No encode is running, so there is nothing to pause.");

            activeProcess.Pause();
            logger.Log($"API: the encode was paused by '{KeyName(http)}'.");
            return Results.Json(new { paused = activeProcess.IsPaused });
        }).RequireApiScope(ApiKeyScope.Control);

        v1.MapPost("/resume", (HttpContext http, IActiveEncodeProcess activeProcess, IEncodeSchedule schedule, IRunLogger logger) =>
        {
            // Resuming by hand while a hold has the encode frozen would just be frozen again on the next tick, so it counts as letting the file finish.
            if (schedule.GetStatus().IsHeld) schedule.RunAnyway();
            activeProcess.Resume();
            logger.Log($"API: the encode was resumed by '{KeyName(http)}'.");
            return Results.Json(new { paused = activeProcess.IsPaused });
        }).RequireApiScope(ApiKeyScope.Control);

        // Lifts the schedule's off-hours hold for the rest of this daytime window (it does not lift an external hold: release that).
        v1.MapPost("/schedule/run-anyway", (HttpContext http, IEncodeSchedule schedule, IConfigStore configStore, IRunLogger logger) =>
        {
            schedule.RunAnyway();
            logger.Log($"API: run anyway, asked by '{KeyName(http)}'.");
            return Results.Json(new { state = ScheduleStateOf(schedule.GetStatus(configStore.Load(AppPaths.GetConfigFilePath()))) });
        }).RequireApiScope(ApiKeyScope.Control);

        // Changes the schedule settings - the same ones the Scheduler page edits, so this changes what you see there. Only the fields you send change;
        // the result must be a valid schedule or nothing is saved.
        v1.MapPatch("/schedule", (HttpContext http, JsonObject? patchBody, IConfigStore configStore, IEncodeSchedule schedule, IRunLogger logger) =>
        {
            if (patchBody is null || patchBody.Count == 0) return ApiErrors.Result(StatusCodes.Status400BadRequest, "bad_request", "Send a JSON object with the schedule fields to change.");

            var unknown = patchBody.Select(kv => kv.Key).Where(k => !SchedulePatchFields.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
            if (unknown.Count > 0) return ApiErrors.Result(StatusCodes.Status400BadRequest, "bad_request", $"Unknown field(s): {string.Join(", ", unknown)}. Allowed: {string.Join(", ", SchedulePatchFields)}.");

            var config = configStore.Load(AppPaths.GetConfigFilePath());
            ScheduleDto merged;
            try { merged = MergeSchedule(ScheduleEndpoints.ToDto(config.Schedule), patchBody); }
            catch (FormatException ex) { return ApiErrors.Result(StatusCodes.Status400BadRequest, "bad_request", ex.Message); }

            var candidate = new ScheduleSettings();
            ScheduleEndpoints.Apply(candidate, merged);
            candidate.Enabled = true;   // checked as if it were on: a schedule that would be invalid the moment it is switched on is not saved either
            var issues = ScheduleValidator.Validate(candidate);
            if (issues.Count > 0)
                return Results.Json(new { error = new { code = "invalid_schedule", message = "That schedule is not valid, so nothing was changed.", issues = issues.Select(i => new { field = i.Field, message = i.Message }).ToList() } },
                    statusCode: StatusCodes.Status400BadRequest);

            configStore.Update(AppPaths.GetConfigFilePath(), c => { ScheduleEndpoints.Apply(c.Schedule, merged); return true; });
            logger.Log($"API: the schedule was changed by '{KeyName(http)}' ({string.Join(", ", patchBody.Select(kv => kv.Key))}).");
            schedule.Tick();
            var saved = configStore.Load(AppPaths.GetConfigFilePath());
            return Results.Json(ScheduleBody(saved.Schedule, schedule.GetStatus(saved)));
        }).RequireApiScope(ApiKeyScope.Control);
    }

    private const int MaxHoldReason = 200;

    private static readonly string[] SchedulePatchFields =
    {
        "enabled", "mode", "dayStart", "dayEnd", "weekendDayStart", "weekendDayEnd", "days", "dayPriority", "nightPriority", "onlyEncodeOffHours", "whenDayStarts"
    };

    public sealed record HoldRequest(string? Reason, string? Until, bool? SuspendCurrent);

    private static string KeyName(HttpContext http) => (http.Items[ApiKeyFilter.ItemKey] as ApiKeyInfo)?.Name ?? "api";

    /// <summary>The current schedule with the fields of a PATCH body laid over it. Throws <see cref="FormatException"/> with a plain message for a value of the wrong kind.</summary>
    internal static ScheduleDto MergeSchedule(ScheduleDto current, JsonObject patch)
    {
        JsonNode? Field(string name) => patch.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

        bool Flag(string name, bool fallback) => Field(name) switch
        {
            null => fallback,
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            _ => throw new FormatException($"{name} must be true or false.")
        };

        string Text(string name, string fallback) => Field(name) switch
        {
            null => fallback,
            JsonValue v when v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) => s.Trim(),
            _ => throw new FormatException($"{name} must be text.")
        };

        string Choice<TEnum>(string name, string fallback) where TEnum : struct, Enum
        {
            var text = Text(name, fallback);
            return Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                ? parsed.ToString()
                : throw new FormatException($"{name} must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}.");
        }

        var days = current.Days;
        if (Field("days") is { } daysNode)
        {
            if (daysNode is not JsonArray array || array.Count != 7) throw new FormatException("days must be a list of seven {start, end} entries, Sunday first.");
            days = array.Select((d, i) => d is JsonObject o && o["start"] is JsonValue s && o["end"] is JsonValue e
                    && s.TryGetValue<string>(out var start) && e.TryGetValue<string>(out var end)
                ? new ScheduleDayDto(current.Days[i].Day, start, end)
                : throw new FormatException("each entry of days needs a start and an end (HH:mm).")).ToList();
        }

        return current with
        {
            Enabled = Flag("enabled", current.Enabled),
            Mode = Choice<ScheduleMode>("mode", current.Mode),
            DayStart = Text("dayStart", current.DayStart),
            DayEnd = Text("dayEnd", current.DayEnd),
            WeekendDayStart = Text("weekendDayStart", current.WeekendDayStart),
            WeekendDayEnd = Text("weekendDayEnd", current.WeekendDayEnd),
            Days = days,
            DayPriority = Choice<EncodePriority>("dayPriority", current.DayPriority),
            NightPriority = Choice<EncodePriority>("nightPriority", current.NightPriority),
            OnlyEncodeOffHours = Flag("onlyEncodeOffHours", current.OnlyEncodeOffHours),
            WhenDayStarts = Choice<DayHoldBehavior>("whenDayStarts", current.WhenDayStarts)
        };
    }

    // ---- shared shapes -------------------------------------------------------------------------------------------------------------------

    private static object? HoldOf(HoldInfo? hold) => hold is null ? null : new
    {
        reason = hold.Reason,
        since = hold.Since.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
        until = hold.Until is { } end ? end.ToLocalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) : null,
        suspendCurrent = hold.SuspendCurrent,
        setBy = hold.SetBy
    };

    private static IResult HistoryUnavailable() =>
        ApiErrors.Result(StatusCodes.Status503ServiceUnavailable, "history_unavailable", "The run history database could not be opened, so history is not available right now.");

    private static object ScheduleBody(ScheduleSettings settings, ScheduleStatus status)
    {
        var s = ScheduleEndpoints.ToDto(settings);
        return new
        {
            settings = new
            {
                enabled = s.Enabled,
                mode = s.Mode,
                dayStart = s.DayStart,
                dayEnd = s.DayEnd,
                weekendDayStart = s.WeekendDayStart,
                weekendDayEnd = s.WeekendDayEnd,
                days = s.Days.Select(d => new { day = d.Day, start = d.Start, end = d.End }).ToList(),
                dayPriority = s.DayPriority,
                nightPriority = s.NightPriority,
                onlyEncodeOffHours = s.OnlyEncodeOffHours,
                whenDayStarts = s.WhenDayStarts
            },
            state = ScheduleStateOf(status)
        };
    }

    private static object ScheduleStateOf(ScheduleStatus s) => new
    {
        enabled = s.Enabled,
        isDaytime = s.IsDaytime,
        priority = s.Priority?.ToString(),
        onlyOffHours = s.OnlyOffHours,
        isHeld = s.IsHeld,
        overrideActive = s.OverrideActive,
        nextChange = s.NextChange
    };

    private static object RunOf(RunSummary r) => new
    {
        id = r.Id,
        number = r.Number,
        endedAt = Time(r.EndedAt),
        durationSeconds = r.DurationSeconds,
        files = r.Files,
        beforeGb = r.BeforeGb,
        afterGb = r.AfterGb,
        savedGb = Math.Round(r.BeforeGb - r.AfterGb, 3),
        errors = r.Errors,
        warnings = r.Warnings,
        redirects = r.Redirects,
        movies = r.Movies,
        videos = r.Videos,
        tvEpisodes = r.Tvs,
        avgFps = r.AvgFps,
        engines = r.HandBrakeFiles is null && r.FfmpegFiles is null && r.PassthroughFiles is null
            ? null
            : new { handbrake = r.HandBrakeFiles, ffmpeg = r.FfmpegFiles, passthrough = r.PassthroughFiles },
        counted = r.Counted,
        hasReport = r.HasReport,
        reportUrl = r.HasReport ? $"/api/v1/reports/{r.Id}" : null
    };

    /// <summary>A server-local time with its UTC offset, or null.</summary>
    private static string? Time(DateTime? value) =>
        value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(v)).ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) : null;

    private static ((IReadOnlyList<T> Items, int Page) Page, int Size, int Total) PageOf<T>(IReadOnlyList<T> all, int? page, int? pageSize, int defaultSize, int maxSize)
    {
        var size = Math.Clamp(pageSize ?? defaultSize, 1, maxSize);
        var lastPage = Math.Max(1, (all.Count + size - 1) / size);
        var shown = Math.Clamp(page ?? 1, 1, lastPage);
        return ((all.Skip((shown - 1) * size).Take(size).ToList(), shown), size, all.Count);
    }

    /// <summary>The History page's windows (today, d7 = the last 7 days including today, d30, year, all), or an explicit from/to day pair, which wins.</summary>
    internal static bool TryWindow(string? period, string? from, string? to, out DateTime? fromDay, out DateTime? toDay, out string? problem)
    {
        fromDay = toDay = null;
        problem = null;

        if (!string.IsNullOrWhiteSpace(from) || !string.IsNullOrWhiteSpace(to))
        {
            if (!string.IsNullOrWhiteSpace(from))
            {
                if (!DateTime.TryParseExact(from.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var f)) { problem = "from must be a day, yyyy-MM-dd."; return false; }
                fromDay = f;
            }

            if (!string.IsNullOrWhiteSpace(to))
            {
                if (!DateTime.TryParseExact(to.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) { problem = "to must be a day, yyyy-MM-dd."; return false; }
                toDay = t;
            }

            return true;
        }

        var today = DateTime.Now.Date;
        switch ((period ?? "").Trim().ToLowerInvariant())
        {
            case "":
            case "all": return true;
            case "today": fromDay = today; return true;
            case "d7": fromDay = today.AddDays(-6); return true;
            case "d30": fromDay = today.AddDays(-29); return true;
            case "year": fromDay = today.AddYears(-1).AddDays(1); return true;
            default:
                problem = "period must be today, d7, d30, year or all.";
                return false;
        }
    }
}
