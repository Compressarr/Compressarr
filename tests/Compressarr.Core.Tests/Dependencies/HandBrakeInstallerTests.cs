using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
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
            { "name": "HandBrakeCLI-1.11.2-win-x86_64.zip", "browser_download_url": "https://github.com/HandBrake/HandBrake/releases/download/1.11.2/HandBrakeCLI-1.11.2-win-x86_64.zip", "size": 25721458, "digest": "sha256:80bfe8d5f5d11cc3ef76b834add3ed4e82dee6523ffeb435c283f88b1a21f09d" },
            { "name": "HandBrakeCLI-1.11.2-win-aarch64.zip", "browser_download_url": "https://github.com/HandBrake/HandBrake/releases/download/1.11.2/HandBrakeCLI-1.11.2-win-aarch64.zip", "size": 24000000 },
            { "name": "HandBrakeCLI-1.11.2.dmg", "browser_download_url": "https://github.com/HandBrake/HandBrake/releases/download/1.11.2/HandBrakeCLI-1.11.2.dmg", "size": 30000000, "digest": "sha256:14463aa81038aaa3ce421dc6cee65fd6c82fdabda040931541ccca38939299fa" },
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
    public async Task GetLatestReleaseAsync_CarriesTheChecksumGitHubPublishedForTheAsset()
    {
        if (OperatingSystem.IsLinux()) return;

        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SampleReleaseJson) });
        var release = await new Compressarr.Core.Dependencies.HandBrakeInstaller(new FakeHttpClientFactory(handler)).GetLatestReleaseAsync();

        Assert.Equal(OperatingSystem.IsWindows() ? "80bfe8d5f5d11cc3ef76b834add3ed4e82dee6523ffeb435c283f88b1a21f09d" : "14463aa81038aaa3ce421dc6cee65fd6c82fdabda040931541ccca38939299fa", release!.Sha256);
    }

    [Theory]
    [InlineData("sha256:abc123", "abc123")]
    [InlineData("SHA256:ABC123", "ABC123")]
    [InlineData("sha512:abc123", null)]
    [InlineData("sha256:", null)]
    [InlineData(null, null)]
    public void DigestOf_ReadsOnlyASha256Value(string? digest, string? expected)
    {
        var asset = new JsonObject { ["name"] = "x.zip" };
        if (digest is not null) asset["digest"] = digest;

        Assert.Equal(expected, Compressarr.Core.Dependencies.HandBrakeInstaller.DigestOf(asset));
    }

    [Fact]
    public async Task InstallAsync_WithNoPublishedChecksum_IsRefusedBeforeAnythingIsDownloaded()
    {
        var requests = 0;
        var handler = new FakeHttpMessageHandler(_ => { requests++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) }; });
        var installer = new Compressarr.Core.Dependencies.HandBrakeInstaller(new FakeHttpClientFactory(handler));
        var folder = Path.Combine(Path.GetTempPath(), "compressarr-hbinst-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var release = new HandBrakeReleaseInfo("1.11.2", "HandBrakeCLI-1.11.2-win-x86_64.zip", "https://example.invalid/hb.zip", 3, "https://example.invalid/r", Sha256: null);

            await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(release, folder));

            Assert.Equal(0, requests);
            Assert.False(Directory.Exists(folder));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_ADownloadThatDoesNotMatchTheChecksum_IsThrownAwayAndNothingIsInstalled()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 }) });
        var installer = new Compressarr.Core.Dependencies.HandBrakeInstaller(new FakeHttpClientFactory(handler));
        var folder = Path.Combine(Path.GetTempPath(), "compressarr-hbinst-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var release = new HandBrakeReleaseInfo("1.11.2", "HandBrakeCLI-1.11.2-win-x86_64.zip", "https://example.invalid/hb.zip", 4, "https://example.invalid/r", Sha256: new string('0', 64));

            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(release, folder));

            Assert.Contains("does not match", ex.Message);
            Assert.Empty(Directory.EnumerateFiles(folder));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task InstallAsync_ADownloadThatMatchesTheChecksum_IsInstalled()
    {
        if (!OperatingSystem.IsWindows()) return;   // the zip route is the Windows one

        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("HandBrakeCLI-1.11.2-win-x86_64/HandBrakeCLI.exe").Open());
            writer.Write("fake program");
        }
        var bytes = zip.ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        var installer = new Compressarr.Core.Dependencies.HandBrakeInstaller(new FakeHttpClientFactory(handler));
        var folder = Path.Combine(Path.GetTempPath(), "compressarr-hbinst-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var release = new HandBrakeReleaseInfo("1.11.2", "HandBrakeCLI-1.11.2-win-x86_64.zip", "https://example.invalid/hb.zip", bytes.Length, "https://example.invalid/r", Sha256: sha);

            var installed = await installer.InstallAsync(release, folder);

            Assert.Equal(Path.Combine(folder, "HandBrakeCLI.exe"), installed);
            Assert.True(File.Exists(installed));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }
}
