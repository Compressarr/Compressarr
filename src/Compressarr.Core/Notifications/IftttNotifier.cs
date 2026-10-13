namespace Compressarr.Core.Notifications;

/// <summary>IFTTT Maker Webhooks - technically already reachable via the Generic Webhook notifier
/// (its body already includes value1/value2/value3 aliases for this exact reason), but a dedicated
/// entry means the user only has to paste their event name and Webhooks key instead of hand-building
/// the "https://maker.ifttt.com/trigger/{event}/with/key/{key}" URL themselves - same UX reasoning
/// as building dedicated Discord/Slack notifiers instead of leaving those to Generic Webhook too.</summary>
public sealed class IftttNotifier : INotifier
{
    private readonly INotificationHttpClient _http;

    public IftttNotifier(INotificationHttpClient http)
    {
        _http = http;
    }

    public string Type => "ifttt";
    public string DisplayName => "IFTTT";

    public IReadOnlyList<NotifierField> Fields { get; } = new[]
    {
        new NotifierField("eventName", "Event Name", "text", Required: true,
            HelpText: "The event name your IFTTT applet's 'Receive a web request' trigger is listening for.",
            Placeholder: "compressarr_run"),
        new NotifierField("webhooksKey", "Webhooks Key", "text", Required: true, Secret: true,
            HelpText: "Find this at ifttt.com/maker_webhooks under Documentation - it's the string after /use/ in your personal URL.",
            Placeholder: "dV3xxxxxxxxxxxxxxxxx")
    };

    public Task<NotifyResult> SendAsync(IReadOnlyDictionary<string, string> settings, NotificationEvent evt, CancellationToken ct)
    {
        return Post(settings, evt.DisplayTitle, evt.Body, evt.ReportPath, ct);
    }

    public Task<NotifyResult> TestAsync(IReadOnlyDictionary<string, string> settings, CancellationToken ct, string? machineName = null)
    {
        return Post(settings, MachineLabel.Prefix("Compressarr test notification", machineName), "This is a test notification from Compressarr.", null, ct);
    }

    private async Task<NotifyResult> Post(IReadOnlyDictionary<string, string> settings, string value1, string value2, string? value3, CancellationToken ct)
    {
        settings.TryGetValue("eventName", out var eventName);
        settings.TryGetValue("webhooksKey", out var webhooksKey);
        if (string.IsNullOrWhiteSpace(eventName) || string.IsNullOrWhiteSpace(webhooksKey))
        {
            return new NotifyResult(false, "Event Name and Webhooks Key are both required.");
        }

        var destination = new Uri($"https://maker.ifttt.com/trigger/{eventName}/with/key/{webhooksKey}");

        try
        {
            using var response = await _http.PostJsonAsync(destination, new { value1, value2, value3 }, headers: null, ct);
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
