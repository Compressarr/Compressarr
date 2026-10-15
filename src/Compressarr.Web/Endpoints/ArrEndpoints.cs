using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Compressarr.Core.Arr;
using Compressarr.Web.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

public static class ArrEndpoints
{
    public static void MapArrEndpoints(this IEndpointRouteBuilder app)
    {
        // Tests whatever URL/API key is currently typed into the form, not necessarily what's
        // saved - lets the user confirm it works before hitting Save, matching how Check/Install
        // and Install/Merge Presets already behave on this page.
        app.MapPost("/api/arr/test", async (ArrServiceDto dto, IArrClient client) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Url) || string.IsNullOrWhiteSpace(dto.ApiKey))
            {
                return Results.Json(new { success = false, message = "URL and API key are both required." });
            }

            try
            {
                var status = await client.GetAsync(dto.Url, dto.ApiKey, "/api/v3/system/status");
                var version = (status as JsonObject)?["version"]?.GetValue<string>();
                return Results.Json(new
                {
                    success = true,
                    message = version is not null ? $"Connected - version {version}." : "Connected."
                });
            }
            catch (Exception ex)
            {
                // Wrong API key, unreachable host, wrong port, TLS mismatch, etc. all land here -
                // surfacing ex.Message (HttpClient/HttpRequestException already phrase these
                // reasonably, e.g. "401 Unauthorized" or "No connection could be made") rather than
                // a generic "failed" is exactly the point of a test-connection button.
                return Results.Json(new { success = false, message = $"Connection failed: {ex.Message}" });
            }
        });

        // Radarr's "Delete empty folders" (see ArrMediaManagement): what the Radarr column's note and button on Settings use. Like the test button,
        // they act on the URL / API key typed into the form, saved or not. Nothing here runs by itself - the change is made only when the button is pressed.
        app.MapPost("/api/arr/radarr/delete-empty-folders/check", async (ArrServiceDto dto, IArrClient client) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Url) || string.IsNullOrWhiteSpace(dto.ApiKey))
            {
                return Results.Json(new { success = false, message = "URL and API key are both required." });
            }

            try
            {
                var on = await ArrMediaManagement.GetDeleteEmptyFoldersAsync(client, dto.Url, dto.ApiKey);
                return Results.Json(new { success = true, deleteEmptyFolders = on, message = on switch { true => "\"Delete empty folders\" is on in Radarr.", false => "\"Delete empty folders\" is off in Radarr.", _ => "This version of Radarr does not report that setting." } });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"Couldn't read Radarr's settings: {ex.Message}" });
            }
        });

        app.MapPost("/api/arr/radarr/delete-empty-folders/disable", async (ArrServiceDto dto, IArrClient client) =>
        {
            if (string.IsNullOrWhiteSpace(dto.Url) || string.IsNullOrWhiteSpace(dto.ApiKey))
            {
                return Results.Json(new { success = false, message = "URL and API key are both required." });
            }

            try
            {
                var changed = await ArrMediaManagement.TurnOffDeleteEmptyFoldersAsync(client, dto.Url, dto.ApiKey);
                return Results.Json(new
                {
                    success = true,
                    deleteEmptyFolders = false,
                    message = changed ? "Turned off \"Delete empty folders\" in Radarr." : "\"Delete empty folders\" was already off in Radarr."
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { success = false, message = $"Couldn't change it in Radarr: {ex.Message}" });
            }
        });
    }
}
