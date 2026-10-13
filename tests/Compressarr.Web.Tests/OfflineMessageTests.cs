using System.Net;
using System.Text.Json.Nodes;

namespace Compressarr.Web.Tests;

/// <summary>What the page's "Check for Updates" / Check-Install buttons show when GitHub cannot be reached: a JSON answer carrying one plain
/// message, never an empty server error (which the HandBrake check used to produce, and the browser then showed as "Unexpected end of JSON
/// input"). The test host answers every outgoing request with a 404, which stands in for "GitHub did not give a usable answer".</summary>
public class OfflineMessageTests
{
    private static readonly LaneSpec Lane1 = new("hdsd", "SD-HD");

    [Fact]
    public async Task TheHandBrakeCheck_AnswersWithAMessage_NotAnEmptyServerError()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        using var response = await host.Client.GetAsync("/api/handbrake/latest-release");
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;   // would throw on an empty body

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(body["available"]!.GetValue<bool>());
        Assert.False(string.IsNullOrWhiteSpace(body["error"]!.GetValue<string>()));
        Assert.DoesNotContain("Exception", body["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheHandBrakeInstall_FailsWithAMessageTheEncoderPageCanShow()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        using var response = await host.Client.PostAsync("/api/handbrake/install", null);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(body["message"]!.GetValue<string>()));
    }

    [Fact]
    public async Task TheAboutPageUpdateCheck_CarriesAPlainErrorSentence()
    {
        await using var host = await QueueHost.StartAsync(Lane1);

        var body = JsonNode.Parse(await host.Client.GetStringAsync("/api/about/check-update"))!;

        Assert.False(body["checkedOk"]!.GetValue<bool>());
        var error = body["error"]!.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.DoesNotContain("No such host", error);
    }
}
