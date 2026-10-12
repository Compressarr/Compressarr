using Compressarr.Core.Announcements;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

/// <summary>The announcements bell: what is on the list and which are unread, and marking them read. Both read only what is stored on this
/// machine - nothing here ever goes to the network (the background service does that, and only while the setting is on).</summary>
public static class AnnouncementEndpoints
{
    public sealed record ReadRequest(List<string>? Ids, bool All);

    public static void MapAnnouncementEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/announcements", (IAnnouncementService announcements) => Results.Json(announcements.GetView()));

        // { all: true } marks everything on the list read (opening the panel); { ids: [...] } marks just those.
        app.MapPost("/api/announcements/read", (ReadRequest request, IAnnouncementService announcements) =>
        {
            if (!request.All && (request.Ids is null || request.Ids.Count == 0)) return Results.BadRequest(new { message = "Say which announcements to mark read." });
            announcements.MarkRead(request.All ? null : request.Ids);
            return Results.Json(announcements.GetView());
        });
    }
}
