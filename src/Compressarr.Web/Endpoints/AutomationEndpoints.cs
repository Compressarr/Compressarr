using Compressarr.Core.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

/// <summary>What the Settings page uses to show, copy and regenerate the Automation API's two fixed keys. Not part of the v1 API itself (it is the web UI's own endpoint,
/// like the rest of <c>/api</c>): a key cannot be used to read or replace the keys.</summary>
public static class AutomationEndpoints
{
    private static object Describe(IApiKeyStore keys, ApiKeyScope scope) => new
    {
        scope = ApiKeys.ScopeName(scope),
        name = ApiKeys.DisplayName(scope),
        key = keys.GetKey(scope)
    };

    public static void MapAutomationEndpoints(this IEndpointRouteBuilder app)
    {
        // Both keys, as Sonarr's settings page shows its one: always visible to whoever may open Settings.
        app.MapGet("/api/automation/keys", (IApiKeyStore keys) =>
        {
            try
            {
                return Results.Json(new { keys = new[] { Describe(keys, ApiKeyScope.Control), Describe(keys, ApiKeyScope.Read) } });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        // A new key of that scope; the old one stops working at once.
        app.MapPost("/api/automation/keys/{scope}/regenerate", (string scope, IApiKeyStore keys) =>
        {
            if (!ApiKeys.TryParseScope(scope, out var parsed)) return Results.NotFound();

            try
            {
                keys.Regenerate(parsed);
                return Results.Json(Describe(keys, parsed));
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { message = ex.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
    }
}
