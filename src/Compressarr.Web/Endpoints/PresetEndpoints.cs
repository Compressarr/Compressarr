using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Compressarr.Web.Endpoints;

public static class PresetEndpoints
{
    public static void MapPresetEndpoints(this IEndpointRouteBuilder app)
    {
        // The names a lane or queued file can pick: Compressarr's own profiles for the encoder asked
        // about (HandBrake unless ?engine=ffmpeg) - built-ins plus the user's. There is no path to give:
        // Compressarr no longer reads HandBrake's presets.json at run time; the Profiles page's Import is
        // how presets get in from it.
        //
        // ?includePassthrough=1 (the Lanes page and the Monitor queue) adds "Passthrough (no re-encode)" at the top: a choice for a lane or a
        // queued file, not a profile - so the profile editors, which read this list for fallback profiles, never ask for it.
        app.MapGet("/api/presets", (string? engine, string? includePassthrough, IEncoderResolver encoders) =>
        {
            var which = string.Equals(engine, "ffmpeg", StringComparison.OrdinalIgnoreCase) ? EncoderEngine.FFmpeg : EncoderEngine.HandBrake;
            var names = encoders.PresetsFor(which).GetPresetNames();
            return Results.Json(includePassthrough is "1" or "true"
                ? new[] { PassthroughPreset.Name }.Concat(names).ToList()
                : names);
        });
    }
}
