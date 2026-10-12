using System.Net;
using Compressarr.Core.Config;
using Compressarr.Core.Updates;

namespace Compressarr.Core.Tests.Updates;

file sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public int CallCount { get; private set; }

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(_responder(request));
    }
}

file sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
    public HttpClient CreateClient(string name) => new(_handler);
}

file sealed class ChannelConfigStore : IConfigStore
{
    private readonly CompressarrConfig _config = new();
    public ChannelConfigStore(UpdateChannel channel) => _config.UpdateCheck.Channel = channel;
    public CompressarrConfig Load(string path) => _config;
    public void Save(CompressarrConfig config, string path) { }
    public T Update<T>(string path, Func<CompressarrConfig, T> mutate) => mutate(_config);
}

public class UpdateCheckServiceTests
{
    // Real, very short interval rather than a fake TimeProvider - same tradeoff
    // BackupSchedulerTests/RunLoopControllerTests make.
    private static readonly TimeSpan TinyInterval = TimeSpan.FromMilliseconds(20);

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(5);
        }
        throw new TimeoutException("Condition was not met within the timeout.");
    }

    private static string ReleaseJson(string tag) => $$"""
        { "tag_name": "{{tag}}", "html_url": "https://github.com/Compressarr/Compressarr/releases/tag/{{tag}}" }
        """;

    // The installed version at test time is whatever this test assembly's own <Version> is
    // (Directory.Build.props, shared repo-wide) - a tag comfortably below any real release
    // guarantees HasUpdate stays false regardless of what version this repo is actually on, and a
    // tag far in the future guarantees it stays true.
    private const string OldTag = "v0.0.1";
    private const string FutureTag = "v999.0.0";

    [Fact]
    public async Task CheckNowAsync_NewerTagOnGitHub_ReportsHasUpdate()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ReleaseJson(FutureTag))
        });
        var service = new UpdateCheckService(new FakeHttpClientFactory(handler));

        var result = await service.CheckNowAsync();

        Assert.True(result.CheckedOk);
        Assert.True(result.HasUpdate);
        Assert.Equal(FutureTag, result.LatestVersion);
        Assert.Equal($"https://github.com/Compressarr/Compressarr/releases/tag/{FutureTag}", result.ReleaseUrl);
    }

    [Fact]
    public async Task CheckNowAsync_OlderOrEqualTagOnGitHub_ReportsNoUpdate()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ReleaseJson(OldTag))
        });
        var service = new UpdateCheckService(new FakeHttpClientFactory(handler));

        var result = await service.CheckNowAsync();

        Assert.True(result.CheckedOk);
        Assert.False(result.HasUpdate);
    }

    [Fact]
    public async Task CheckNowAsync_GitHubUnreachable_ReturnsNotCheckedOkRatherThanThrowing()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("simulated network failure"));
        var service = new UpdateCheckService(new FakeHttpClientFactory(handler));

        var result = await service.CheckNowAsync();

        Assert.False(result.CheckedOk);
        Assert.NotNull(result.Error);
        Assert.False(result.HasUpdate);
    }

    [Fact]
    public async Task CheckNowAsync_NonSuccessStatus_ReturnsNotCheckedOk()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var service = new UpdateCheckService(new FakeHttpClientFactory(handler));

        var result = await service.CheckNowAsync();

        Assert.False(result.CheckedOk);
        Assert.False(result.HasUpdate);
    }

    [Fact]
    public async Task Start_RunsFirstCheckImmediately_WithoutWaitingOutTheInterval()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ReleaseJson(FutureTag))
        });
        // A long interval - if Start() waited it out before its first check, LastResult would
        // still be null when we assert a moment later.
        var service = new UpdateCheckService(new FakeHttpClientFactory(handler), TimeProvider.System, TimeSpan.FromHours(24));

        service.Start();
        await WaitUntil(() => service.LastResult is not null, TimeSpan.FromSeconds(2));

        Assert.True(service.LastResult!.HasUpdate);
        await service.StopAsync();
    }

    [Fact]
    public async Task Start_ChecksAgainAfterIntervalElapses()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ReleaseJson(OldTag))
        });
        var service = new UpdateCheckService(new FakeHttpClientFactory(handler), TimeProvider.System, TinyInterval);

        service.Start();
        await WaitUntil(() => handler.CallCount >= 3, TimeSpan.FromSeconds(2));

        await service.StopAsync();
    }

    [Fact]
    public void Start_CalledTwice_DoesNotDoubleStart()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ReleaseJson(OldTag))
        });
        var service = new UpdateCheckService(new FakeHttpClientFactory(handler), TimeProvider.System, TinyInterval);

        service.Start();
        service.Start();

        Assert.True(service.IsRunning);
    }

    [Fact]
    public async Task StopAsync_StopsTheLoop_NoMoreChecksAfterStopping()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(ReleaseJson(OldTag))
        });
        var service = new UpdateCheckService(new FakeHttpClientFactory(handler), TimeProvider.System, TinyInterval);

        service.Start();
        await WaitUntil(() => handler.CallCount >= 1, TimeSpan.FromSeconds(2));
        await service.StopAsync();

        var countAtStop = handler.CallCount;
        await Task.Delay(TinyInterval * 5);

        Assert.Equal(countAtStop, handler.CallCount);
        Assert.False(service.IsRunning);
    }

    // ---- the update channel (Settings > Updates) ----

    private static string Release(string tag, bool prerelease = false, bool draft = false) =>
        $$"""{ "tag_name": "{{tag}}", "html_url": "https://github.com/Compressarr/Compressarr/releases/tag/{{tag}}", "prerelease": {{(prerelease ? "true" : "false")}}, "draft": {{(draft ? "true" : "false")}} }""";

    private static UpdateCheckService Channelled(HttpMessageHandler handler, UpdateChannel channel, string installed) =>
        new(new FakeHttpClientFactory(handler), TimeProvider.System, TimeSpan.FromHours(24), new ChannelConfigStore(channel), () => AppVersion.Parse(installed));

    private static HttpMessageHandler ListOf(params string[] releases) =>
        new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[" + string.Join(",", releases) + "]") });

    [Fact]
    public async Task Stable_AsksForTheLatestRealRelease_AndBetaAsksForTheWholeList()
    {
        var urls = new List<string>();
        var handler = new FakeHttpMessageHandler(request =>
        {
            urls.Add(request.RequestUri!.AbsolutePath + request.RequestUri.Query);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.AbsolutePath.EndsWith("/latest") ? Release("v2.1.10") : "[" + Release("v2.1.10") + "]") };
        });

        var stable = await Channelled(handler, UpdateChannel.Stable, "2.1.9").CheckNowAsync();
        var beta = await Channelled(handler, UpdateChannel.Beta, "2.1.9").CheckNowAsync();

        Assert.Equal("/repos/Compressarr/Compressarr/releases/latest", urls[0]);
        Assert.Equal("/repos/Compressarr/Compressarr/releases?per_page=100", urls[1]);
        Assert.Equal(("Stable", "Beta"), (stable.Channel, beta.Channel));
    }

    [Fact]
    public async Task Beta_OffersTheHighestVersionOnEitherList_SoABetaOfTheNextVersionBeatsTheCurrentStable()
    {
        var handler = ListOf(Release("v2.1.10"), Release("v2.2.0-beta.6", prerelease: true), Release("v3.0.0", draft: true), Release("catalog-2026.10.11"));

        var result = await Channelled(handler, UpdateChannel.Beta, "2.1.9").CheckNowAsync();

        Assert.True(result.CheckedOk);
        Assert.True(result.HasUpdate);
        Assert.Equal("v2.2.0-beta.6", result.LatestVersion);      // not the draft, not the unrelated tag, not 2.1.10
        Assert.EndsWith("/releases/tag/v2.2.0-beta.6", result.ReleaseUrl);
    }

    [Theory]
    [InlineData("2.2.0-beta.5", true)]     // an older beta is told about the newer one
    [InlineData("2.2.0-beta.6", false)]    // the same beta is up to date
    [InlineData("2.2.0", false)]           // a final release is above its own betas
    public async Task Beta_ComparesPreReleasesAsVersions(string installed, bool expected)
    {
        var handler = ListOf(Release("v2.1.10"), Release("v2.2.0-beta.6", prerelease: true));

        var result = await Channelled(handler, UpdateChannel.Beta, installed).CheckNowAsync();

        Assert.Equal(expected, result.HasUpdate);
    }

    [Fact]
    public async Task Beta_AFinalReleaseBeatsItsBetas()
    {
        var handler = ListOf(Release("v2.2.0-beta.6", prerelease: true), Release("v2.2.0"), Release("v2.1.10"));

        var result = await Channelled(handler, UpdateChannel.Beta, "2.2.0-beta.6").CheckNowAsync();

        Assert.True(result.HasUpdate);
        Assert.Equal("v2.2.0", result.LatestVersion);
    }

    [Theory]
    [InlineData("2.1.9", "v2.1.10", true)]
    [InlineData("2.2.0-beta.5", "v2.1.10", false)]   // a 2.2 beta is not told to go back to 2.1
    [InlineData("2.2.0-beta.5", "v2.2.0", true)]     // ...but is told when 2.2.0 itself is out (the plain-number check could not say so)
    [InlineData("2.2.0", "v2.2.0", false)]
    public async Task Stable_ComparesTheInstalledVersionIncludingItsPreReleaseTag(string installed, string latestTag, bool expected)
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Release(latestTag)) });

        var result = await Channelled(handler, UpdateChannel.Stable, installed).CheckNowAsync();

        Assert.Equal(expected, result.HasUpdate);
    }

    [Fact]
    public async Task Beta_AListWithNothingRecognisable_IsAQuietNoUpdate()
    {
        var result = await Channelled(ListOf(Release("catalog-1"), Release("nightly")), UpdateChannel.Beta, "2.1.9").CheckNowAsync();

        Assert.True(result.CheckedOk);
        Assert.False(result.HasUpdate);
    }
}
