using Compressarr.Core.Api;
using Compressarr.Web.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

/// <summary>The Automation API: <c>/api/v1</c>. Stable, versioned JSON for scripts and Home Assistant, every call behind an API key (see
/// <see cref="ApiKeyFilter"/>). Additive changes only; a breaking change would be <c>/api/v2</c>. The web UI's own endpoints are not part of it.
/// See docs/design-api.md.</summary>
public static class ApiV1Endpoints
{
    public static void MapApiV1Endpoints(this IEndpointRouteBuilder app)
    {
        var v1 = app.MapGroup("/api/v1").AddEndpointFilter<ApiKeyFilter>();

        // Who the key is and which version answered: the first call to try with a new key.
        v1.MapGet("/whoami", (HttpContext http) =>
        {
            var key = (ApiKeyInfo)http.Items[ApiKeyFilter.ItemKey]!;
            return Results.Json(new { name = key.Name, scope = ApiKeys.ScopeName(key.Scope), version = AboutEndpoints.InstalledVersion });
        }).RequireApiScope(ApiKeyScope.Read);
    }
}
