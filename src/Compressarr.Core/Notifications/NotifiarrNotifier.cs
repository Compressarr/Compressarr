namespace Compressarr.Core.Notifications;

/// <summary>Notifiarr - a Discord-relay service built specifically for the *arr ecosystem
/// (Sonarr/Radarr/etc.), reached via its "Passthrough" integration
/// (https://notifiarr.wiki/pages/integrations/passthrough/). Notifiarr renders the payload as a
/// Discord embed in whichever channel the user's Notifiarr Discord bot is configured to post to -
/// the channel id is Notifiarr's own routing target, not a raw Discord webhook.</summary>
public sealed class NotifiarrNotifier : INotifier
{
    // Same palette as DiscordNotifier, but as a bare 6-digit hex string per Notifiarr's schema
    // rather than a decimal int.
    private const string ColorSuccess = "2ECC71";
    private const string ColorWarning = "F1C40F";
    private const string ColorError = "E74C3C";

    private readonly INotificationHttpClient _http;

    public NotifiarrNotifier(INotificationHttpClient http)
    {
        _http = http;
    }

    public string Type => "notifiarr";
    public string DisplayName => "Notifiarr";

    public IReadOnlyList<NotifierField> Fields { get; } = new[]
    {
        new NotifierField("apiKey", "Notifiarr API Key", "text", Required: true, Secret: true,
            HelpText: "Found on your Notifiarr account page under My Account > API Key.",
            Placeholder: "01234567-89ab-cdef-0123-456789abcdef"),
        new NotifierField("channelId", "Discord Channel ID", "text", Required: true,
            HelpText: "The Discord channel to post to, allowed for your Notifiarr Discord integration. Enable Developer Mode in Discord, then right-click the channel and Copy Channel ID.",
            Placeholder: "123456789012345678")
    };

    public Task<NotifyResult> SendAsync(IReadOnlyDictionary<string, string> settings, NotificationEvent evt, CancellationToken ct)
    {
        var color = evt.Outcome switch
        {
            NotificationOutcome.Error => ColorError,
            NotificationOutcome.Warning => ColorWarning,
            _ => ColorSuccess
        };
        var fields = new List<object>
        {
            new { title = "Files", text = evt.TotalFiles.ToString(), inline = true },
            new { title = "Saved", text = $"{evt.SavedGb:0.##} GB", inline = true },
            new { title = "Duration", text = NotificationFormatting.FormatDuration(evt.Duration), inline = true }
        };
        return Post(settings, evt.DisplayTitle, evt.Body, color, fields, ct);
    }

    public Task<NotifyResult> TestAsync(IReadOnlyDictionary<string, string> settings, CancellationToken ct, string? machineName = null)
    {
        return Post(settings, MachineLabel.Prefix("Compressarr test notification", machineName), "This is a test notification from Compressarr.", ColorSuccess, new List<object>(), ct);
    }

    private async Task<NotifyResult> Post(IReadOnlyDictionary<string, string> settings, string title, string description, string color, List<object> fields, CancellationToken ct)
    {
        settings.TryGetValue("apiKey", out var apiKey);
        settings.TryGetValue("channelId", out var channelIdText);
        if (string.IsNullOrWhiteSpace(apiKey) || !long.TryParse(channelIdText, out var channelId))
        {
            return new NotifyResult(false, "Notifiarr API Key and a numeric Discord Channel ID are both required.");
        }

        var destination = new Uri($"https://notifiarr.com/api/v1/notification/passthrough/{apiKey}");
        var payload = new
        {
            notification = new { update = false, name = "Compressarr", @event = "run-complete" },
            discord = new
            {
                color,
                ping = new { pingUser = 0, pingRole = 0 },
                images = new { thumbnail = "", image = "" },
                text = new { title, icon = "", content = "", description, fields, footer = "" },
                ids = new { channel = channelId }
            }
        };
        var headers = new Dictionary<string, string> { ["Accept"] = "text/plain" };

        try
        {
            using var response = await _http.PostJsonAsync(destination, payload, headers, ct);
            return response.IsSuccessStatusCode
                ? new NotifyResult(true, $"Sent ({(int)response.StatusCode}).")
                : new NotifyResult(false, $"{(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return new NotifyResult(false, $"Failed: {ex.Message}");
        }
    }
}
