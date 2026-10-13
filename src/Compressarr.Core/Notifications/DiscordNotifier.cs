namespace Compressarr.Core.Notifications;

/// <summary>Discord incoming webhook - just a URL, no auth token needed (the URL itself is the
/// secret, same as Home Assistant's own webhook design). Posts a single embed rather than plain
/// text so the outcome reads at a glance via its color strip, matching how Discord notifications
/// from other tools (CI systems, monitoring bots) typically look.</summary>
public sealed class DiscordNotifier : INotifier
{
    // Discord embed colors are a plain decimal int, not a hex string - standard Discord palette.
    private const int ColorSuccess = 0x2ECC71;
    private const int ColorWarning = 0xF1C40F;
    private const int ColorError = 0xE74C3C;

    private readonly INotificationHttpClient _http;

    public DiscordNotifier(INotificationHttpClient http)
    {
        _http = http;
    }

    public string Type => "discord";
    public string DisplayName => "Discord";

    public IReadOnlyList<NotifierField> Fields { get; } = new[]
    {
        new NotifierField("url", "Webhook URL", "text", Required: true,
            HelpText: "In Discord, go to a channel's Edit Channel > Integrations > Webhooks, create one, and paste its URL here.",
            Placeholder: "https://discord.com/api/webhooks/...")
    };

    public Task<NotifyResult> SendAsync(IReadOnlyDictionary<string, string> settings, NotificationEvent evt, CancellationToken ct)
    {
        var color = evt.Outcome switch
        {
            NotificationOutcome.Error => ColorError,
            NotificationOutcome.Warning => ColorWarning,
            _ => ColorSuccess
        };
        var payload = BuildPayload(evt.DisplayTitle, evt.Body, color, evt.TotalFiles, evt.SavedGb, evt.Duration);
        return Post(settings, payload, ct);
    }

    public Task<NotifyResult> TestAsync(IReadOnlyDictionary<string, string> settings, CancellationToken ct, string? machineName = null)
    {
        var payload = BuildPayload(MachineLabel.Prefix("Compressarr test notification", machineName), "This is a test notification from Compressarr.", ColorSuccess, null, null, null);
        return Post(settings, payload, ct);
    }

    private async Task<NotifyResult> Post(IReadOnlyDictionary<string, string> settings, object payload, CancellationToken ct)
    {
        settings.TryGetValue("url", out var url);
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var destination))
        {
            return new NotifyResult(false, "A valid URL is required.");
        }

        try
        {
            using var response = await _http.PostJsonAsync(destination, payload, headers: null, ct);
            return response.IsSuccessStatusCode
                ? new NotifyResult(true, $"Sent ({(int)response.StatusCode}).")
                : new NotifyResult(false, $"{(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return new NotifyResult(false, $"Failed: {ex.Message}");
        }
    }

    private static object BuildPayload(string title, string message, int color, int? totalFiles, double? savedGb, TimeSpan? duration)
    {
        var fields = new List<object>();
        if (totalFiles is not null) fields.Add(new { name = "Files", value = totalFiles.ToString(), inline = true });
        if (savedGb is not null) fields.Add(new { name = "Saved", value = $"{savedGb:0.##} GB", inline = true });
        if (duration is not null) fields.Add(new { name = "Duration", value = NotificationFormatting.FormatDuration(duration.Value), inline = true });

        return new
        {
            embeds = new[]
            {
                new
                {
                    title,
                    description = message,
                    color,
                    fields,
                    timestamp = DateTimeOffset.UtcNow.ToString("o")
                }
            }
        };
    }
}
