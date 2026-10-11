using Compressarr.Core.Config;
using Compressarr.Core.FFmpeg;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

/// <summary>The Encoder page's ffmpeg card: is it there, what can this build do, find one already on the
/// machine, or download a managed copy (after the user has been asked - the page shows name, size and
/// source first).</summary>
public static class FFmpegEndpoints
{
    // The encoders worth showing as chips, in the order a reader looks for them.
    private static readonly string[] ChipEncoders = { "libx265", "libx264", "libsvtav1", "eac3", "aac", "ac3", "libopus", "hevc_nvenc", "av1_nvenc" };

    /// <summary>The channel a call asks for (?channel=stable|latest - the Encoder page passes its not-yet-saved choice), else
    /// the saved one.</summary>
    private static FFmpegChannel ResolveChannel(string? requested, CompressarrConfig config) =>
        Enum.TryParse<FFmpegChannel>(requested, ignoreCase: true, out var channel) ? channel : config.FFmpeg.Channel;

    public static void MapFFmpegEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/ffmpeg/status", (IConfigStore configStore, IPathExpander pathExpander) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            return Results.Json(new
            {
                exists = pathExpander.PathExists(config.FFmpeg.Path),
                probeExists = pathExpander.PathExists(config.FFmpeg.ProbePath)
            });
        });

        // Asks ffmpeg what it can do (version, encoders, whether an NVENC session opens) and flags each
        // profile that needs something this build lacks. Runs a few short processes, so the page asks for
        // it separately from the (instant) settings call.
        app.MapGet("/api/ffmpeg/capabilities", async (IConfigStore configStore, IPathExpander pathExpander, IFFmpegCapabilityProbe probe, IFFmpegProfileStore profiles, CancellationToken ct) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var path = pathExpander.Expand(config.FFmpeg.Path);
            var caps = await probe.DetectAsync(path, ct);
            if (caps is null) return Results.Json(new { found = false });

            // A hardware encoder is ticked only when a GPU session really opens for it; AV1 is its own test (RTX 40-series or newer).
            var chips = ChipEncoders.Select(e => new
            {
                name = e,
                ok = e.EndsWith("_nvenc", StringComparison.Ordinal) ? caps.CanUse(e) : caps.Has(e),
                note = e.EndsWith("_nvenc", StringComparison.Ordinal) && caps.Has(e)
                    ? (caps.SessionOk(e) == true ? "NVIDIA GPU session OK" : e == "av1_nvenc" ? "no NVIDIA GPU session for AV1 (needs an RTX 40-series or newer)" : "no NVIDIA GPU session")
                    : null
            }).ToList();
            var warnings = profiles.GetAll()
                .Select(p => (p.Name, Missing: FFmpegProfileRequirements.Missing(p, caps)))
                .Where(x => x.Missing.Count > 0)
                .ToDictionary(x => x.Name, x => x.Missing);

            return Results.Json(new { found = true, version = caps.Version, buildNote = caps.BuildNote, chips, profileWarnings = warnings });
        });

        // An ffmpeg already on this machine (PATH or a usual folder) - offered before any download.
        app.MapGet("/api/ffmpeg/find", () =>
        {
            var found = FFmpegLocator.Find();
            return Results.Json(found is { } f ? new { found = true, path = f.FFmpeg, probePath = f.FFprobe } : new { found = false, path = (string?)null, probePath = (string?)null });
        });

        app.MapGet("/api/ffmpeg/latest-release", async (IConfigStore configStore, IFFmpegInstaller installer, string? channel) =>
        {
            try
            {
                var selected = ResolveChannel(channel, configStore.Load(AppPaths.GetConfigFilePath()));
                var release = await installer.GetLatestReleaseAsync(selected);
                if (release is null) return Results.Json(new { available = false });

                return Results.Json(new
                {
                    available = true,
                    channel = selected.ToString().ToLowerInvariant(),
                    release.Branch,
                    release.Name,
                    release.AssetName,
                    release.ReleaseUrl,
                    sizeMb = release.SizeBytes / 1024 / 1024,
                    verified = !string.IsNullOrWhiteSpace(release.Sha256)
                });
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(new { available = false, error = $"Could not reach GitHub: {ex.Message}" });
            }
        });

        // The About page's ffmpeg card: which ffmpeg is installed (one quick call - no encoder list, no GPU test).
        app.MapGet("/api/ffmpeg/installed-version", async (IConfigStore configStore, IPathExpander pathExpander, IFFmpegCapabilityProbe probe, CancellationToken ct) =>
        {
            var path = pathExpander.Expand(configStore.Load(AppPaths.GetConfigFilePath()).FFmpeg.Path);
            var found = await probe.ReadVersionAsync(path, ct);
            return Results.Json(new { version = found?.Version, buildNote = found?.BuildNote, installedBuild = FFmpegInstallMarkerFile.Read(path)?.Name });
        });

        // "Check for Updates" on the About page. ffmpeg's own version string is a commit id and BtbN publishes every
        // build under the same file name, so a newer build is recognised from the build Compressarr recorded when it
        // installed ffmpeg (see FFmpegInstallMarkerFile). An ffmpeg Compressarr did not install can't be compared.
        app.MapGet("/api/ffmpeg/update-check", async (IConfigStore configStore, IPathExpander pathExpander, IFFmpegCapabilityProbe probe, IFFmpegInstaller installer, string? channel, CancellationToken ct) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var path = pathExpander.Expand(config.FFmpeg.Path);
            var installed = await probe.ReadVersionAsync(path, ct);
            var marker = FFmpegInstallMarkerFile.Read(path);
            var selected = ResolveChannel(channel, config);

            FFmpegReleaseInfo? latest;
            try
            {
                latest = await installer.GetLatestReleaseAsync(selected);
            }
            catch (HttpRequestException ex)
            {
                return Results.Json(new { status = "error", error = $"Could not reach GitHub: {ex.Message}" });
            }

            if (latest is null) return Results.Json(new { status = "unavailable" });

            return Results.Json(new
            {
                status = FFmpegUpdateCheck.Compare(installed is not null, marker, latest, selected).ToString().ToLowerInvariant(),
                channel = selected.ToString().ToLowerInvariant(),
                installedChannel = marker is null ? null : FFmpegUpdateCheck.ChannelOf(marker.AssetName).ToString().ToLowerInvariant(),
                branch = latest.Branch,
                installedVersion = installed?.Version,
                installedBuild = marker?.Name,
                latestBuild = latest.Name,
                releaseUrl = latest.ReleaseUrl
            });
        });

        app.MapPost("/api/ffmpeg/install", async (IFFmpegInstaller installer, IConfigStore configStore, string? channel, CancellationToken ct) =>
        {
            try
            {
                var selected = ResolveChannel(channel, configStore.Load(AppPaths.GetConfigFilePath()));
                var release = await installer.GetLatestReleaseAsync(selected);
                if (release is null)
                {
                    return Results.BadRequest(new { message = "No downloadable ffmpeg build was found for this platform. Install ffmpeg with your package manager and set its path here." });
                }

                var installDir = Path.Combine(AppPaths.GetToolsDirectory(), "ffmpeg");
                var installedPath = await installer.InstallAsync(release, installDir, null, ct);

                configStore.Update(AppPaths.GetConfigFilePath(), config =>
                {
                    config.FFmpeg.Path = installedPath;
                    config.FFmpeg.ProbePath = Path.Combine(installDir, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
                    config.FFmpeg.Channel = selected; // the channel just installed from is the one selected from now on
                    return true;
                });

                return Results.Json(new { installedPath, name = release.Name, channel = selected.ToString().ToLowerInvariant(), branch = release.Branch });
            }
            catch (InvalidDataException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
            catch (HttpRequestException ex)
            {
                return Results.BadRequest(new { message = $"The download failed: {ex.Message}" });
            }
        });
    }
}
