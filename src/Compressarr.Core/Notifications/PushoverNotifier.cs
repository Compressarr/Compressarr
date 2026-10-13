namespace Compressarr.Core.Notifications;

/// <summary>Pushover - form-encoded POST per their long-standing documented API
/// (token/user/title/message/priority), not their newer JSON variant, since form-encoding is the
/// guaranteed-stable option every client integration example uses.</summary>
public sealed class PushoverNotifier : INotifier
{
    private static readonly Uri Destination = new("https://api.pushover.net/1/messages.json");

    private readonly INotificationHttpClient _http;

    public PushoverNotifier(INotificationHttpClient http)
    {
        _http = http;
    }

    public string Type => "pushover";
    public string DisplayName => "Pushover";

    public IReadOnlyList<NotifierField> Fields { get; } = new[]
    {
        new NotifierField("appToken", "Application API Token", "text", Required: true, Secret: true,
            HelpText: "Create an Application at pushover.net/apps/build to get this token.",
            Placeholder: "azGDORePK8gMaC0QOYAMyEEuzJnyUi"),
        new NotifierField("userKey", "User Key", "text", Required: true, Secret: true,
            HelpText: "Your personal 30-character User Key, shown on your Pushover dashboard.",
            Placeholder: "uQiRzpo4DXghDmr9QzzfQu27cmVRsG")
    };

    public Task<NotifyResult> SendAsync(IReadOnlyDictionary<string, string> settings, NotificationEvent evt, CancellationToken ct)
    {
        var summary = $"Files: {evt.TotalFiles} | Saved: {evt.SavedGb:0.##} GB | Duration: {NotificationFormatting.FormatDuration(evt.Duration)}";
        var priority = evt.Outcome == NotificationOutcome.Error ? "1" : "0";
        return Post(settings, evt.DisplayTitle, $"{evt.Body}\n{summary}", priority, ct);
    }

    public Task<NotifyResult> TestAsync(IReadOnlyDictionary<string, string> settings, CancellationToken ct, string? machineName = null)
    {
        return Post(settings, MachineLabel.Prefix("Compressarr test notification", machineName), "This is a test notification from Compressarr.", "0", ct);
    }

    private async Task<NotifyResult> Post(IReadOnlyDictionary<string, string> settings, string title, string message, string priority, CancellationToken ct)
    {
        settings.TryGetValue("appToken", out var appToken);
        settings.TryGetValue("userKey", out var userKey);
        if (string.IsNullOrWhiteSpace(appToken) || string.IsNullOrWhiteSpace(userKey))
        {
            return new NotifyResult(false, "Application API Token and User Key are both required.");
        }

        var fields = new Dictionary<string, string>
        {
            ["token"] = appToken,
            ["user"] = userKey,
            ["title"] = title,
            ["message"] = message,
            ["priority"] = priority
        };

        try
        {
            using var response = await _http.PostFormAsync(Destination, fields, ct);
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
