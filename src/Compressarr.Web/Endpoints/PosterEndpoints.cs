using Compressarr.Core.Config;
using Compressarr.Core.Posters;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

/// <summary>The queue's poster pictures (Settings > Show posters on queue). The browser only ever talks to Compressarr: the image comes from
/// the local cache, which downloads it from TMDb the first time. Nothing here does anything while the setting is off.</summary>
public static class PosterEndpoints
{
    public sealed record TestRequest(string? ApiKey);

    public static void MapPosterEndpoints(this IEndpointRouteBuilder app)
    {
        // The picture for a poster id at a size (w92, w154 or w185). A poster never changes under its id, so the browser may keep it.
        app.MapGet("/api/posters/{id}", async (string id, string? size, HttpContext http, IPosterService posters, IConfigStore configStore, CancellationToken cancellationToken) =>
        {
            var settings = configStore.Load(AppPaths.GetConfigFilePath()).Posters;
            var path = await posters.GetImagePathAsync(settings, id, size ?? "w154", cancellationToken);
            if (path is null) return Results.NotFound();

            http.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(path, path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
        });

        // Checks the key typed into the card (saved or not), like the other Test buttons.
        app.MapPost("/api/posters/test", async (TestRequest request, IPosterService posters, CancellationToken cancellationToken) =>
        {
            var result = await posters.TestKeyAsync(request.ApiKey ?? "", cancellationToken);
            return Results.Json(new { success = result.Success, message = result.Message });
        });

        app.MapGet("/api/posters/info", (IPosterService posters) =>
        {
            var info = posters.GetCacheInfo();
            return Results.Json(new { posters = info.Posters, bytes = info.Bytes, problem = info.Problem });
        });

        app.MapPost("/api/posters/clear", (IPosterService posters) =>
        {
            var removed = posters.ClearCache();
            return Results.Json(new { removed });
        });
    }
}
