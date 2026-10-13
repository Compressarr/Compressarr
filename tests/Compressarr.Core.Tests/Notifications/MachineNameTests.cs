using System.Net;
using System.Text.Json;
using Compressarr.Core.Config;
using Compressarr.Core.Notifications;
using Compressarr.Core.Reporting;

namespace Compressarr.Core.Tests.Notifications;

file sealed class RecordingHttpClient : INotificationHttpClient
{
    public string? LastBody { get; private set; }

    public Task<HttpResponseMessage> PostJsonAsync(Uri destination, object payload, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        LastBody = JsonSerializer.Serialize(payload);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    public Task<HttpResponseMessage> PostFormAsync(Uri destination, IReadOnlyDictionary<string, string> fields, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<HttpResponseMessage> PostTextAsync(Uri destination, string text, IReadOnlyDictionary<string, string>? headers, CancellationToken ct) =>
        throw new NotSupportedException();
}

file sealed class RecordingWebhookSender : IWebhookSender
{
    public string? LastBody { get; private set; }

    public Task<NotifyResult> PostAsync(string url, HttpMethod method, IReadOnlyDictionary<string, string> headers, string body, string contentType, CancellationToken ct)
    {
        LastBody = body;
        return Task.FromResult(new NotifyResult(true, "OK"));
    }
}

public class MachineNameTests
{
    private static readonly Dictionary<string, string> SlackSettings = new() { ["url"] = "https://hooks.slack.com/services/T/B/x" };

    private static NotificationEvent Event(string? machineName, bool prefix = true) => new(
        NotificationOutcome.Success, "Compressarr run #1 - Success", "Body", 1, 1, TimeSpan.FromSeconds(1), null,
        MachineName: machineName, PrefixMachineName: prefix);

    // ---- cleaning and the on/off rule ----

    [Theory]
    [InlineData("  Basement   NAS ", "Basement NAS")]
    [InlineData("[Basement] NAS", "Basement NAS")]
    [InlineData("Line\r\nBreak\tTab", "Line Break Tab")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Clean_TrimsCollapsesAndDropsBrackets(string? input, string expected) =>
        Assert.Equal(expected, MachineLabel.Clean(input));

    [Fact]
    public void Clean_CapsTheLength()
    {
        var cleaned = MachineLabel.Clean(new string('a', 100));
        Assert.Equal(MachineLabel.MaxLength, cleaned.Length);
    }

    [Fact]
    public void For_IsNullUnlessTheBoxIsTicked()
    {
        var settings = new NotificationSettings { IncludeMachineName = false, MachineName = "Basement NAS" };
        Assert.Null(MachineLabel.For(settings));

        settings.IncludeMachineName = true;
        Assert.Equal("Basement NAS", MachineLabel.For(settings));
    }

    [Fact]
    public void For_TickedWithABlankNameAddsNothing_NeverTheComputerName()
    {
        var settings = new NotificationSettings { IncludeMachineName = true, MachineName = "  " };
        Assert.Null(MachineLabel.For(settings));
    }

    [Fact]
    public void ADefaultInstall_IncludesNothing()
    {
        var settings = new NotificationSettings();
        Assert.False(settings.IncludeMachineName);
        Assert.Equal("", settings.MachineName);
        Assert.Null(MachineLabel.For(settings));
    }

    // ---- the title prefix ----

    [Fact]
    public void DisplayTitle_IsBracketedNameThenTitle()
    {
        Assert.Equal("[Basement NAS] Compressarr run #1 - Success", Event("Basement NAS").DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_NoNameMeansTheTitleUnchanged()
    {
        Assert.Equal("Compressarr run #1 - Success", Event(null).DisplayTitle);
    }

    [Fact]
    public void DisplayTitle_CustomStyleIsNotPrefixedOnTopOfItsOwnToken()
    {
        Assert.Equal("Compressarr run #1 - Success", Event("Basement NAS", prefix: false).DisplayTitle);
    }

    [Fact]
    public async Task Slack_PrefixesTheTitle()
    {
        var http = new RecordingHttpClient();
        await new SlackNotifier(http).SendAsync(SlackSettings, Event("Basement NAS"), CancellationToken.None);

        using var doc = JsonDocument.Parse(http.LastBody!);
        Assert.Contains("[Basement NAS] Compressarr run #1 - Success", doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Discord_PrefixesTheTitle()
    {
        var http = new RecordingHttpClient();
        await new DiscordNotifier(http).SendAsync(new Dictionary<string, string> { ["url"] = "https://discord.com/api/webhooks/1/abc" }, Event("Basement NAS"), CancellationToken.None);

        Assert.Contains("[Basement NAS] Compressarr run #1 - Success", http.LastBody);
    }

    [Fact]
    public async Task Slack_TestMessageShowsTheNameWhenGiven_AndNotOtherwise()
    {
        var http = new RecordingHttpClient();
        var notifier = new SlackNotifier(http);

        await notifier.TestAsync(SlackSettings, CancellationToken.None, "Basement NAS");
        Assert.Contains("[Basement NAS] Compressarr test notification", http.LastBody);

        await notifier.TestAsync(SlackSettings, CancellationToken.None);
        Assert.DoesNotContain("[", http.LastBody);
    }

    // ---- the Generic Webhook: its own field, title untouched ----

    [Fact]
    public async Task Webhook_GetsAMachineField_AndAnUnprefixedTitle()
    {
        var sender = new RecordingWebhookSender();
        await new WebhookNotifier(sender).SendAsync(new Dictionary<string, string> { ["url"] = "https://example.com/hook" }, Event("Basement NAS"), CancellationToken.None);

        using var doc = JsonDocument.Parse(sender.LastBody!);
        Assert.Equal("Basement NAS", doc.RootElement.GetProperty("machine").GetString());
        Assert.Equal("Compressarr run #1 - Success", doc.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Webhook_HasNoMachineFieldWhenNoNameIsSet()
    {
        var sender = new RecordingWebhookSender();
        await new WebhookNotifier(sender).SendAsync(new Dictionary<string, string> { ["url"] = "https://example.com/hook" }, Event(null), CancellationToken.None);

        using var doc = JsonDocument.Parse(sender.LastBody!);
        Assert.False(doc.RootElement.TryGetProperty("machine", out _));
    }

    // ---- digests ----

    [Fact]
    public void Digest_EventCarriesTheName_AndDisplaysItInFront()
    {
        var evt = new DigestSummary(3, 10, 5, TimeSpan.FromMinutes(5)).ToNotificationEvent("Daily Digest", "Basement NAS");
        Assert.Equal("[Basement NAS] Compressarr Daily Digest", evt.DisplayTitle);
    }

    // ---- the {machine} template token ----

    [Fact]
    public void Render_MachineToken_FillsWithTheNameOrNothing()
    {
        var report = new ReportModel { GeneratedAt = new DateTime(2026, 10, 12), RunTime = TimeSpan.FromMinutes(1), RunNumber = 1, Lanes = Array.Empty<LaneReportSection>() };

        Assert.Equal("Basement NAS: run 1", NotificationMessageRenderer.Render("{machine}: run {run_number}", NotificationOutcome.Success, report, "", "Basement NAS"));
        Assert.Equal(": run 1", NotificationMessageRenderer.Render("{machine}: run {run_number}", NotificationOutcome.Success, report, ""));
    }
}
