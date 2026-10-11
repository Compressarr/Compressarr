using Compressarr.Core.Catalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

/// <summary>The catalog of extra profiles (Profiles page > Import > From Catalog, and the About page's card). Nothing here runs by itself:
/// a catalog is read only when the user opens it or presses Check, and a profile is installed only when the user ticks it.</summary>
public static class CatalogEndpoints
{
    public sealed record InstallRequest(List<InstallItem>? Items);

    public sealed record InstallItem(string Family, int Version, string? ImportAs, string? Engine = null);

    public static void MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        // Who the profiles would come from, without fetching anything: the dialog shows "Importing from:" before it loads the list.
        app.MapGet("/api/catalog/source", (ICatalogService catalog) =>
            Results.Json(new { name = catalog.Source.Name, url = catalog.Source.Url, kind = catalog.Source.Kind.ToString().ToLowerInvariant() }));

        app.MapGet("/api/catalog", async (ICatalogService catalog, CancellationToken ct) =>
        {
            try
            {
                return Results.Json(new { ok = true, catalog = await catalog.ListAsync(ct) });
            }
            catch (CatalogException ex)
            {
                return Results.Json(new { ok = false, error = ex.Message, source = new { name = catalog.Source.Name, url = catalog.Source.Url } });
            }
        });

        app.MapPost("/api/catalog/install", async (InstallRequest request, ICatalogService catalog, Compressarr.Core.Presets.IHandBrakeProfileStore handBrake, CancellationToken ct) =>
        {
            var items = request.Items?.Where(i => !string.IsNullOrWhiteSpace(i.Family)).ToList() ?? new List<InstallItem>();
            if (items.Count == 0) return Results.BadRequest(new { message = "Tick at least one profile." });

            try
            {
                var results = await catalog.InstallAsync(items.Select(i => new CatalogInstallRequest(i.Family, i.Version, i.ImportAs, i.Engine)).ToList(), ct);
                handBrake.EnsureActiveFile(); // HandBrake is handed the new profiles with the next encode; this just keeps the file current
                return Results.Json(new { results });
            }
            catch (CatalogException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });
    }
}
