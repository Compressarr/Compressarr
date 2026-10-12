using System.Text.Json.Nodes;

namespace Compressarr.Core.Config;

/// <summary>
/// Whether a file someone is importing or restoring is plausibly a Compressarr config at all. A config file is read leniently
/// (missing settings fall back to defaults, so an older export still loads), which means a JSON file with none of Compressarr's
/// settings in it - an empty <c>{}</c>, or another program's file such as HandBrake's presets.json - would "load" as all defaults
/// and replace the real lanes and settings. Requiring at least one known setting stops that.
/// </summary>
public static class ConfigFileShape
{
    private static readonly HashSet<string> KnownSettings =
        new(typeof(CompressarrConfig).GetProperties().Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

    public static bool LooksLikeAConfig(JsonNode? root) =>
        root is JsonObject obj && obj.Any(p => KnownSettings.Contains(p.Key));
}
