namespace Compressarr.Core.Notifications;

/// <summary>Slack incoming webhook - just a URL, same as Discord. Uses the plain "text" field
/// with Slack's default mrkdwn formatting (*bold*) rather than Block Kit, since every incoming
/// webhook accepts that with no extra setup, whereas Block Kit's exact shape can vary by how the
/// webhook/app was configured.</summary>
public sealed class SlackNotifier : INotifier
{
    private readonly INotificationHttpClient _http;

    public SlackNotifier(INotificationHttpClient http)
    {
        _http = http;
    }

    public string Type => "slack";
    public string DisplayName => "Slack";

    public IReadOnlyList<NotifierField> Fields { get; } = new[]
    {
        new NotifierField("url", "Webhook URL", "text", Required: true,
            HelpText: "Create an Incoming Webhook for your Slack workspace at api.slack.com/apps and paste its URL here.",
            Placeholder: "https://hooks.slack.com/services/...")
    };

    public Task<NotifyResult> SendAsync(IReadOnlyDictionary<string, string> settings, NotificationEvent evt, CancellationToken ct)
    {
        var icon = evt.Outcome switch
        {
            NotificationOutcome.Error => ":red_circle:",
            NotificationOutcome.Warning => ":warning:",
            _ => ":white_check_mark:"
        };
        var summary = $"Files: {evt.TotalFiles} | Saved: {evt.SavedGb:0.##} GB | Duration: {NotificationFormatting.FormatDuration(evt.Duration)}";
        var text = $"{icon} *{Escape(evt.DisplayTitle)}*\n{Escape(evt.Body)}\n{summary}";
        return Post(settings, text, ct);
    }

    public Task<NotifyResult> TestAsync(IReadOnlyDictionary<string, string> settings, CancellationToken ct, string? machineName = null)
    {
        var text = $":white_check_mark: *{Escape(MachineLabel.Prefix("Compressarr test notification", machineName))}*\nThis is a test notification from Compressarr.";
        return Post(settings, text, ct);
    }

    private async Task<NotifyResult> Post(IReadOnlyDictionary<string, string> settings, string text, CancellationToken ct)
    {
        settings.TryGetValue("url", out var url);
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var destination))
        {
            return new NotifyResult(false, "A valid URL is required.");
        }

        try
        {
            using var response = await _http.PostJsonAsync(destination, new { text }, headers: null, ct);
            return response.IsSuccessStatusCode
                ? new NotifyResult(true, $"Sent ({(int)response.StatusCode}).")
                : new NotifyResult(false, $"{(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return new NotifyResult(false, $"Failed: {ex.Message}");
        }
    }

    // Slack's mrkdwn treats &, <, > specially - escape them so a filename/report path containing
    // one doesn't get silently mangled or mis-rendered.
    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
