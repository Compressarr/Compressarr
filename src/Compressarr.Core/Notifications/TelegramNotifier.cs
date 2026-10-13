namespace Compressarr.Core.Notifications;

/// <summary>Telegram Bot API - a bot token plus the target chat id. Sends plain text with no
/// parse_mode rather than Markdown/HTML, since Telegram's Markdown escaping rules are strict and
/// brittle (MarkdownV2 requires escaping a long list of punctuation characters) and a report path
/// or filename containing any of them would otherwise silently break formatting or fail the send.</summary>
public sealed class TelegramNotifier : INotifier
{
    private readonly INotificationHttpClient _http;

    public TelegramNotifier(INotificationHttpClient http)
    {
        _http = http;
    }

    public string Type => "telegram";
    public string DisplayName => "Telegram";

    public IReadOnlyList<NotifierField> Fields { get; } = new[]
    {
        new NotifierField("botToken", "Bot Token", "text", Required: true, Secret: true,
            HelpText: "Message @BotFather on Telegram to create a bot and get its token.",
            Placeholder: "123456789:ABCdefGhIJKlmNoPQRsTUVwxyZ"),
        new NotifierField("chatId", "Chat ID", "text", Required: true,
            HelpText: "The numeric chat to send messages to. Message @userinfobot to find your own, or check your bot's getUpdates response for a group/channel.",
            Placeholder: "123456789")
    };

    public Task<NotifyResult> SendAsync(IReadOnlyDictionary<string, string> settings, NotificationEvent evt, CancellationToken ct)
    {
        var summary = $"Files: {evt.TotalFiles} | Saved: {evt.SavedGb:0.##} GB | Duration: {NotificationFormatting.FormatDuration(evt.Duration)}";
        var text = $"{NotificationFormatting.FormatOutcome(evt.Outcome)}: {evt.DisplayTitle}\n{evt.Body}\n{summary}";
        return Post(settings, text, ct);
    }

    public Task<NotifyResult> TestAsync(IReadOnlyDictionary<string, string> settings, CancellationToken ct, string? machineName = null)
    {
        var text = $"{MachineLabel.Prefix("Compressarr test notification", machineName)}\nThis is a test notification from Compressarr.";
        return Post(settings, text, ct);
    }

    private async Task<NotifyResult> Post(IReadOnlyDictionary<string, string> settings, string text, CancellationToken ct)
    {
        settings.TryGetValue("botToken", out var botToken);
        settings.TryGetValue("chatId", out var chatId);
        if (string.IsNullOrWhiteSpace(botToken) || string.IsNullOrWhiteSpace(chatId))
        {
            return new NotifyResult(false, "Bot Token and Chat ID are both required.");
        }

        var destination = new Uri($"https://api.telegram.org/bot{botToken}/sendMessage");

        try
        {
            using var response = await _http.PostJsonAsync(destination, new { chat_id = chatId, text }, headers: null, ct);
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
