using Compressarr.Core.Config;
using Compressarr.Core.Queue;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

/// <summary>One priority rule as the page sends and receives it.</summary>
public sealed record PriorityRuleDto(string? Id, string? Match, int? TmdbId, string Priority);

public sealed record PriorityRulesRequest(List<PriorityRuleDto> Rules);

/// <summary>Make a rule from a queue row: the row's file picks the title (or the TMDb id tagged in its name). Priority is "Higher", "Lower", or "Normal" (which removes the rules that match the file).</summary>
public sealed record FilePriorityRequest(string LaneId, string FullName, string Priority);

/// <summary>The queue-priority rules (3.1): which shows go earlier or later in the queue when their files first arrive.</summary>
public static class QueuePriorityEndpoints
{
    private const int MinMatchLength = 2;

    private static PriorityRuleDto ToDto(PriorityRule r) => new(r.Id, r.Match, r.TmdbId, r.Priority.ToString());

    public static void MapQueuePriorityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/queue-priority", (IConfigStore configStore) =>
            Results.Json(new { rules = configStore.Load(AppPaths.GetConfigFilePath()).Priority.Rules.Select(ToDto).ToList() }));

        // The whole list is replaced, so the page can add, edit, reorder (the first matching rule wins) and delete in one save.
        app.MapPut("/api/queue-priority", (PriorityRulesRequest request, IConfigStore configStore) =>
        {
            var rules = new List<PriorityRule>();
            foreach (var dto in request.Rules ?? new())
            {
                var match = (dto.Match ?? "").Trim();
                if (!Enum.TryParse<QueuePriority>(dto.Priority, ignoreCase: true, out var priority))
                    return Results.BadRequest(new { message = "A rule's priority must be Higher or Lower." });
                if (dto.TmdbId is <= 0) return Results.BadRequest(new { message = "A TMDb id is a positive number." });
                if (match.Length < MinMatchLength && dto.TmdbId is null)
                    return Results.BadRequest(new { message = $"A rule needs at least {MinMatchLength} characters of the title, or a TMDb id." });
                rules.Add(new PriorityRule { Id = string.IsNullOrWhiteSpace(dto.Id) ? Guid.NewGuid().ToString("N") : dto.Id!, Match = match, TmdbId = dto.TmdbId, Priority = priority });
            }

            var saved = configStore.Update(AppPaths.GetConfigFilePath(), config =>
            {
                config.Priority.Rules = rules;
                return rules.Select(ToDto).ToList();
            });
            return Results.Json(new { rules = saved });
        });

        // "Apply to files already waiting": re-sorts the queue by tier once. New arrivals are placed by the rules on their own.
        app.MapPost("/api/run/queue/priority/apply", (IConfigStore configStore, IQueueService queue) =>
            Results.Json(new { moved = queue.ApplyPriorityToWaiting(configStore.Load(AppPaths.GetConfigFilePath())) }));

        // From a queue row's menu: "Lower priority for this show" and friends. Creates (or changes) the rule that matches this file's title,
        // or its TMDb id when the file or folder carries one; "Normal" removes whatever rules match the file.
        app.MapPost("/api/run/queue/priority-rule", (FilePriorityRequest request, IConfigStore configStore) =>
        {
            var wantsNormal = string.Equals(request.Priority, "Normal", StringComparison.OrdinalIgnoreCase);
            if (!wantsNormal && !Enum.TryParse<QueuePriority>(request.Priority, ignoreCase: true, out _))
                return Results.BadRequest(new { message = "Priority must be Higher, Lower or Normal." });

            var fileName = Path.GetFileName(request.FullName);
            var title = QueuePriorityRules.TitleOf(fileName);
            var tmdb = QueuePriorityRules.TmdbIdIn(request.FullName);
            if (QueuePriorityRules.Normalize(title).Length < MinMatchLength && tmdb is null)
                return Results.BadRequest(new { message = "Could not work out a title for that file." });

            var outcome = configStore.Update(AppPaths.GetConfigFilePath(), config =>
            {
                var rules = config.Priority.Rules;
                if (wantsNormal)
                {
                    // Every rule that decides this file's tier: those that match the file as it is now.
                    var removed = rules.RemoveAll(r => QueuePriorityRules.RuleMatches(r, request.FullName));
                    return (Removed: removed, Rule: (PriorityRule?)null);
                }

                var priority = Enum.Parse<QueuePriority>(request.Priority, ignoreCase: true);
                var same = rules.FirstOrDefault(r => tmdb is { } id
                    ? r.TmdbId == id && QueuePriorityRules.Normalize(r.Match).Length == 0
                    : r.TmdbId is null && QueuePriorityRules.Normalize(r.Match) == QueuePriorityRules.Normalize(title));
                if (same is not null) { same.Priority = priority; return (Removed: 0, Rule: (PriorityRule?)same); }

                var rule = new PriorityRule { Match = tmdb is null ? title : "", TmdbId = tmdb, Priority = priority };
                rules.Insert(0, rule);      // newest first: the first matching rule wins, so a fresh choice overrides an older broader one
                return (Removed: 0, Rule: (PriorityRule?)rule);
            });
            return Results.Json(new { rule = outcome.Rule is null ? null : ToDto(outcome.Rule), removed = outcome.Removed });
        });
    }
}
