using Microsoft.AspNetCore.Http;
using Compressarr.Core.Config;
using Compressarr.Core.Dependencies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

public static class HandBrakeEndpoints
{
    public static void MapHandBrakeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/handbrake/status", (IConfigStore configStore, IPathExpander pathExpander) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var exists = pathExpander.PathExists(config.HandBrake.CliPath);
            return Results.Json(new { exists });
        });

        app.MapGet("/api/handbrake/installed-version", async (IConfigStore configStore, IPathExpander pathExpander, IHandBrakeInstaller installer) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var cliPath = pathExpander.Expand(config.HandBrake.CliPath);
            var version = await installer.GetInstalledVersionAsync(cliPath);
            return Results.Json(new { version });
        });

        app.MapGet("/api/handbrake/latest-release", async (IHandBrakeInstaller installer) =>
        {
            HandBrakeReleaseInfo? release;
            try
            {
                release = await installer.GetLatestReleaseAsync();
            }
            catch (Exception ex) when (Compressarr.Core.Net.NetworkProblem.IsNetworkFailure(ex))
            {
                // Offline (or GitHub unreachable): answer with a message, never an empty server error the page cannot read.
                return Results.Json(new { available = false, error = Compressarr.Core.Net.NetworkProblem.Describe(ex) });
            }

            if (release is null) return Results.Json(new { available = false });

            return Results.Json(new
            {
                available = true,
                release.Version,
                release.AssetName,
                release.ReleaseUrl,
                sizeMb = release.SizeBytes / 1024 / 1024
            });
        });

        app.MapPost("/api/handbrake/install", async (IHandBrakeInstaller installer, IConfigStore configStore) =>
        {
            try
            {
            var release = await installer.GetLatestReleaseAsync();
            if (release is null)
            {
                return Results.BadRequest(new { message = "No downloadable HandBrakeCLI build was found for this platform. On Linux, install it via your distro's package manager or Flatpak." });
            }

            var installDir = Path.Combine(AppPaths.GetToolsDirectory(), "HandBrakeCLI");
            var installedPath = await installer.InstallAsync(release, installDir);

            configStore.Update(AppPaths.GetConfigFilePath(), config =>
            {
                config.HandBrake.CliPath = installedPath;
                return true;
            });

            return Results.Json(new { installedPath, version = release.Version });
            }
            catch (Exception ex) when (Compressarr.Core.Net.NetworkProblem.IsNetworkFailure(ex))
            {
                return Results.BadRequest(new { message = $"The HandBrakeCLI download failed. {Compressarr.Core.Net.NetworkProblem.Describe(ex)}" });
            }
        });
    }
}
