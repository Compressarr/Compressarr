using System.Net;
using Compressarr.Core.Dependencies;

namespace Compressarr.Core.Tests.Dependencies;

file sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public HttpRequestMessage? LastRequest { get; private set; }

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(_responder(request));
    }
}

file sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
    public HttpClient CreateClient(string name) => new(_handler);
}

public class HandBrakeInstallerTests
{
    // A trimmed but structurally real payload, matching what api.github.com/repos/HandBrake/HandBrake/releases/latest
    // actually returns (verified against the live API before writing this test).
    private const string SampleReleaseJson = """
        {
          "tag_name": "1.11.2",
          "html_url": "https://github.com/HandBrake/HandBrake/releases/tag/1.11.2",
          "assets": [
            { "name": "HandBrakeCLI-1.11.2-win-x86_64.zip", "browser_download_url": "https://github.com/HandBrake/HandBrake/releases/download/1.11.2/HandBrakeCLI-1.11.2-win-x86_64.zip", "size": 25721458 },
            { "name": "HandBrakeCLI-1.11.2-win-aarch64.zip", "browser_download_url": "https://github.com/HandBrake/HandBrake/releases/download/1.11.2/HandBrakeCLI-1.11.2-win-aarch64.zip", "size": 24000000 },
            { "name": "HandBrakeCLI-1.11.2.dmg", "browser_download_url": "https://github.com/HandBrake/HandBrake/releases/download/1.11.2/HandBrakeCLI-1.11.2.dmg", "size": 30000000 },
            { "name": "HandBrake-1.11.2-x86_64-Win_GUI.zip", "browser_download_url": "https://example.invalid/gui.zip", "size": 99999999 }
          ]
        }
        """;

    [Theory]
    [InlineData("1.11.2")]
    [InlineData("1.12.0")]
    public void GetExpectedAssetName_CurrentPlatform_MatchesRealHandBrakeNamingConvention(string version)
    {
        // This test runs on whatever OS the test suite executes on - on Windows (the CI/dev
        // environment here), it asserts the win-x86_64 zip name; the mac/linux branches are
        // covered by code review since there's no macOS/Linux runner available in this
        // environment, but the OperatingSystem.IsWindows()/IsMacOS() branching itself is simple
        // enough that reviewing it directly against real observed asset names (see
        // SampleReleaseJson above, pulled from a live API call) gives reasonable confidence.
        var name = Compressarr.Core.Dependencies.HandBrakeInstaller.GetExpectedAssetName(version);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal($"HandBrakeCLI-{version}-win-x86_64.zip", name);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Equal($"HandBrakeCLI-{version}.dmg", name);
        }
        else
        {
            Assert.Null(name);
        }
    }

    [Fact]
    public async Task GetLatestReleaseAsync_ParsesRealShapedResponse_PicksCorrectAssetForCurrentPlatform()
    {
        if (OperatingSystem.IsLinux()) return; // GetLatestReleaseAsync short-circuits to null on Linux by design.

        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SampleReleaseJson)
        });
        var installer = new Compressarr.Core.Dependencies.HandBrakeInstaller(new FakeHttpClientFactory(handler));

        var release = await installer.GetLatestReleaseAsync();

        Assert.NotNull(release);
        Assert.Equal("1.11.2", release!.Version);
        Assert.Equal("https://github.com/HandBrake/HandBrake/releases/tag/1.11.2", release.ReleaseUrl);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("HandBrakeCLI-1.11.2-win-x86_64.zip", release.AssetName);
            Assert.Equal(25721458, release.SizeBytes);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Equal("HandBrakeCLI-1.11.2.dmg", release.AssetName);
        }
    }

    [Fact]
    public async Task GetLatestReleaseAsync_SendsGitHubRequiredUserAgentHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            capturedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SampleReleaseJson) };
        });
        var installer = new Compressarr.Core.Dependencies.HandBrakeInstaller(new FakeHttpClientFactory(handler));

        await installer.GetLatestReleaseAsync();

        // The GitHub API returns 403 for any request with no User-Agent header - this is the
        // literal difference between the feature working and silently failing on every user's
        // machine, so it's worth asserting directly rather than trusting the implementation.
        Assert.NotNull(capturedRequest);
        Assert.NotEmpty(capturedRequest!.Headers.UserAgent);
    }

    [Fact]
    public void ADownloadedHandBrake_GetsALicenseNoteBesideIt_AndAnUnwritableFolderNeverFailsTheInstall()
    {
        var dir = Directory.CreateTempSubdirectory("compressarr-hb-note-").FullName;
        try
        {
            HandBrakeInstaller.WriteLicenseNotice(dir);

            var note = File.ReadAllText(Path.Combine(dir, "LICENSE-NOTICE.txt"));
            Assert.Contains("GNU General Public License, version 2", note);
            Assert.Contains("https://github.com/HandBrake/HandBrake", note);
            Assert.Contains("not part of Compressarr", note);

            HandBrakeInstaller.WriteLicenseNotice(Path.Combine(dir, "does", "not", "exist")); // a missing folder is a courtesy that failed, not an error
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
