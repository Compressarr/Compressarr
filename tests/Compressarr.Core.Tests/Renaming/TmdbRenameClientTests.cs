using System.Net;
using System.Text;
using Compressarr.Core.Renaming;

namespace Compressarr.Core.Tests.Renaming;

internal sealed class TmdbFakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public List<Uri> Requests { get; } = new();

    public TmdbFakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(_responder(request));
    }
}

internal sealed class TmdbFakeFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    public TmdbFakeFactory(HttpMessageHandler handler) => _handler = handler;
    public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
}

public class TmdbRenameClientTests
{
    private const string SecretKey = "k3y-that-must-never-be-in-a-message";

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static TmdbRenameClient Client(TmdbFakeHandler handler) => new(new TmdbFakeFactory(handler), "https://tmdb.test/3", TimeSpan.Zero);

    [Fact]
    public async Task ASearchReadsTheResultsAndSendsTheTitleYearAndLanguage()
    {
        var handler = new TmdbFakeHandler(_ => Json("""
            {"results":[
              {"id":949,"title":"Heat","original_title":"Heat","release_date":"1995-12-15","popularity":16.2,"overview":"A crew.","poster_path":"/h.jpg"},
              {"id":5,"title":"No Date","original_title":"No Date","release_date":"","popularity":0},
              {"id":6,"overview":"no title at all"}
            ]}
            """));

        var results = await Client(handler).SearchAsync(SecretKey, tv: false, "Heat", 1995, "de-DE", CancellationToken.None);

        Assert.Equal(2, results.Count);                                         // the one without a title is dropped
        var heat = results[0];
        Assert.Equal((949, false, "Heat", "Heat", 1995, 16.2, "A crew.", "/h.jpg"), (heat.Id, heat.IsTv, heat.Title, heat.OriginalTitle, heat.Year, heat.Popularity, heat.Overview, heat.PosterPath));
        Assert.Empty(heat.OriginCountries);
        Assert.Null(results[1].Year);

        var query = handler.Requests.Single().Query;
        Assert.Contains("query=Heat", query);
        Assert.Contains("year=1995", query);
        Assert.Contains("language=de-DE", query);
        Assert.Equal("/3/search/movie", handler.Requests.Single().AbsolutePath);
    }

    [Fact]
    public async Task ATvSearchReadsNameFirstAirDateAndOriginCountry()
    {
        var handler = new TmdbFakeHandler(_ => Json("""{"results":[{"id":2316,"name":"The Office","original_name":"The Office","first_air_date":"2005-03-24","origin_country":["US"],"popularity":1740}]}"""));

        var results = await Client(handler).SearchAsync(SecretKey, tv: true, "The Office", 2005, "en-US", CancellationToken.None);

        var show = Assert.Single(results);
        Assert.Equal(2005, show.Year);
        Assert.Equal(new[] { "US" }, show.OriginCountries);
        Assert.Contains("first_air_date_year=2005", handler.Requests.Single().Query);
        Assert.Equal("/3/search/tv", handler.Requests.Single().AbsolutePath);
    }

    [Fact]
    public async Task ASeasonGivesTheEpisodeNames()
    {
        var handler = new TmdbFakeHandler(_ => Json("""{"episodes":[{"episode_number":1,"name":"Pilot"},{"episode_number":2,"name":"Cat's in the Bag..."}]}"""));

        var season = await Client(handler).GetSeasonAsync(SecretKey, 1396, 1, "en-US", CancellationToken.None);

        Assert.Equal("Pilot", season[1]);
        Assert.Equal("Cat's in the Bag...", season[2]);
        Assert.Equal("/3/tv/1396/season/1", handler.Requests.Single().AbsolutePath);
    }

    [Fact]
    public async Task ASeasonTmdbDoesNotHaveIsEmpty_NotAnError()
    {
        var season = await Client(new TmdbFakeHandler(_ => Json("{}", HttpStatusCode.NotFound))).GetSeasonAsync(SecretKey, 1, 99, "en-US", CancellationToken.None);

        Assert.Empty(season);
    }

    [Fact]
    public async Task ARejectedKeyIsReportedAsThat_AndNeverCarriesTheKey()
    {
        var ex = await Assert.ThrowsAsync<TmdbKeyRejectedException>(() =>
            Client(new TmdbFakeHandler(_ => Json("{}", HttpStatusCode.Unauthorized))).SearchAsync(SecretKey, false, "Heat", 1995, "en-US", CancellationToken.None));

        Assert.DoesNotContain(SecretKey, ex.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]       // a search endpoint that 404s is broken, unlike a season that does not exist
    public async Task AnyOtherFailureIsUnavailable_NeverNoSuchTitle(HttpStatusCode status)
    {
        var ex = await Assert.ThrowsAsync<TmdbUnavailableException>(() =>
            Client(new TmdbFakeHandler(_ => Json("{}", status))).SearchAsync(SecretKey, false, "Heat", 1995, "en-US", CancellationToken.None));

        Assert.DoesNotContain(SecretKey, ex.ToString());
    }

    [Fact]
    public async Task ANetworkErrorIsUnavailable_AndTheMessageDoesNotCarryTheUrl()
    {
        var handler = new TmdbFakeHandler(_ => throw new HttpRequestException($"could not reach https://tmdb.test/3/search/movie?api_key={SecretKey}"));

        var ex = await Assert.ThrowsAsync<TmdbUnavailableException>(() => Client(handler).SearchAsync(SecretKey, false, "Heat", 1995, "en-US", CancellationToken.None));

        Assert.DoesNotContain(SecretKey, ex.ToString());
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public async Task AnUnreadableAnswerIsUnavailable()
    {
        await Assert.ThrowsAsync<TmdbUnavailableException>(() =>
            Client(new TmdbFakeHandler(_ => Json("<html>not json</html>"))).SearchAsync(SecretKey, false, "Heat", 1995, "en-US", CancellationToken.None));
    }

    [Fact]
    public async Task ACancelledRequestIsCancelled_NotUnavailable()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(new TmdbFakeHandler(_ => Json("{}"))).SearchAsync(SecretKey, false, "Heat", 1995, "en-US", cts.Token));
    }
}
