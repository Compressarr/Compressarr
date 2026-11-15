using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Renaming;
using Compressarr.Web.Dtos;
using Compressarr.Web.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

/// <summary>The built-in TMDB renamer's preview (Settings > Rename with TMDB): what each file in the lanes' Input folders would be called, with nothing renamed. It uses the card's own
/// choices (typed, saved or not) and the saved TMDB key, and only ever sends titles and years to TMDB.</summary>
public static class RenameEndpoints
{
    public sealed record PreviewRequest(TmdbRenameDto? Settings, string? ApiKey);

    /// <summary>How many files one preview looks at per lane: a huge folder would otherwise ask TMDB about every one of them.</summary>
    private const int PreviewFilesPerLane = 40;

    public static void MapRenameEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/rename/preview", async (PreviewRequest request, ITmdbRenamer renamer, IConfigStore configStore, IPathExpander pathExpander, CancellationToken cancellationToken) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var key = (SecretFields.PosterTestKey(request.ApiKey, config.Posters.TmdbApiKey) ?? "").Trim();
            if (key.Length == 0)
            {
                return Results.Json(new { success = false, message = "Enter a TMDB API key under Queue Posters first - the renamer uses the same one." });
            }

            var settings = new TmdbRenameSettings
            {
                Enabled = true,
                Language = string.IsNullOrWhiteSpace(request.Settings?.Language) ? config.TmdbRename.Language : request.Settings!.Language.Trim(),
                Numbering = Enum.TryParse<TvNumbering>(request.Settings?.Numbering, ignoreCase: true, out var numbering) ? numbering : config.TmdbRename.Numbering,
                AddMovieTmdbId = request.Settings?.AddMovieTmdbId ?? config.TmdbRename.AddMovieTmdbId
            };

            var lanes = new List<object>();
            foreach (var lane in config.Lanes.Where(l => l.Enabled && !LaneKinds.IsVideos(l)))
            {
                var input = pathExpander.Expand(lane.Input);
                if (string.IsNullOrWhiteSpace(input) || !Directory.Exists(input)) continue;

                var vidTypes = LaneProcessing.ConfigFor(config, lane).Processing.VidTypes;
                var plan = await renamer.PreviewAsync(settings, key, input, vidTypes, PreviewFilesPerLane, cancellationToken);
                var changes = plan.Where(p => p.Kind != RenameKind.AlreadyNamed).ToList();
                lanes.Add(new
                {
                    laneId = lane.Id,
                    laneName = lane.DisplayName,
                    looked = plan.Count,
                    alreadyNamed = plan.Count - changes.Count,
                    items = changes.Select(p => new { file = p.FileName, kind = p.Kind.ToString(), newName = p.NewName, reason = p.Reason })
                });
            }

            return Results.Json(new { success = true, lanes });
        });
    }
}
