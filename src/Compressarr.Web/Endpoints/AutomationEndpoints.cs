using Compressarr.Core.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

/// <summary>What the Settings page's Automation API card uses to list, make and revoke API keys. Not part of the v1 API itself (it is the web UI's own
/// endpoint, like the rest of <c>/api</c>): a key cannot be used to make more keys.</summary>
public static class AutomationEndpoints
{
    public sealed record CreateKeyRequest(string? Name, string? Scope);

    private static object Describe(ApiKeyInfo k) => new
    {
        id = k.Id,
        name = k.Name,
        scope = ApiKeys.ScopeName(k.Scope),
        prefix = k.Prefix,
        createdAt = k.CreatedAt,
        lastUsedAt = k.LastUsedAt
    };

    public static void MapAutomationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/automation/keys", (IApiKeyStore keys) =>
            Results.Json(new { max = ApiKeys.MaxKeys, keys = keys.List().Select(Describe).ToList() }));

        // The key itself is in this response and nowhere else, ever: only its hash is stored.
        app.MapPost("/api/automation/keys", (CreateKeyRequest request, IApiKeyStore keys) =>
        {
            if (!ApiKeys.TryParseScope(request.Scope, out var scope))
                return Results.BadRequest(new { message = "Choose a scope: read or control." });

            try
            {
                var created = keys.Create(request.Name ?? "", scope);
                return Results.Json(new { key = created.Key, info = Describe(created.Info) });
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { message = ex.Message.Split('\n')[0].Replace(" (Parameter 'name')", "") });
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { message = ex.Message });
            }
        });

        app.MapDelete("/api/automation/keys/{id:long}", (long id, IApiKeyStore keys) =>
            keys.Revoke(id) ? Results.NoContent() : Results.NotFound());
    }
}
