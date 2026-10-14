using Microsoft.AspNetCore.Http;
using Compressarr.Core.Announcements;
using Compressarr.Core.Config;
using Compressarr.Core.Startup;
using Compressarr.Web.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings", (IConfigStore configStore, IPathExpander pathExpander) =>
        {
            var config = configStore.Load(AppPaths.GetConfigFilePath());
            var issues = SettingsValidator.Validate(config, pathExpander).Select(i => new ValidationIssueDto(i.Field, i.Message)).ToList();
            return Results.Json(ConfigMapping.ToSettingsDto(config, issues));
        });

        app.MapPut("/api/settings", async (SettingsDto dto, IConfigStore configStore, IStartupRegistrationService startupRegistration, IPathExpander pathExpander, IAnnouncementService announcements) =>
        {
            var announcementsWereOn = false;
            var result = configStore.Update(AppPaths.GetConfigFilePath(), config =>
            {
                announcementsWereOn = config.Announcements.Enabled;
                ConfigMapping.ApplySettingsDto(config, dto);
                var issues = SettingsValidator.Validate(config, pathExpander).Select(i => new ValidationIssueDto(i.Field, i.Message)).ToList();
                return ConfigMapping.ToSettingsDto(config, issues);
            });
            startupRegistration.Apply(dto.RunAtLogin);

            // Turning announcements on looks for news now, so the bell can appear as soon as this save returns instead of at the next
            // daily check. Capped, so Save never waits long when GitHub cannot be reached (the check carries on in the background).
            if (!announcementsWereOn && dto.AnnouncementsEnabled == true)
            {
                try { await announcements.CheckNowAsync().WaitAsync(TimeSpan.FromSeconds(4)); }
                catch (Exception) { /* best-effort: the bell just appears at the next check */ }
            }

            return Results.Json(result);
        });
    }
}
