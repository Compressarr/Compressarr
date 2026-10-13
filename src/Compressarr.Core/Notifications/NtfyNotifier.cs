namespace Compressarr.Core.Notifications;

/// <summary>ntfy - simple pub/sub push. The POST body is the raw message text (not JSON); title,
/// priority, and tags travel as headers, per ntfy's documented publish API. Server URL is
/// user-configurable rather than fixed, since ntfy is commonly self-hosted - defaults to the
/// public ntfy.sh instance in the UI placeholder only, not hardcoded here.</summary>
public sealed class NtfyNotifier : INotifier
{
    private readonly INotificationHttpClient _http;

    public NtfyNotifier(INotificationHttpClient http)
    {
        _http = http;
    }

    public string Type => "ntfy";
    public string DisplayName => "ntfy";

    public IReadOnlyList<NotifierField> Fields { get; } = new[]
    {
        new NotifierField("server", "Server URL", "text", Required: true,
            HelpText: "The ntfy server to publish to. Defaults to the public ntfy.sh instance - change this if you're self-hosting ntfy.",
            Placeholder: "https://ntfy.sh", DefaultValue: "https://ntfy.sh"),
        new NotifierField("topic", "Topic", "text", Required: true,
            HelpText: "Any string works. Pick something unique and hard to guess - anyone who knows your topic name can subscribe to it too.",
            Placeholder: "compressarr-a1b2c3"),
        new NotifierField("accessToken", "Access Token (optional, for protected topics)", "text", Required: false, Secret: true,
            HelpText: "Only needed if your topic is access-controlled. Leave blank for a public topic on a public server.",
            Placeholder: "tk_...")
    };

    public Task<NotifyResult> SendAsync(IReadOnlyDictionary<string, string> settings, NotificationEvent evt, CancellationToken ct)
    {
        var (priority, tags) = evt.Outcome switch
        {
            NotificationOutcome.Error => ("4", "rotating_light"),
            NotificationOutcome.Warning => ("4", "warning"),
            _ => ("3", "white_check_mark")
        };
        var summary = $"Files: {evt.TotalFiles} | Saved: {evt.SavedGb:0.##} GB | Duration: {NotificationFormatting.FormatDuration(evt.Duration)}";
        var message = $"{evt.Body}\n{summary}";
        return Post(settings, evt.DisplayTitle, message, priority, tags, ct);
    }

    public Task<NotifyResult> TestAsync(IReadOnlyDictionary<string, string> settings, CancellationToken ct, string? machineName = null)
    {
        return Post(settings, MachineLabel.Prefix("Compressarr test notification", machineName), "This is a test notification from Compressarr.", "3", "white_check_mark", ct);
    }

    private async Task<NotifyResult> Post(IReadOnlyDictionary<string, string> settings, string title, string message, string priority, string tags, CancellationToken ct)
    {
        settings.TryGetValue("server", out var server);
        settings.TryGetValue("topic", out var topic);
        var url = $"{(server ?? "").TrimEnd('/')}/{topic}";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var destination))
        {
            return new NotifyResult(false, "A valid Server URL and Topic are both required.");
        }

        var headers = new Dictionary<string, string>
        {
            ["Title"] = title,
            ["Priority"] = priority,
            ["Tags"] = tags
        };
        if (settings.TryGetValue("accessToken", out var token) && !string.IsNullOrWhiteSpace(token))
        {
            headers["Authorization"] = $"Bearer {token}";
        }

        try
        {
            using var response = await _http.PostTextAsync(destination, message, headers, ct);
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
