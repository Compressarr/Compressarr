using System.Net;
using System.Net.Sockets;
using Compressarr.Core.Net;

namespace Compressarr.Core.Tests.Net;

public class NetworkProblemTests
{
    [Fact]
    public void NoNetwork_IsOnePlainSentence_WithNoTechnicalText()
    {
        var noHost = new HttpRequestException("No such host is known. (api.github.com:443)", new SocketException(11001));

        var message = NetworkProblem.Describe(noHost);

        Assert.Equal("Couldn't reach GitHub. Check your internet connection and try again.", message);
        Assert.DoesNotContain("No such host", message);
        Assert.DoesNotContain("443", message);
    }

    [Fact]
    public void TheServiceNameIsWhatThePageIsAbout()
    {
        Assert.Equal("Couldn't reach the catalog. Check your internet connection and try again.",
            NetworkProblem.Describe(new HttpRequestException("x"), "the catalog"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "limiting requests")]
    [InlineData(HttpStatusCode.TooManyRequests, "limiting requests")]
    [InlineData(HttpStatusCode.NotFound, "doesn't have what was asked for")]
    [InlineData(HttpStatusCode.BadGateway, "had a problem answering (HTTP 502)")]
    [InlineData(HttpStatusCode.Conflict, "answered with an error (HTTP 409)")]
    public void AnAnswerThatWasAnError_SaysWhatKind(HttpStatusCode status, string expected) =>
        Assert.Contains(expected, NetworkProblem.Describe(new HttpRequestException("x", null, status)));

    [Fact]
    public void ATimeout_SaysGitHubDidNotAnswerInTime() =>
        Assert.Equal("GitHub didn't answer in time. Check your internet connection and try again.", NetworkProblem.Describe(new TaskCanceledException()));

    [Fact]
    public void SomethingElse_KeepsItsOwnMessage_SoNothingIsHidden() =>
        Assert.Equal("Something went wrong while contacting GitHub: odd", NetworkProblem.Describe(new InvalidOperationException("odd")));

    [Fact]
    public void OnlyNetworkExceptionsAreTreatedAsNetworkFailures()
    {
        Assert.True(NetworkProblem.IsNetworkFailure(new HttpRequestException()));
        Assert.True(NetworkProblem.IsNetworkFailure(new TaskCanceledException()));
        Assert.False(NetworkProblem.IsNetworkFailure(new InvalidOperationException()));
    }
}
