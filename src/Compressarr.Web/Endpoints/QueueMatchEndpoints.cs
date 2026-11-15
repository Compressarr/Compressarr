using System.Text.RegularExpressions;
using Compressarr.Core.Config;
using Compressarr.Core.Renaming;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

/// <summary>The queue card's Match / Fix match / Restore (3.2): pick the right film or show on TMDB for a waiting file and rename it to match. The page names a file and a TMDB id, never a title;
/// the name comes from TMDB. The file being encoded is refused. The candidates' pictures come through here too, so the browser only ever talks to Compressarr.</summary>
public static partial class QueueMatchEndpoints
{
    public sealed record CandidatesRequest(string LaneId, string FullName, string? Query = null, int? Year = null, string? Kind = null);
    public sealed record ChoiceRequest(string LaneId, string FullName, int TmdbId, string Kind, int? Season = null, List<int>? Episodes = null, bool ApplyToOthers = false);
    public sealed record FileRequest(string LaneId, string FullName);

    [GeneratedRegex(@"^/[A-Za-z0-9_-]{5,64}\.(jpg|jpeg|png)$")]
    private static partial Regex SafePosterPath();

    private static readonly HashSet<string> PosterSizes = new(StringComparer.Ordinal) { "w92", "w154" };
    private const int MaxPosterBytes = 2 * 1024 * 1024;

    public static void MapQueueMatchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/run/queue/match/candidates", async (CandidatesRequest request, IConfigStore configStore, ITmdbMatchService match, CurrentRunStateService runState, CancellationToken cancellationToken) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            bool? tv = request.Kind is null ? null : string.Equals(request.Kind, "tv", StringComparison.OrdinalIgnoreCase);
            var result = await match.CandidatesAsync(config, request.LaneId, request.FullName, request.Query, request.Year, tv, runState.GetSnapshot().FileFullName, cancellationToken);
            return Results.Json(new
            {
                success = result.Success,
                message = result.Message,
                suggestion = result.Suggestion,
                candidates = result.Candidates?.Select(c => new { id = c.Id, isTv = c.IsTv, title = c.Title, originalTitle = c.OriginalTitle, year = c.Year, overview = Shorten(c.Overview), posterPath = c.PosterPath, countries = c.OriginCountries }),
                currentTmdbId = result.CurrentTmdbId,
                originalName = result.OriginalName,
                isFix = result.IsFix,
                otherFiles = result.OtherFiles
            });
        });

        app.MapPost("/api/run/queue/match/apply", async (ChoiceRequest request, IConfigStore configStore, ITmdbMatchService match, CurrentRunStateService runState, CancellationToken cancellationToken) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var isTv = string.Equals(request.Kind, "tv", StringComparison.OrdinalIgnoreCase);
            var result = await match.ApplyAsync(config, new MatchApplyRequest(request.LaneId, request.FullName, request.TmdbId, isTv, request.Season, request.Episodes, request.ApplyToOthers), runState.GetSnapshot().FileFullName, cancellationToken);
            return Answer(result);
        });

        app.MapPost("/api/run/queue/match/restore", (FileRequest request, IConfigStore configStore, ITmdbMatchService match, CurrentRunStateService runState) =>
            Answer(match.Restore(configStore.Load(AppPaths.GetConfigFilePath()), request.LaneId, request.FullName, runState.GetSnapshot().FileFullName)));

        app.MapPost("/api/run/queue/match/leave", (FileRequest request, IConfigStore configStore, ITmdbMatchService match, CurrentRunStateService runState) =>
            Answer(match.LeaveAsIs(configStore.Load(AppPaths.GetConfigFilePath()), request.LaneId, request.FullName, runState.GetSnapshot().FileFullName)));

        // A candidate's picture, fetched from TMDB's image server on the page's behalf. Only a plain poster path at one of two small sizes is ever fetched, whatever the page asks for.
        app.MapGet("/api/rename/poster", async (string? path, string? size, HttpContext http, IHttpClientFactory httpFactory, CancellationToken cancellationToken) =>
        {
            if (path is null || !SafePosterPath().IsMatch(path) || size is null || !PosterSizes.Contains(size)) return Results.BadRequest();

            try
            {
                using var client = httpFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Compressarr");
                using var response = await client.GetAsync($"https://image.tmdb.org/t/p/{size}{path}", cancellationToken);
                if (!response.IsSuccessStatusCode) return Results.NotFound();

                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length == 0 || bytes.Length > MaxPosterBytes) return Results.NotFound();

                http.Response.Headers.CacheControl = "private, max-age=86400";
                return Results.File(bytes, path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                return Results.NotFound();
            }
        });
    }

    private static IResult Answer(MatchApplyResult result) =>
        Results.Json(new { success = result.Success, message = result.Message, newName = result.NewName, othersRenamed = result.OthersRenamed, problems = result.Problems });

    private static string? Shorten(string? text) => text is { Length: > 240 } ? text[..237].TrimEnd() + "..." : text;
}
