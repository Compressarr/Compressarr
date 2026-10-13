namespace Compressarr.Core.Notifications;

/// <summary>Gotify - a self-hosted push notification server, popular in the same self-hosted
/// crowd as ntfy. Server URL is user-configurable (Gotify has no public hosted instance, it's
/// always self-hosted). The app token travels as the X-Gotify-Key header rather than the ?token=
/// query parameter Gotify also accepts, so it never ends up in a server access log's URL.</summary>
public sealed class GotifyNotifier : INotifier
{
    private readonly INotificationHttpClient _http;

    public GotifyNotifier(INotificationHttpClient http)
    {
        _http = http;
    }

    public string Type => "gotify";
    public string DisplayName => "Gotify";

    public IReadOnlyList<NotifierField> Fields { get; } = new[]
    {
        new NotifierField("server", "Server URL", "text", Required: true,
            HelpText: "The base URL of your self-hosted Gotify instance. Gotify has no public hosted service - this always points at a server you run.",
            Placeholder: "https://gotify.example.com"),
        new NotifierField("appToken", "Application Token", "text", Required: true, Secret: true,
            HelpText: "Create an Application in Gotify's web UI (Apps tab) to get this token.",
            Placeholder: "Aici-XXXXXXXXXXX")
    };

    public Task<NotifyResult> SendAsync(IReadOnlyDictionary<string, string> settings, NotificationEvent evt, CancellationToken ct)
    {
        var priority = evt.Outcome switch
        {
            NotificationOutcome.Error => 8,
            NotificationOutcome.Warning => 7,
            _ => 5
        };
        var summary = $"Files: {evt.TotalFiles} | Saved: {evt.SavedGb:0.##} GB | Duration: {NotificationFormatting.FormatDuration(evt.Duration)}";
        return Post(settings, evt.DisplayTitle, $"{evt.Body}\n{summary}", priority, ct);
    }

    public Task<NotifyResult> TestAsync(IReadOnlyDictionary<string, string> settings, CancellationToken ct, string? machineName = null)
    {
        return Post(settings, MachineLabel.Prefix("Compressarr test notification", machineName), "This is a test notification from Compressarr.", 5, ct);
    }

    private async Task<NotifyResult> Post(IReadOnlyDictionary<string, string> settings, string title, string message, int priority, CancellationToken ct)
    {
        settings.TryGetValue("server", out var server);
        settings.TryGetValue("appToken", out var appToken);
        var url = $"{(server ?? "").TrimEnd('/')}/message";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var destination) || string.IsNullOrWhiteSpace(appToken))
        {
            return new NotifyResult(false, "A valid Server URL and Application Token are both required.");
        }

        var headers = new Dictionary<string, string> { ["X-Gotify-Key"] = appToken };

        try
        {
            using var response = await _http.PostJsonAsync(destination, new { title, message, priority }, headers, ct);
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
