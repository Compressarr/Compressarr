using System.Net;
using System.Text;
using Compressarr.Core.Config;
using Compressarr.Core.Posters;
using Compressarr.Core.Tests.Announcements;
using Compressarr.Core.Tests.Presets;

namespace Compressarr.Core.Tests.Posters;

public class PosterNameParserTests
{
    [Theory]
    [InlineData("Breaking Bad - S05E14 - Ozymandias.mkv", true, "Breaking Bad", null)]
    [InlineData("Breaking.Bad.S05E14.1080p.WEB.mkv", true, "Breaking Bad", null)]
    [InlineData("The Office (US) - S03E10 - A Benihana Christmas.mkv", true, "The Office", null)]
    [InlineData("Doctor Who (2005) - S01E01 - Rose.mkv", true, "Doctor Who", 2005)]
    [InlineData("Fight Club (1999).mkv", false, "Fight Club", 1999)]
    [InlineData("Fight Club (1999) {edition-Director's Cut}.mkv", false, "Fight Club", 1999)]
    [InlineData("The.Matrix.(1999).mkv", false, "The Matrix", 1999)]
    public void AFileName_BecomesAShowOrAMovieQuery(string fileName, bool isTv, string title, int? year)
    {
        var query = PosterNameParser.Parse(fileName);

        Assert.NotNull(query);
        Assert.Equal((isTv, title, year), (query!.IsTv, query.Title, query.Year));
    }

    [Theory]
    [InlineData("Family Vacation 2019.mp4")]     // a movie with no (YYYY) tag is not guessed at
    [InlineData("random.mkv")]
    [InlineData("(1999).mkv")]                    // a year and no title
    [InlineData("Old (1500).mkv")]                // not a plausible film year
    [InlineData("")]
    public void AFileThatCannotBeIdentified_GetsNoQuery(string fileName) => Assert.Null(PosterNameParser.Parse(fileName));

    [Fact]
    public void EpisodesOfOneShow_ShareOnePosterId_ButShowsAndMoviesDoNot()
    {
        var one = PosterNameParser.Parse("Severance - S02E03 - Who Is Alive.mkv")!;
        var two = PosterNameParser.Parse("severance.S01E01.mkv")!;
        var other = PosterNameParser.Parse("The Bear - S03E01.mkv")!;
        var movieSameName = PosterNameParser.Parse("Severance (2022).mkv")!;

        Assert.Equal(one.Id, two.Id);
        Assert.NotEqual(one.Id, other.Id);
        Assert.NotEqual(one.Id, movieSameName.Id);
        Assert.Matches("^[0-9a-f]{16}$", one.Id);
    }
}

public class PosterServiceTests : AppDataTestBase
{
    private const string Api = "https://tmdb.test/3";
    private const string Img = "https://img.test/t/p";

    private sealed class FakeHttp : IHttpClientFactory
    {
        private sealed class Handler : HttpMessageHandler
        {
            private readonly FakeHttp _owner;
            public Handler(FakeHttp owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (_owner._requests) _owner._requests.Add(request.RequestUri!);
                return Task.FromResult(_owner.Respond(request));
            }
        }

        private readonly List<Uri> _requests = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<Uri> Requests { get { lock (_requests) return _requests.ToList(); } }
        public HttpClient CreateClient(string name) => new(new Handler(this), disposeHandler: false);
    }

