using Compressarr.Core.Config;
using Compressarr.Core.FFmpeg;
using Compressarr.Core.Presets;
using Compressarr.Web.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

/// <summary>The Encoder page: where HandBrakeCLI and ffmpeg are and what extra options every encode gets,
/// plus the Profiles page's list (both encoders' profiles). The install/version helpers the Check/Install
/// buttons use are HandBrakeEndpoints and FFmpegEndpoints.</summary>
public static class EncoderEndpoints
{
    private static EncoderSettingsDto ToDto(CompressarrConfig config, IHandBrakeProfileStore profiles, IFFmpegProfileStore ffmpegProfiles, IPathExpander pathExpander)
    {
        var all = profiles.GetAll();
        var issues = EncoderValidator.Validate(config, pathExpander).Select(i => new ValidationIssueDto(i.Field, i.Message)).ToList();

        return new EncoderSettingsDto(
            HandBrakeCliPath: config.HandBrake.CliPath,
            HandBrakeOptions: config.HandBrake.Options,
            BuiltInProfileCount: all.Count(p => p.IsBuiltIn),
            UserProfileCount: all.Count(p => !p.IsBuiltIn),
            HandBrakeLanes: config.Lanes.Where(l => l.Enabled && l.Engine == EncoderEngine.HandBrake).Select(l => l.DisplayName).ToList(),
            ValidationIssues: issues,
            FfmpegPath: config.FFmpeg.Path,
            FfmpegProbePath: config.FFmpeg.ProbePath,
            FfmpegOptions: config.FFmpeg.Options,
            FfmpegProfileCount: ffmpegProfiles.GetAll().Count,
            FfmpegLanes: config.Lanes.Where(l => l.Enabled && l.Engine == EncoderEngine.FFmpeg).Select(l => l.DisplayName).ToList(),
            FfmpegChannel: config.FFmpeg.Channel.ToString().ToLowerInvariant());
    }

    public static void MapEncoderEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/encoder", (IConfigStore configStore, IHandBrakeProfileStore profiles, IFFmpegProfileStore ffmpegProfiles, IPathExpander pathExpander) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            return Results.Json(ToDto(config, profiles, ffmpegProfiles, pathExpander));
        });

        app.MapPut("/api/encoder", (EncoderSettingsDto dto, IConfigStore configStore, IHandBrakeProfileStore profiles, IFFmpegProfileStore ffmpegProfiles, IPathExpander pathExpander) =>
        {
            var result = configStore.Update(AppPaths.GetConfigFilePath(), config =>
            {
                config.HandBrake.CliPath = dto.HandBrakeCliPath;
                config.HandBrake.Options = dto.HandBrakeOptions;
                if (dto.FfmpegPath is not null) config.FFmpeg.Path = dto.FfmpegPath;
                if (dto.FfmpegProbePath is not null) config.FFmpeg.ProbePath = dto.FfmpegProbePath;
                if (dto.FfmpegOptions is not null) config.FFmpeg.Options = dto.FfmpegOptions;
                if (Enum.TryParse<FFmpegChannel>(dto.FfmpegChannel, ignoreCase: true, out var channel)) config.FFmpeg.Channel = channel;
                return ToDto(config, profiles, ffmpegProfiles, pathExpander);
            });
            return Results.Json(result);
        });

        app.MapGet("/api/profiles", (IConfigStore configStore, IHandBrakeProfileStore profiles, IFFmpegProfileStore ffmpegProfiles) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());

            List<string> UsedBy(string name, EncoderEngine engine) => config.Lanes
                .Where(l => l.Engine == engine
                         && (string.Equals(l.TvPreset, name, StringComparison.OrdinalIgnoreCase)
                          || string.Equals(l.MoviePreset, name, StringComparison.OrdinalIgnoreCase)))
                .Select(l => l.DisplayName)
                .ToList();

            var rows = profiles.GetAll().Select(p => new ProfileDto(
                Engine: "handbrake",
                Name: p.Name,
                BuiltIn: p.IsBuiltIn,
                Container: HandBrakeProfileSummary.Container(p.FileFormat),
                Video: HandBrakeProfileSummary.Video(p.Definition),
                Audio: HandBrakeProfileSummary.Audio(p.Definition),
                Description: p.Definition["PresetDescription"]?.GetValue<string>() ?? "",
                UsedBy: UsedBy(p.Name, EncoderEngine.HandBrake))).ToList();

            rows.AddRange(ffmpegProfiles.GetAll().Select(p => new ProfileDto(
                Engine: "ffmpeg",
                Name: p.Name,
                BuiltIn: p.IsBuiltIn,
                Container: p.Extension.TrimStart('.').ToUpperInvariant(),
                Video: FFmpegProfileSummary.Video(p),
                Audio: FFmpegProfileSummary.Audio(p),
                Description: p.Description,
                UsedBy: UsedBy(p.Name, EncoderEngine.FFmpeg))));

            return Results.Json(new ProfileListDto(
                rows,
                AppPaths.GetHandBrakeProfilesFilePath(),
                AppPaths.GetHandBrakeActivePresetsFilePath(),
                profiles.UserFileError,
                AppPaths.GetFFmpegProfilesFilePath(),
                ffmpegProfiles.UserFileError));
        });
    }
}
