using Microsoft.AspNetCore.Http;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Web.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

public static class LaneEndpoints
{
    // A lane's presets are checked against ITS OWN encoder's profile catalog.
    private static List<ValidationIssueDto> Validate(LaneConfig lane, CompressarrConfig config, IPathExpander pathExpander, IEncoderResolver encoders)
    {
        return LaneValidator.Validate(lane, config, pathExpander, encoders.PresetsFor(lane.Engine))
            .Select(i => new ValidationIssueDto(i.Field, i.Message)).ToList();
    }

    public static void MapLaneEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/lanes", (IConfigStore configStore, IPathExpander pathExpander, IEncoderResolver encoders) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            return Results.Json(config.Lanes.Select(lane => ConfigMapping.ToLaneDto(lane, Validate(lane, config, pathExpander, encoders))).ToList());
        });

        app.MapPost("/api/lanes", (IConfigStore configStore, IPathExpander pathExpander, IEncoderResolver encoders) =>
        {
            var dto = configStore.Update(AppPaths.GetConfigFilePath(), config =>
            {
                var lane = LaneConfig.CreateNew($"New Lane {config.Lanes.Count + 1}");
                config.Lanes.Add(lane);
                return ConfigMapping.ToLaneDto(lane, Validate(lane, config, pathExpander, encoders));
            });

            return Results.Json(dto);
        });

        // Copies a lane under a new name: every field as the card currently shows it (the request carries it), Input
        // and Output included. The copy always starts DISABLED, which is what makes an exact copy safe - two enabled lanes
        // can't watch the same folder, and that is checked when the copy is enabled (see the PUT above). The new lane sits
        // right after the one it was copied from.
        app.MapPost("/api/lanes/duplicate", (DuplicateLaneRequest request, IConfigStore configStore, IPathExpander pathExpander, IEncoderResolver encoders) =>
        {
            var name = (request.DisplayName ?? "").Trim();
            if (name.Length == 0) return Results.BadRequest(new { message = "Enter a name for the new lane." });

            var outcome = configStore.Update(AppPaths.GetConfigFilePath(), config =>
            {
                if (config.Lanes.Any(l => string.Equals(l.DisplayName.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                {
                    return (Dto: (LaneDto?)null, Error: $"There is already a lane named \"{name}\". Choose a different name.");
                }

                var lane = new LaneConfig();
                ConfigMapping.ApplyLaneDto(lane, request.Lane);
                lane.DisplayName = name;
                lane.Enabled = false;

                var sourceIndex = config.Lanes.FindIndex(l => l.Id == request.Lane.Id);
                config.Lanes.Insert(sourceIndex >= 0 ? sourceIndex + 1 : config.Lanes.Count, lane);
                return (Dto: ConfigMapping.ToLaneDto(lane, Validate(lane, config, pathExpander, encoders)), Error: (string?)null);
            });

            return outcome.Error is null ? Results.Json(outcome.Dto) : Results.BadRequest(new { message = outcome.Error });
        });

        app.MapPut("/api/lanes/{id}", (string id, LaneDto dto, IConfigStore configStore, IPathExpander pathExpander, IEncoderResolver encoders) =>
        {
            var outcome = configStore.Update(AppPaths.GetConfigFilePath(), config =>
            {
                var lane = config.Lanes.FirstOrDefault(l => l.Id == id);
                if (lane is null) return (Found: false, Dto: (LaneDto?)null, Error: (string?)null);

                // Two enabled lanes must not watch the same Input folder (or one inside the other): both would pick up the
                // same files. A save that would CREATE such an overlap - enabling the lane, or pointing it at a different
                // Input - is refused. A lane that already overlapped and is otherwise unchanged still saves, so an
                // existing setup (an upgrade, an import) is flagged but never locked out of unrelated edits or stopped.
                var candidate = new LaneConfig { Id = lane.Id };
                ConfigMapping.ApplyLaneDto(candidate, dto);
                var unchangedAndRunning = lane.Enabled && LaneFolders.SameFolder(pathExpander, lane.Input, candidate.Input);
                if (!unchangedAndRunning && LaneFolders.FindConflict(candidate, config.Lanes, pathExpander) is { } conflict)
                {
                    return (Found: true, Dto: null, Error: conflict.Describe());
                }

                ConfigMapping.ApplyLaneDto(lane, dto);
                return (Found: true, Dto: ConfigMapping.ToLaneDto(lane, Validate(lane, config, pathExpander, encoders)), Error: null);
            });

            if (!outcome.Found) return Results.NotFound();
            return outcome.Error is null ? Results.Json(outcome.Dto) : Results.BadRequest(new { message = outcome.Error, field = "input" });
        });

        // Checks a lane as it is currently typed on the page, without saving it - so a field that was red can
        // turn normal the moment it is fixed instead of waiting for the next Save.
        app.MapPost("/api/lanes/validate", (LaneDto dto, IConfigStore configStore, IPathExpander pathExpander, IEncoderResolver encoders) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var lane = new LaneConfig { Id = dto.Id };
            ConfigMapping.ApplyLaneDto(lane, dto);
            return Results.Json(Validate(lane, config, pathExpander, encoders));
        });

        // How many queued files in OTHER lanes are set to land in this lane's library (see
        // ResumeEntry.DestinationLaneId) - asked before a lane is deleted so the warning can say what would
        // happen to them: each waits in Output until the user picks another destination.
        app.MapGet("/api/lanes/{id}/redirected-files", (string id, IResumeStateStore resumeStore) =>
        {
            var waiting = resumeStore.Load(AppPaths.GetResumeFilePath()).Count(e =>
                e.DestinationLaneId == id &&
                e.LaneId != id &&
                !e.Removed &&
                e.Status is ResumeStatus.Pending or ResumeStatus.MoveFailed);
            return Results.Json(new { waitingFiles = waiting });
        });

        app.MapDelete("/api/lanes/{id}", (string id, IConfigStore configStore) =>
        {
            var removed = configStore.Update(AppPaths.GetConfigFilePath(), config => config.Lanes.RemoveAll(l => l.Id == id));
            return removed == 0 ? Results.NotFound() : Results.NoContent();
        });
    }
}
