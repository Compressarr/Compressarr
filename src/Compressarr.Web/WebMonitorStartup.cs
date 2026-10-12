using Compressarr.Core.Logging;
using Compressarr.Web.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Web;

public static class WebMonitorStartup
{
    public static void MapCompressarrEndpoints(this WebApplication app)
    {
        app.MapSettingsEndpoints();
        app.MapScheduleEndpoints();
        app.MapLaneEndpoints();
        app.MapPresetEndpoints();
        app.MapEncoderEndpoints();
        app.MapHandBrakeProfileEndpoints();
        app.MapFFmpegEndpoints();
        app.MapCatalogEndpoints();
        app.MapFFmpegProfileEndpoints();
        app.MapHandBrakeEndpoints();
        app.MapRunEndpoints();
        app.MapHistoryEndpoints();
        app.MapBrowseEndpoints();
        app.MapAboutEndpoints();
        app.MapAnnouncementEndpoints();
        app.MapArrEndpoints();
        app.MapBackupEndpoints();
        app.MapMaintenanceEndpoints();
        app.MapDonateEndpoints();
        app.MapNotificationEndpoints();
        app.MapFileBotEndpoints();
    }

    /// <summary>Starts Kestrel without throwing - a bind failure (port in use, etc.) is logged
    /// and leaves the app fully usable without the web UI, same best-effort posture as
    /// NoOpNotificationService/TrashServiceFactory fallbacks elsewhere in this codebase. Must
    /// never be awaited synchronously from a UI thread - call as fire-and-forget.</summary>
    public static async Task StartAsync(WebApplication app, IRunLogger logger)
    {
        try
        {
            await app.StartAsync();
        }
        catch (Exception ex)
        {
            logger.Log($"Web UI failed to start: {ex.Message}", LogSeverity.Error);
        }
    }
}
