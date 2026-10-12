using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Compressarr.Core.Config;
using Compressarr.Core.Updates;

namespace Compressarr.Web.Endpoints;

public static class AboutEndpoints
{
    // The informational version carries any pre-release suffix ("2.2.0-dev"), which the numeric
    // assembly version drops - shown so a dev/pre-release build can be told apart from a release.
    // Build metadata after '+' (the commit hash) is trimmed off.
    private static string InstalledVersionString =>
        typeof(AboutEndpoints).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .Select(a => a.InformationalVersion.Split('+')[0])
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
        ?? FormatVersion(typeof(AboutEndpoints).Assembly.GetName().Version);

    public static void MapAboutEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/about", () => Results.Json(new
        {
            version = InstalledVersionString
        }));

        // The GPL text and the third-party notices, installed next to the program (see Compressarr.Desktop.csproj),
        // for the About page's links. Plain text; 404 when running somewhere they were not copied to.
        app.MapGet("/api/about/license", () => ServeBundledText("LICENSE"));
        app.MapGet("/api/about/notices", () => ServeBundledText("THIRD-PARTY-NOTICES.txt"));

        // The toolbar's ambient indicator (nav.js, on every page load) - purely reads
        // IUpdateCheckService's own background-checked cache (refreshed immediately on app
        // startup, then roughly daily), no network call of its own. Falls back to one inline live
        // check only if the service hasn't completed one yet at all (e.g. this request landed
        // before Start()'s first check finished).
        // A cached result from the other channel is stale the moment the Settings choice changes, so it is checked again.
        app.MapGet("/api/about/update-status", async (IUpdateCheckService updateCheckService, IConfigStore configStore) =>
        {
            var channel = configStore.Load(AppPaths.GetConfigFilePath()).UpdateCheck.Channel.ToString();
            var last = updateCheckService.LastResult;
            return Results.Json(ToDto(last is not null && last.Channel == channel ? last : await updateCheckService.CheckNowAsync()));
        });

        // The About page's explicit "Check for Updates" button - always performs a real,
        // on-demand GitHub check (not the cache above), since that's what clicking a button
        // labeled "Check for Updates" should actually do. Also refreshes IUpdateCheckService's
        // cache as a side effect, so the toolbar indicator picks up the same fresh result too.
        app.MapGet("/api/about/check-update", async (IUpdateCheckService updateCheckService) =>
            Results.Json(ToDto(await updateCheckService.CheckNowAsync())));
    }

    private static IResult ServeBundledText(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        return File.Exists(path) ? Results.File(File.ReadAllBytes(path), "text/plain; charset=utf-8") : Results.NotFound();
    }

    private static string FormatVersion(Version? version) =>
        version is null ? "unknown" : $"{version.Major}.{version.Minor}.{version.Build}";

    private static object ToDto(UpdateCheckResult result) => new
    {
        checkedOk = result.CheckedOk,
        error = result.Error,
        latestVersion = result.LatestVersion,
        releaseUrl = result.ReleaseUrl,
        hasUpdate = result.HasUpdate,
        channel = result.Channel
    };
}