    private readonly FakeHttp _http = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero));
    private static readonly PosterSettings On = new() { Enabled = true, TmdbApiKey = "test-key-123" };

    private PosterService Service() => new(_http, _clock, TimeSpan.Zero, Api, Img);

    private static HttpResponseMessage Results(params string[] posterPaths) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{ \"results\": [" + string.Join(",", posterPaths.Select(p => p is null ? "{ \"poster_path\": null }" : $"{{ \"poster_path\": \"{p}\" }}")) + "] }", Encoding.UTF8, "application/json")
    };

    private static async Task<string?> WaitForId(PosterService service, PosterSettings settings, string file)
    {
        for (var i = 0; i < 250; i++)
        {
            if (service.GetPosterId(settings, file) is { } id) return id;
            await Task.Delay(20);
        }

        return null;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 250 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition(), "timed out waiting");
        await Task.Delay(100);   // let the lookup finish recording its answer
    }

    [Fact]
    public void WhenOffOrWithoutAKey_NothingIsEverRequested()
    {
        using var service = Service();

        Assert.Null(service.GetPosterId(new PosterSettings { Enabled = false, TmdbApiKey = "k" }, "Fight Club (1999).mkv"));
        Assert.Null(service.GetPosterId(new PosterSettings { Enabled = true, TmdbApiKey = "  " }, "Fight Club (1999).mkv"));

        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task AMovie_IsLookedUpByTitleAndYear_Once_AndOnlyThoseTwoThingsAreSent()
    {
        _http.Respond = _ => Results("/jSziioSwPVrOy9Yow3XhWIBDjq1.jpg");
        using var service = Service();
        var file = @"C:\Media\Movies\Fight Club (1999).mkv";

        Assert.Null(service.GetPosterId(On, file));              // not known yet: queued, never blocks
        var id = await WaitForId(service, On, file);

        Assert.NotNull(id);
        var request = Assert.Single(_http.Requests);
        Assert.Equal("tmdb.test", request.Host);
        Assert.Equal("/3/search/movie", request.AbsolutePath);
        Assert.Contains("query=Fight%20Club", request.Query);
        Assert.Contains("year=1999", request.Query);
        Assert.Contains("api_key=test-key-123", request.Query);
        Assert.DoesNotContain("Media", request.ToString());     // never a path
        Assert.DoesNotContain("mkv", request.ToString());       // never the file name

        Assert.Equal(id, service.GetPosterId(On, file));         // remembered
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task ATvShow_UsesTheTvSearch_AndASeasonCostsOneLookup()
    {
        _http.Respond = _ => Results("/abcdef12345.jpg");
        using var service = Service();

        var id = await WaitForId(service, On, "Doctor Who (2005) - S01E01 - Rose.mkv");

        Assert.NotNull(id);
        Assert.Equal(id, service.GetPosterId(On, "Doctor Who (2005) - S01E02 - The End of the World.mkv"));
        var request = Assert.Single(_http.Requests);
        Assert.Equal("/3/search/tv", request.AbsolutePath);
        Assert.Contains("first_air_date_year=2005", request.Query);
    }

    [Fact]
    public async Task WhatWasFound_SurvivesARestart()
    {
        _http.Respond = _ => Results("/abcdef12345.jpg");
        string id;
        using (var first = Service())
        {
            id = (await WaitForId(first, On, "Inception (2010).mkv"))!;
        }

        using var second = Service();
        Assert.Equal(id, second.GetPosterId(On, "Inception (2010).mkv"));   // straight from disk
        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task ANotFound_IsRememberedForTwoWeeks_ThenAskedAgain()
    {
        _http.Respond = _ => Results();    // TMDb has no such title
        using var service = Service();

        service.GetPosterId(On, "Nobody Knows This (2001).mkv");
        await WaitFor(() => _http.Requests.Count == 1);

        Assert.Null(service.GetPosterId(On, "Nobody Knows This (2001).mkv"));
        Assert.Single(_http.Requests);                                   // remembered: no second ask

        _clock.Now = _clock.Now.AddDays(15);
        service.GetPosterId(On, "Nobody Knows This (2001).mkv");
        await WaitFor(() => _http.Requests.Count == 2);
    }

    [Fact]
    public async Task AResultWithoutAUsablePoster_IsSkipped_AndAnUnsafePathIsNeverUsed()
    {
        _http.Respond = _ => Results(null!, "/../../evil.jpg", "/good12345.jpg");
        using var service = Service();

        var id = await WaitForId(service, On, "Parasite (2019).mkv");
        Assert.NotNull(id);

        _http.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
        var path = await service.GetImagePathAsync(On, id!, "w154");
        Assert.NotNull(path);
        Assert.EndsWith("good12345.jpg", path);   // the safe one was chosen, not the traversal path
    }

    [Fact]
    public async Task ARejectedKey_IsNotRetried_UntilItChanges_AndTheCardCanSayWhy()
    {
        _http.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        using var service = Service();

        service.GetPosterId(On, "Inception (2010).mkv");
        await WaitFor(() => service.GetCacheInfo().Problem is not null);
        Assert.Contains("rejected", service.GetCacheInfo().Problem);

        service.GetPosterId(On, "The Matrix (1999).mkv");        // same key: not even tried
        await Task.Delay(150);
        Assert.Single(_http.Requests);

        service.GetPosterId(new PosterSettings { Enabled = true, TmdbApiKey = "another-key" }, "The Matrix (1999).mkv");
        await WaitFor(() => _http.Requests.Count == 2);          // a different key is tried
    }

    [Fact]
    public async Task WhenTmdbCannotBeReached_ItIsLeftAloneForAFewMinutes_AndNothingIsRememberedAsNotFound()
    {
        _http.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        using var service = Service();

        service.GetPosterId(On, "Inception (2010).mkv");
        await WaitFor(() => _http.Requests.Count == 1);

        service.GetPosterId(On, "The Matrix (1999).mkv");        // inside the pause: not asked
        await Task.Delay(150);
        Assert.Single(_http.Requests);

        _clock.Now = _clock.Now.AddMinutes(6);
        _http.Respond = _ => Results("/abcdef12345.jpg");
        Assert.NotNull(await WaitForId(service, On, "Inception (2010).mkv"));   // asked again, and the earlier failure was not kept
    }

    [Fact]
    public async Task AnImage_IsDownloadedOncePerSize_ThenServedFromTheCache_AndOnlyKnownSizesAreAllowed()
    {
        _http.Respond = r => r.RequestUri!.Host == "tmdb.test" ? Results("/abcdef12345.jpg") : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 9, 9, 9, 9 }) };
        using var service = Service();
        var id = (await WaitForId(service, On, "Inception (2010).mkv"))!;

        var first = await service.GetImagePathAsync(On, id, "w154");
        var second = await service.GetImagePathAsync(On, id, "w154");

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, await File.ReadAllBytesAsync(first!));
        Assert.Equal(1, _http.Requests.Count(r => r.Host == "img.test"));                  // once, not twice
        Assert.Equal("/t/p/w154/abcdef12345.jpg", _http.Requests.Single(r => r.Host == "img.test").AbsolutePath);

        Assert.Null(await service.GetImagePathAsync(On, id, "original"));                  // not a size we serve
        Assert.Null(await service.GetImagePathAsync(On, id, "../w154"));
        Assert.Null(await service.GetImagePathAsync(On, "unknown-id", "w154"));
        Assert.Null(await service.GetImagePathAsync(new PosterSettings { Enabled = false }, id, "w154"));
        Assert.Equal(1, _http.Requests.Count(r => r.Host == "img.test"));
    }

    [Fact]
    public async Task APosterThatHasBeenWithdrawn_IsForgotten_SoTheTitleIsLookedUpAgain()
    {
        _http.Respond = r => r.RequestUri!.Host == "tmdb.test" ? Results("/abcdef12345.jpg") : new HttpResponseMessage(HttpStatusCode.NotFound);
        using var service = Service();
        var id = (await WaitForId(service, On, "Inception (2010).mkv"))!;

        Assert.Null(await service.GetImagePathAsync(On, id, "w154"));    // the image host says it is gone

        service.GetPosterId(On, "Inception (2010).mkv");
        await WaitFor(() => _http.Requests.Count(r => r.Host == "tmdb.test") == 2);
    }

    [Fact]
    public async Task TheTestButton_SaysWhatHappened()
    {
        using var service = Service();

        _http.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);
        Assert.True((await service.TestKeyAsync("good")).Success);
        Assert.Equal("/3/authentication", _http.Requests.Last().AbsolutePath);

        _http.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        var rejected = await service.TestKeyAsync("bad");
        Assert.False(rejected.Success);
        Assert.Contains("Read Access Token", rejected.Message);   // the usual mistake is named

        _http.Respond = _ => throw new HttpRequestException("no network");
        var offline = await service.TestKeyAsync("x");
        Assert.False(offline.Success);
        Assert.Contains("Could not reach", offline.Message);

        Assert.False((await service.TestKeyAsync("  ")).Success);
    }

    [Fact]
    public async Task ClearingTheCache_RemovesTheImagesAndWhatWasRemembered()
    {
        _http.Respond = r => r.RequestUri!.Host == "tmdb.test" ? Results("/abcdef12345.jpg") : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
        using var service = Service();
        var id = (await WaitForId(service, On, "Inception (2010).mkv"))!;
        await service.GetImagePathAsync(On, id, "w92");
        await service.GetImagePathAsync(On, id, "w154");
        Assert.Equal(1, service.GetCacheInfo().Posters);
        Assert.True(service.GetCacheInfo().Bytes > 0);

        Assert.Equal(2, service.ClearCache());

        Assert.Equal(0, service.GetCacheInfo().Posters);
        Assert.Equal(0, service.GetCacheInfo().Bytes);
        Assert.False(File.Exists(Path.Combine(AppPaths.GetPostersDirectory(), "index.json")));
    }

    // ---- the cache cleanup --------------------------------------------------------------------------------------------------------

    private HttpResponseMessage ImageOrResults(HttpRequestMessage r) =>
        r.RequestUri!.Host == "tmdb.test" ? Results("/abcdef12345.jpg") : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 7, 7, 7 }) };

    [Fact]
    public async Task TheCleanup_RemovesPicturesNotUsedForTheRetentionPeriod_ButKeepsWhatWasLookedUp()
    {
        _http.Respond = ImageOrResults;
        using var service = Service();
        var id = (await WaitForId(service, On, "Inception (2010).mkv"))!;
        var picture = (await service.GetImagePathAsync(On, id, "w154"))!;
        Assert.True(File.Exists(picture));

        _clock.Now = _clock.Now.AddDays(29);
        Assert.Equal(0, service.CleanUp(30));                       // not old enough yet
        Assert.True(File.Exists(picture));

        _clock.Now = _clock.Now.AddDays(2);                         // 31 days unused
        Assert.Equal(1, service.CleanUp(30));
        Assert.False(File.Exists(picture));

        // The lookup was kept: the title is still known and its picture comes back on demand, with no new search.
        Assert.Equal(id, service.GetPosterId(On, "Inception (2010).mkv"));
        Assert.NotNull(await service.GetImagePathAsync(On, id, "w154"));
        Assert.True(File.Exists(picture));
        Assert.Equal(1, _http.Requests.Count(r => r.Host == "tmdb.test"));
    }

    [Fact]
    public async Task ATitleStillInTheQueue_KeepsItsPicture_BecauseSeeingItAgainCountsAsUse()
    {
        _http.Respond = ImageOrResults;
        using var service = Service();
        var id = (await WaitForId(service, On, "Inception (2010).mkv"))!;
        var picture = (await service.GetImagePathAsync(On, id, "w154"))!;

        _clock.Now = _clock.Now.AddDays(20);
        Assert.Equal(id, service.GetPosterId(On, "Inception (2010).mkv"));   // still queued on day 20

        _clock.Now = _clock.Now.AddDays(20);                                  // 40 days after the download, 20 since it was last seen
        Assert.Equal(0, service.CleanUp(30));
        Assert.True(File.Exists(picture));
    }

    [Fact]
    public async Task TheCleanup_AlsoRemovesStrayFiles_LeavesAFreshDownloadInProgressAlone_AndDropsOldNotFounds()
    {
        _http.Respond = _ => Results();      // TMDb knows no such title
        using var service = Service();
        service.GetPosterId(On, "Nobody Knows This (2001).mkv");
        await WaitFor(() => _http.Requests.Count == 1);

        var directory = Path.Combine(AppPaths.GetPostersDirectory(), "w154");
        Directory.CreateDirectory(directory);
        var stray = Path.Combine(directory, "stray12345.jpg");
        var inProgress = Path.Combine(directory, "downloading1.jpg.tmp");
        File.WriteAllBytes(stray, new byte[] { 1 });
        File.WriteAllBytes(inProgress, new byte[] { 1 });
        var indexFile = Path.Combine(AppPaths.GetPostersDirectory(), "index.json");
        Assert.Contains("Nobody Knows This", File.ReadAllText(indexFile));

        _clock.Now = _clock.Now.AddDays(31);
        Assert.Equal(1, service.CleanUp(30));
        Assert.False(File.Exists(stray));
        Assert.True(File.Exists(inProgress));                                   // written just now: someone may be downloading it
        Assert.DoesNotContain("Nobody Knows This", File.ReadAllText(indexFile)); // the stale "not found" is gone
    }

    [Fact]
    public async Task ARetentionOfZero_KeepsEverything_LikeLogsAndReports()
    {
        _http.Respond = ImageOrResults;
        using var service = Service();
        var id = (await WaitForId(service, On, "Inception (2010).mkv"))!;
        var picture = (await service.GetImagePathAsync(On, id, "w154"))!;

        _clock.Now = _clock.Now.AddDays(365);

        Assert.Equal(0, service.CleanUp(0));
        Assert.Equal(0, service.CleanUp(-5));
        Assert.True(File.Exists(picture));
    }

    [Fact]
    public async Task TheCleanup_RunsByItselfInTheBackground_AtMostOncePerDay()
    {
        _http.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        using var service = Service();
        var directory = Path.Combine(AppPaths.GetPostersDirectory(), "w154");
        Directory.CreateDirectory(directory);
        var first = Path.Combine(directory, "firststray1.jpg");
        File.WriteAllBytes(first, new byte[] { 1 });

        service.GetPosterId(On, "Inception (2010).mkv");                          // the first request of the day starts the cleanup
        for (var i = 0; i < 250 && File.Exists(first); i++) await Task.Delay(20);
        Assert.False(File.Exists(first));

        var second = Path.Combine(directory, "secondstray.jpg");
        File.WriteAllBytes(second, new byte[] { 1 });
        service.GetPosterId(On, "Inception (2010).mkv");
        await Task.Delay(300);
        Assert.True(File.Exists(second));                                         // same day: not cleaned again

        _clock.Now = _clock.Now.AddHours(25);
        service.GetPosterId(On, "Inception (2010).mkv");
        for (var i = 0; i < 250 && File.Exists(second); i++) await Task.Delay(20);
        Assert.False(File.Exists(second));                                        // the next day it runs again
    }

    [Fact]
    public async Task SeveralRequestsForTheSamePicture_AtOnce_AllGetIt_AndLeaveNoTempFileBehind()
    {
        _http.Respond = r => r.RequestUri!.Host == "tmdb.test"
            ? Results("/abcdef12345.jpg")
            : Slow(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 5, 5, 5, 5 }) });
        using var service = Service();
        var id = (await WaitForId(service, On, "Inception (2010).mkv"))!;

        var paths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => service.GetImagePathAsync(On, id, "w154"))));

        Assert.All(paths, p => Assert.NotNull(p));
        Assert.Single(paths.Distinct());
        Assert.Equal(new byte[] { 5, 5, 5, 5 }, await ReadWhenFree(paths[0]!));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(paths[0]!)!, "*.tmp"));
    }

    private static async Task<byte[]> ReadWhenFree(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await File.ReadAllBytesAsync(path); }
            catch (IOException) when (attempt < 10) { await Task.Delay(50); }   // a virus scanner can hold a new file for a moment
        }
    }

    private static HttpResponseMessage Slow(HttpResponseMessage response)
    {
        Thread.Sleep(80);   // long enough for the parallel requests to overlap
        return response;
    }

    [Fact]
    public async Task ASeenAgainMark_IsWrittenInTheBackground_OnceAFewMinutesHavePassed()
    {
        _http.Respond = r => r.RequestUri!.Host == "tmdb.test" ? Results("/abcdef12345.jpg") : new HttpResponseMessage(HttpStatusCode.OK);
        using var service = Service();
        var id = (await WaitForId(service, On, "Inception (2010).mkv"))!;
        var indexFile = Path.Combine(Compressarr.Core.Config.AppPaths.GetPostersDirectory(), "index.json");
        var before = await File.ReadAllTextAsync(indexFile);

        _clock.Now = _clock.Now.AddDays(2);
        Assert.Equal(id, service.GetPosterId(On, "Inception (2010).mkv"));   // the mark is made in memory
        _clock.Now = _clock.Now.AddMinutes(6);
        Assert.Equal(id, service.GetPosterId(On, "Inception (2010).mkv"));   // a few minutes on: written out, without making this call wait

        for (var i = 0; i < 250 && await File.ReadAllTextAsync(indexFile) == before; i++) await Task.Delay(20);
        Assert.NotEqual(before, await File.ReadAllTextAsync(indexFile));
    }

    [Fact]
    public async Task ThatATitleWasSeenAgain_IsNotLostWhenTheAppCloses_EvenRightAfterAnotherWrite()
    {
        _http.Respond = r => r.RequestUri!.Host == "tmdb.test"
            ? Results(r.RequestUri.Query.Contains("Matrix") ? "/matrixpath1.jpg" : "/abcdef12345.jpg")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 7, 7, 7 }) };
        string picture;
        using (var first = Service())
        {
            var id = (await WaitForId(first, On, "Inception (2010).mkv"))!;
            picture = (await first.GetImagePathAsync(On, id, "w154"))!;

            _clock.Now = _clock.Now.AddDays(20);
            Assert.NotNull(await WaitForId(first, On, "The Matrix (1999).mkv"));   // a lookup writes the index just now...
            _clock.Now = _clock.Now.AddMinutes(1);
            Assert.Equal(id, first.GetPosterId(On, "Inception (2010).mkv"));        // ...so this "seen again" mark is not written yet
        }                                                                          // closing the app must write it

        _clock.Now = _clock.Now.AddDays(20);                                       // day 40: 20 days since it was seen
        using var second = Service();
        Assert.Equal(0, second.CleanUp(30));
        Assert.True(File.Exists(picture));
    }
}
