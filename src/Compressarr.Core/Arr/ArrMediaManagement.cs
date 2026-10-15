using System.Text.Json.Nodes;

namespace Compressarr.Core.Arr;

/// <summary>
/// Radarr's "Delete empty folders" setting (Settings > Media Management > Folders). With it on, Radarr removes a movie's now-empty folder in the
/// middle of its own rescan and the rescan then fails with "could not find a part of the path", so Compressarr cannot confirm it and defers the
/// source-folder cleanup. These two calls let the Settings page show whether it is on and switch it off - only when the user presses the button,
/// and only that one field: the whole Media Management object is read, the one flag is changed, and the object is written back as it was.
/// </summary>
public static class ArrMediaManagement
{
    private const string Path = "/api/v3/config/mediamanagement";
    private const string Field = "deleteEmptyFolders";

    /// <summary>True or false as Radarr reports it; null when this Radarr does not report the setting.</summary>
    public static async Task<bool?> GetDeleteEmptyFoldersAsync(IArrClient client, string baseUrl, string apiKey, CancellationToken cancellationToken = default)
    {
        var config = await client.GetAsync(baseUrl, apiKey, Path, cancellationToken) as JsonObject;
        return config?[Field] is JsonValue value && value.TryGetValue<bool>(out var on) ? on : null;
    }

    /// <summary>Switches the setting off. Returns true when it was on and is now off, false when it was already off. Throws
    /// InvalidOperationException (with a message fit to show) when Radarr does not report the setting, or accepts the change but still reports it on.</summary>
    public static async Task<bool> TurnOffDeleteEmptyFoldersAsync(IArrClient client, string baseUrl, string apiKey, CancellationToken cancellationToken = default)
    {
        var config = await client.GetAsync(baseUrl, apiKey, Path, cancellationToken) as JsonObject
            ?? throw new InvalidOperationException("Radarr did not return its Media Management settings.");
        if (config[Field] is not JsonValue value || !value.TryGetValue<bool>(out var isOn))
        {
            throw new InvalidOperationException("This version of Radarr does not report that setting, so Compressarr can't change it. Turn it off in Radarr: Settings > Media Management > Folders.");
        }

        if (!isOn) return false;

        config[Field] = false;
        var id = config["id"]?.GetValue<int>() ?? 1;
        await client.PutAsync(baseUrl, apiKey, $"{Path}/{id}", config, cancellationToken);

        if (await GetDeleteEmptyFoldersAsync(client, baseUrl, apiKey, cancellationToken) != false)
        {
            throw new InvalidOperationException("Radarr accepted the change but still reports \"Delete empty folders\" as on. Turn it off in Radarr: Settings > Media Management > Folders.");
        }

        return true;
    }
}
