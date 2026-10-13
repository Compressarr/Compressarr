using System.Text.Json;

namespace Compressarr.Core.Notifications;

/// <summary>Generic HTTP webhook - a configurable URL/method/custom headers, posting a fixed JSON
/// body. Also fully covers Zapier ("Webhooks by Zapier"), Make ("Webhooks" module), n8n (Webhook
/// node), Node-RED (http in node), and Home Assistant (webhook automation trigger) with zero
/// channel-specific code, since all of those accept an arbitrary POST with no required shape - a
/// user just points this at whichever platform's own webhook URL. The body also includes
/// value1/value2/value3 aliases of title/body/reportUrl specifically so IFTTT's Maker/Webhooks
/// service (which only populates a triggered applet's action from those exact key names) works
/// out of the box too, not just the six generic platforms above.</summary>
public sealed class WebhookNotifier : INotifier
{
    private readonly IWebhookSender _sender;

    public WebhookNotifier(IWebhookSender sender)
    {
        _sender = sender;
    }

    public string Type => "webhook";
    public string DisplayName => "Generic Webhook";

    public IReadOnlyList<NotifierField> Fields { get; } = new[]
    {
        new NotifierField("url", "URL", "text", Required: true,
            HelpText: "The full URL to send the request to. Also works for Zapier, Make, n8n, Node-RED, Home Assistant automation webhooks, and IFTTT (which reads value1/value2/value3 from the body this sends).",
            Placeholder: "https://example.com/webhook"),
        new NotifierField("method", "HTTP Method", "select", Required: false, Options: new[] { "POST", "PUT", "PATCH" },
            HelpText: "The HTTP method used for the request. Most services expect POST."),
        new NotifierField("headers", "Custom Headers (one per line, Name: Value)", "textarea", Required: false,
            HelpText: "Extra HTTP headers to send with the request, one per line as Name: Value - e.g. an Authorization header some services require.",
            Placeholder: "Authorization: Bearer ...")
    };

    public Task<NotifyResult> SendAsync(IReadOnlyDictionary<string, string> settings, NotificationEvent evt, CancellationToken ct)
    {
        var body = BuildBody(evt.Title, evt.Body, evt.TotalFiles, evt.SavedGb, evt.Duration, evt.ReportPath, evt.Outcome.ToString(), evt.MachineName);
        return Post(settings, body, ct);
    }

    public Task<NotifyResult> TestAsync(IReadOnlyDictionary<string, string> settings, CancellationToken ct, string? machineName = null)
    {
        var body = BuildBody("Compressarr test notification", "This is a test notification from Compressarr.", 0, 0, TimeSpan.Zero, null, "Test", machineName);
        return Post(settings, body, ct);
    }

    private Task<NotifyResult> Post(IReadOnlyDictionary<string, string> settings, string body, CancellationToken ct)
    {
        settings.TryGetValue("url", out var url);
        settings.TryGetValue("method", out var methodText);
        var method = string.IsNullOrWhiteSpace(methodText) ? HttpMethod.Post : new HttpMethod(methodText.Trim().ToUpperInvariant());

        var headers = new Dictionary<string, string>();
        if (settings.TryGetValue("headers", out var headerLines) && !string.IsNullOrWhiteSpace(headerLines))
        {
            foreach (var line in headerLines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var colonIndex = line.IndexOf(':');
                if (colonIndex <= 0) continue;
                headers[line[..colonIndex].Trim()] = line[(colonIndex + 1)..].Trim();
            }
        }

        return _sender.PostAsync(url ?? "", method, headers, body, "application/json", ct);
    }

    private static string BuildBody(string title, string message, int totalFiles, double savedGb, TimeSpan duration, string? reportPath, string outcome, string? machineName)
    {
        var payload = new Dictionary<string, object?>
        {
            ["title"] = title,
            ["body"] = message,
            ["outcome"] = outcome,
            ["totalFiles"] = totalFiles,
            ["savedGb"] = Math.Round(savedGb, 3),
            ["durationSeconds"] = (int)duration.TotalSeconds,
            ["reportPath"] = reportPath,
            // IFTTT's Maker/Webhooks service only reads these three exact key names for a
            // triggered applet's action fields - included so an IFTTT applet works without the
            // user needing to know that ahead of time.
            ["value1"] = title,
            ["value2"] = message,
            ["value3"] = reportPath
        };
        // Its own field rather than text in the title: this body is data for another program, so the
        // existing fields keep their meaning. Only present when the user turned the name on.
        if (!string.IsNullOrEmpty(machineName)) payload["machine"] = machineName;
        return JsonSerializer.Serialize(payload);
    }
}
