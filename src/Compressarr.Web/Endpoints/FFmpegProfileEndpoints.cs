using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.FFmpeg;
using Compressarr.Core.Presets;
using Compressarr.Web.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

/// <summary>Create, edit, duplicate, delete ffmpeg profiles, "Duplicate as ffmpeg..." from a HandBrake profile,
/// and the editor's "Preview decisions" for a real file. Built-ins are locked (readable, duplicable).</summary>
public static class FFmpegProfileEndpoints
{
    private static IResult Invalid(string field, string message) =>
        Results.BadRequest(new { message, validationIssues = new[] { new ValidationIssueDto(field, message) } });

    private static IResult Invalid(IEnumerable<Compressarr.Core.Validation.ValidationIssue> issues)
    {
        var list = issues.Select(i => new ValidationIssueDto(i.Field, i.Message)).ToList();
        return Results.BadRequest(new { message = list[0].Message, validationIssues = list });
    }

    private static string Quote(string arg) => arg.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0 ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg;

    public static void MapFFmpegProfileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/profiles/ffmpeg/{name}", (string name, IFFmpegProfileStore store, IConfigStore configStore, IResumeStateStore resumeStore) =>
        {
            var profile = store.Find(name);
            if (profile is null) return Results.NotFound(new { message = $"There is no ffmpeg profile named '{name}'." });

            var usage = ProfileReferences.Find(configStore.Load(AppPaths.GetConfigFilePath()), resumeStore.Load(AppPaths.GetResumeFilePath()), profile.Name, EncoderEngine.FFmpeg);
            return Results.Json(new FFmpegProfileEditDto(profile, profile.IsBuiltIn || profile.IsCatalog, usage.Lanes.ToList(), profile.IsCatalog));
        });

        app.MapPost("/api/profiles/ffmpeg", (FFmpegProfile profile, IFFmpegProfileStore store) =>
        {
            var issues = FFmpegProfileValidator.Validate(profile);
            if (issues.Count > 0) return Invalid(issues);

            var name = profile.Name.Trim();
            if (store.Find(name) is not null) return Invalid("name", $"A profile named '{name}' already exists.");

            profile.Name = name;
            store.AddUserProfiles(new[] { profile });
            return Results.Json(new { name });
        });

        app.MapPut("/api/profiles/ffmpeg/{name}", (string name, FFmpegProfile profile, IFFmpegProfileStore store, IConfigStore configStore, IResumeStateStore resumeStore) =>
        {
            var existing = store.Find(name);
            if (existing is null) return Results.NotFound(new { message = $"There is no ffmpeg profile named '{name}'." });
            if (existing.IsBuiltIn) return Results.BadRequest(new { message = $"'{existing.Name}' is a built-in profile and can't be changed. Duplicate it to make your own." });
            if (existing.IsCatalog) return Results.BadRequest(new { message = $"'{existing.Name}' is a catalog profile and can't be changed. Duplicate it to make your own." });

            var issues = FFmpegProfileValidator.Validate(profile);
            if (issues.Count > 0) return Invalid(issues);

            var newName = profile.Name.Trim();
            var other = store.Find(newName);
            if (other is not null && !string.Equals(other.Name, existing.Name, StringComparison.OrdinalIgnoreCase))
            {
                return Invalid("name", $"A profile named '{newName}' already exists.");
            }

            profile.Name = newName;
            store.ReplaceUserProfile(existing.Name, profile);
            if (!string.Equals(existing.Name, newName, StringComparison.Ordinal))
            {
                ProfileReferences.Repoint(configStore, resumeStore, new Dictionary<string, string> { [existing.Name] = newName }, EncoderEngine.FFmpeg);
            }
            return Results.Json(new { name = newName });
        });

        app.MapDelete("/api/profiles/ffmpeg/{name}", (string name, IFFmpegProfileStore store, IConfigStore configStore, IResumeStateStore resumeStore) =>
        {
            var existing = store.Find(name);
            if (existing is null) return Results.NotFound(new { message = $"There is no ffmpeg profile named '{name}'." });
            if (existing.IsBuiltIn) return Results.BadRequest(new { message = $"'{existing.Name}' is a built-in profile and can't be deleted." });

            var usage = ProfileReferences.Find(configStore.Load(AppPaths.GetConfigFilePath()), resumeStore.Load(AppPaths.GetResumeFilePath()), existing.Name, EncoderEngine.FFmpeg);
            if (usage.InUse)
            {
                var where = new List<string>();
                if (usage.Lanes.Count > 0) where.Add($"lane{(usage.Lanes.Count == 1 ? "" : "s")} {string.Join(", ", usage.Lanes)}");
                if (usage.QueuedFiles > 0) where.Add($"{usage.QueuedFiles} queued file{(usage.QueuedFiles == 1 ? "" : "s")}");
                return Results.Conflict(new { message = $"'{existing.Name}' is still used by {string.Join(" and ", where)}. Pick another profile there first." });
            }

            if (existing.IsCatalog) store.RemoveCatalogProfile(existing.Name);
            else store.RemoveUserProfile(existing.Name);
            return Results.Ok();
        });

        app.MapPost("/api/profiles/ffmpeg/{name}/duplicate", (string name, DuplicateProfileRequest? request, IFFmpegProfileStore store) =>
        {
            var source = store.Find(name);
            if (source is null) return Results.NotFound(new { message = $"There is no ffmpeg profile named '{name}'." });

            var wanted = string.IsNullOrWhiteSpace(request?.Name) ? $"{source.Name} copy" : request!.Name!.Trim();
            var newName = wanted;
            for (var n = 2; store.Find(newName) is not null; n++) newName = $"{wanted} {n}";

            var copy = source.Clone();
            copy.Name = newName;
            store.AddUserProfiles(new[] { copy });
            return Results.Json(new { name = newName });
        });

        // "Duplicate as ffmpeg...": the closest ffmpeg profile to a HandBrake one, saved as the user's own,
        // with the list of what carried over and what ffmpeg can't express.
        app.MapPost("/api/profiles/handbrake/{name}/duplicate-as-ffmpeg", (string name, IHandBrakeProfileStore handBrake, IFFmpegProfileStore store) =>
        {
            var source = handBrake.Find(name);
            if (source is null) return Results.NotFound(new { message = $"There is no HandBrake profile named '{name}'." });

            var conversion = HandBrakeToFFmpeg.Convert(source.Name, source.Definition);
            var profile = conversion.Profile;

            // The converted profile keeps the original's name; ffmpeg's own catalog may already use it (the
            // built-ins share names across encoders), so it is made unique the same way Duplicate does.
            var wanted = profile.Name;
            var newName = wanted;
            for (var n = 2; store.Find(newName) is not null; n++) newName = $"{wanted} {n}";
            profile.Name = newName;

            store.AddUserProfiles(new[] { profile });
            return Results.Json(new { name = newName, carried = conversion.Carried, notCarried = conversion.NotCarried });
        });

        // "Preview decisions": reads a real file and says what this profile would do with it - which
        // tracks are kept or dropped and why, deinterlace, crop, HDR - plus the exact command.
        app.MapPost("/api/profiles/ffmpeg/preview", async (FFmpegPreviewRequest request, IConfigStore configStore, IPathExpander pathExpander, IMediaProbe probe, CancellationToken ct) =>
        {
            var path = pathExpander.Expand(request.Path?.Trim().Trim('"') ?? "");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return Results.Json(new { ok = false, error = "That file doesn't exist. Type or paste the full path of a video file on this computer." });

            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var probePath = pathExpander.Expand(config.FFmpeg.ProbePath);
            if (!pathExpander.PathExists(config.FFmpeg.ProbePath)) return Results.Json(new { ok = false, error = $"ffprobe was not found at '{probePath}'. Set it up on the Encoder page first." });

            var result = await probe.ProbeAsync(probePath, path, ct);
            if (result is null) return Results.Json(new { ok = false, error = "ffprobe could not read that file." });

            var profile = request.Profile;
            var plan = FFmpegPlanner.Plan(profile, result);
            if (plan is null) return Results.Json(new { ok = false, error = "That file has no video stream." });

            if (profile.Crop == "auto" && plan.Video.Width > 0)
            {
                if (pathExpander.PathExists(config.FFmpeg.Path))
                {
                    plan.Crop = await FFmpegCropDetector.DetectAsync(pathExpander.Expand(config.FFmpeg.Path), path, result.DurationSeconds, plan.Video, ct);
                    plan.CropReason = plan.Crop is null ? "no black bars were found" : $"black bars found - cropping to {plan.Crop.Width}x{plan.Crop.Height}";
                }
                else
                {
                    plan.CropReason = "ffmpeg isn't installed, so the picture wasn't sampled for black bars";
                }
            }

            var command = FFmpegCommandBuilder.Build(profile, result, plan, path, "<output file>", config.FFmpeg.Options);

            object Row(StreamDecision d) => new
            {
                index = d.Stream.Index,
                kind = d.Stream.Kind.ToString().ToLowerInvariant(),
                language = d.Stream.Language,
                codec = d.Stream.Codec,
                channels = d.Stream.Channels,
                title = d.Stream.Title,
                action = d.Action,
                reason = d.Reason
            };

            var duration = result.DurationSeconds is { } s ? TimeSpan.FromSeconds(s) : (TimeSpan?)null;
            return Results.Json(new
            {
                ok = true,
                file = Path.GetFileName(path),
                duration = duration is { } d ? $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}" : null,
                video = new
                {
                    index = plan.Video.Index,
                    codec = plan.Video.Codec,
                    size = $"{plan.Video.Width}x{plan.Video.Height}",
                    pixFmt = plan.Video.PixFmt,
                    hdr = plan.Video.IsHdr,
                    dynamicHdr = result.HasDynamicHdrMetadata ? result.DynamicHdrName : null,
                    hasFallback = !string.IsNullOrWhiteSpace(profile.FallbackHandBrakePreset),
                    dropsDynamicHdr = string.IsNullOrWhiteSpace(profile.FallbackHandBrakePreset) && profile.AllowDroppingDynamicHdr,
                    canDropDynamicHdr = result.DynamicHdrCanBeDropped,
                    dolbyVisionProfile = result.DolbyVisionProfileLabel
                },
                deinterlace = new { apply = plan.Deinterlace, reason = plan.DeinterlaceReason },
                crop = new { rect = plan.Crop?.ToString(), reason = plan.CropReason },
                audio = plan.Audio.Select(Row),
                subtitles = plan.Subtitles.Select(Row),
                dropped = plan.Dropped.Select(Row),
                notes = plan.Notes,
                chapters = plan.KeepChapters,
                command = string.Join(" ", command.Select(Quote))
            });
        });
    }
}
