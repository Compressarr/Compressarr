using System.Text.Json;
using System.Text.Json.Serialization;
using Compressarr.Core.Config;
using Compressarr.Core.Notifications;

namespace Compressarr.Web.Secrets;

/// <summary>Which settings hold a secret (a key, a token, a webhook address) and what the web endpoints and exports do with them. The browser is never sent a stored secret: a saved one
/// comes back as <see cref="Mask"/> (the box shows dots), and sending the mask back on a save means "unchanged". Anything else is what the user typed: a new value, or an empty box, which
/// removes the secret. See docs/design-login.md.</summary>
public static class SecretFields
{
    /// <summary>What a saved secret looks like to the page. A real key is never this.</summary>
    public const string Mask = "********";

    private static readonly JsonSerializerOptions CloneOptions = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>For each notifier type, the setting keys its fields mark <c>Secret</c> (webhook addresses, tokens, user keys).</summary>
    public static Dictionary<string, HashSet<string>> ChannelKeys(IEnumerable<INotifier> notifiers) => ConfigSecrets.ChannelKeys(notifiers);

    public static HashSet<string> KeysFor(Dictionary<string, HashSet<string>> channelKeys, string type) => ConfigSecrets.KeysFor(channelKeys, type);

    /// <summary>What the page is shown for a stored secret: the mask when there is one, an empty box when there is not.</summary>
    public static string Show(string? stored) => string.IsNullOrEmpty(stored) ? "" : Mask;

    /// <summary>True when the page sent no new value: nothing at all (an older page), or the mask it was shown.</summary>
    public static bool IsUnchanged(string? typed) => typed is null || typed == Mask;

    /// <summary>What a save does with a secret box: unchanged keeps the stored value, anything else is the new value (trimmed) - an empty box removes it.</summary>
    public static string Resolve(string? typed, string existing) => IsUnchanged(typed) ? existing : typed!.Trim();

    // ---- test buttons -----------------------------------------------------------------------------------------------------------------

    /// <summary>The key a Sonarr/Radarr test uses: what was typed, or - when the box still shows the mask - the saved key, whatever address is typed (a machine that got a new address does
    /// not need its key retyped to test it). An emptied box has no key. Null (with a message) when there is no key to use.</summary>
    public static (string? Key, string? Problem) ArrTestKey(string? typedKey, string? service, CompressarrConfig config)
    {
        if (typedKey != Mask) return string.IsNullOrWhiteSpace(typedKey) ? (null, "URL and API key are both required.") : (typedKey.Trim(), null);

        var saved = service?.ToLowerInvariant() switch { "sonarr" => config.Arrs.Sonarr, "radarr" => config.Arrs.Radarr, _ => null };
        return saved is null || string.IsNullOrEmpty(saved.ApiKey) ? (null, "URL and API key are both required.") : (saved.ApiKey, null);
    }

    /// <summary>The key a poster test uses: what was typed, or the saved key of that source when the box shows the mask. An emptied box is tested as empty.</summary>
    public static string PosterTestKey(string? typedKey, string savedKey) =>
        typedKey == Mask ? savedKey : (typedKey ?? "").Trim();

    /// <summary>The settings a channel's Test button sends, with each secret box that shows the mask filled in from the saved channel, whatever else was changed in the form. An emptied box stays empty.</summary>
    public static Dictionary<string, string> ChannelTestSettings(
        string type, Dictionary<string, string> typed, string? channelId, CompressarrConfig config, Dictionary<string, HashSet<string>> channelKeys)
    {
        var secretKeys = KeysFor(channelKeys, type);
        var settings = new Dictionary<string, string>(typed ?? new Dictionary<string, string>());
        var saved = config.Notifications.Channels.FirstOrDefault(c => c.Id == channelId && string.Equals(c.Type, type, StringComparison.OrdinalIgnoreCase));

        foreach (var key in secretKeys)
        {
            var now = settings.TryGetValue(key, out var value) ? value : null;
            if (now != Mask) continue;

            if (saved is not null && saved.Settings.TryGetValue(key, out var kept) && !string.IsNullOrEmpty(kept)) settings[key] = kept;
            else settings.Remove(key);   // nothing saved to borrow (and the mask is never sent on as a value): the notifier reports the missing field itself
        }

        return settings;
    }

    // ---- export and import ------------------------------------------------------------------------------------------------------------

    /// <summary>A copy of the settings with every secret emptied, for an export people may share.</summary>
    public static CompressarrConfig Redacted(CompressarrConfig config, Dictionary<string, HashSet<string>> channelKeys)
    {
        var copy = JsonSerializer.Deserialize<CompressarrConfig>(JsonSerializer.Serialize(config, CloneOptions), CloneOptions)!;
        copy.Arrs.Sonarr.ApiKey = "";
        copy.Arrs.Radarr.ApiKey = "";
        copy.Posters.TmdbApiKey = "";
        copy.Posters.OmdbApiKey = "";
        foreach (var channel in copy.Notifications.Channels)
        {
            foreach (var key in KeysFor(channelKeys, channel.Type)) if (channel.Settings.ContainsKey(key)) channel.Settings[key] = "";
        }

        return copy;
    }

    /// <summary>For an import: a secret that is blank in the file keeps what this install already has (a channel is matched by its id), so importing a redacted export never wipes a key.</summary>
    public static void KeepExisting(CompressarrConfig imported, CompressarrConfig current, Dictionary<string, HashSet<string>> channelKeys)
    {
        if (string.IsNullOrEmpty(imported.Arrs.Sonarr.ApiKey)) imported.Arrs.Sonarr.ApiKey = current.Arrs.Sonarr.ApiKey;
        if (string.IsNullOrEmpty(imported.Arrs.Radarr.ApiKey)) imported.Arrs.Radarr.ApiKey = current.Arrs.Radarr.ApiKey;
        if (string.IsNullOrEmpty(imported.Posters.TmdbApiKey)) imported.Posters.TmdbApiKey = current.Posters.TmdbApiKey;
        if (string.IsNullOrEmpty(imported.Posters.OmdbApiKey)) imported.Posters.OmdbApiKey = current.Posters.OmdbApiKey;

        foreach (var channel in imported.Notifications.Channels)
        {
            var existing = current.Notifications.Channels.FirstOrDefault(c => c.Id == channel.Id && string.Equals(c.Type, channel.Type, StringComparison.OrdinalIgnoreCase));
            if (existing is null) continue;

            foreach (var key in KeysFor(channelKeys, channel.Type))
            {
                var blank = !channel.Settings.TryGetValue(key, out var value) || string.IsNullOrEmpty(value);
                if (blank && existing.Settings.TryGetValue(key, out var kept) && !string.IsNullOrEmpty(kept)) channel.Settings[key] = kept;
            }
        }
    }
}
