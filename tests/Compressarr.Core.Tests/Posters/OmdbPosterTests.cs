using System.Net;
using System.Text;
using Compressarr.Core.Config;
using Compressarr.Core.Posters;

namespace Compressarr.Core.Tests.Posters;

public class PosterTitleAndShapeTests
{
    [Theory]
    [InlineData("Mission Impossible Fallout", "Mission: Impossible - Fallout")]
    [InlineData("Spider-Man No Way Home", "Spider-Man: No Way Home")]
    [InlineData("Fight Club", "fight club")]
    [InlineData("The Office", "Office")]
    [InlineData("Office", "The Office")]
    [InlineData("Dune Part Two", "Dune: Part Two")]
    [InlineData("Amelie", "Amélie")]
    [InlineData("Law and Order", "Law & Order")]
    [InlineData("Star Trek Strange New Worlds", "Star Trek: Strange New Worlds")]
    public void TitlesThatDifferOnlyInCasePunctuationAccentsOrALeadingThe_Match(string wanted, string found) =>
        Assert.True(PosterNameParser.TitlesMatch(wanted, found));

    [Theory]
    [InlineData("Agents of SHIELD", "Live with Lil! AGENTS OF SHIELD")]    // OMDb's real wrong answer: it merely contains the words
    [InlineData("Agents of SHIELD", "Marvel's Agents of S.H.I.E.L.D.")]    // right show, but not provably so: no picture beats a wrong one
    [InlineData("The Thing", "The Thing from Another World")]
    [InlineData("Thelma", "Lma")]                                           // "The" is only dropped as a word, never from the start of "Thelma"
    [InlineData("Fargo", "Fargoth")]
    [InlineData("", "")]
    [InlineData("!!!", "???")]                                              // nothing left to compare is not a match
    public void TitlesThatAreNotTheSameTitle_DoNotMatch(string wanted, string found) =>
        Assert.False(PosterNameParser.TitlesMatch(wanted, found));

    private static byte[] Jpeg(int width, int height)
    {
        var bytes = new byte[40];
        bytes[0] = 0xFF; bytes[1] = 0xD8;                    // start of image
        bytes[2] = 0xFF; bytes[3] = 0xC0; bytes[5] = 17;     // start of frame, its length
        bytes[6] = 8;                                        // precision
        bytes[7] = (byte)(height >> 8); bytes[8] = (byte)height;
        bytes[9] = (byte)(width >> 8); bytes[10] = (byte)width;
        return bytes;
    }

    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[40];
        bytes[0] = 0x89; bytes[1] = 0x50; bytes[2] = 0x4E; bytes[3] = 0x47;
        bytes[18] = (byte)(width >> 8); bytes[19] = (byte)width;
        bytes[22] = (byte)(height >> 8); bytes[23] = (byte)height;
        return bytes;
    }

    [Fact]
    public void ThePictureShape_IsReadFromTheHeader_AndOnlyAPortraitIsAPoster()
    {
        Assert.True(ImageShape.TryGetSize(Jpeg(380, 562), out var width, out var height));
        Assert.Equal((380, 562), (width, height));
        Assert.True(ImageShape.IsPortrait(Jpeg(380, 562)));
        Assert.True(ImageShape.IsPortrait(Png(185, 278)));

        Assert.False(ImageShape.IsPortrait(Jpeg(300, 169)));     // the landscape still OMDb gave for the wrong show
        Assert.False(ImageShape.IsPortrait(Jpeg(500, 500)));     // square is not a poster
        Assert.False(ImageShape.IsPortrait(Jpeg(40, 60)));       // too small to be one
        Assert.False(ImageShape.IsPortrait(Png(300, 169)));
        Assert.False(ImageShape.IsPortrait(new byte[] { 9, 9, 9, 9 }));
        Assert.False(ImageShape.IsPortrait(Array.Empty<byte>()));
    }
}

/// <summary>OMDb as a poster source, and the choice and fallback between it and TMDb. Same fake network as the TMDb tests: made-up hosts, an
/// instant clock, no pause between lookups.</summary>
public partial class PosterServiceTests
{
    private const string Picture = "https://m.media-amazon.com/images/M/MV5BExample@._V1_SX300.jpg";

    private static readonly PosterSettings OmdbOnly = new() { Enabled = true, Source = PosterSource.Omdb, OmdbApiKey = "omdbkey1" };

    private static readonly PosterSettings OmdbThenTmdb = new()
    {
        Enabled = true, Source = PosterSource.Omdb, OmdbApiKey = "omdbkey1", TmdbApiKey = "test-key-123", FallbackToOther = true
    };

    private static readonly PosterSettings TmdbThenOmdb = new()
    {
        Enabled = true, Source = PosterSource.Tmdb, TmdbApiKey = "test-key-123", OmdbApiKey = "omdbkey1", FallbackToOther = true
    };

    private static byte[] PortraitJpeg(int width = 380, int height = 562)
    {
        var bytes = new byte[40];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xC0; bytes[5] = 17; bytes[6] = 8;
        bytes[7] = (byte)(height >> 8); bytes[8] = (byte)height; bytes[9] = (byte)(width >> 8); bytes[10] = (byte)width;
        return bytes;
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static string OmdbHit(string title, string type, string imdbId = "tt0137523", string poster = Picture) =>
        $"{{\"Title\":\"{title}\",\"Type\":\"{type}\",\"imdbID\":\"{imdbId}\",\"Poster\":\"{poster}\",\"Response\":\"True\"}}";

    private const string OmdbMiss = "{\"Response\":\"False\",\"Error\":\"Movie not found!\"}";

    /// <summary>Answers each fake host: OMDb with omdbBody, its picture host with a portrait, TMDb with a poster path, TMDb's image host with bytes.</summary>
    private void Serve(Func<HttpRequestMessage, HttpResponseMessage>? omdb = null, byte[]? picture = null, string? tmdbPoster = "/abcdef12345.jpg", HttpStatusCode pictureStatus = HttpStatusCode.OK)
    {
        _http.Respond = r => r.RequestUri!.Host switch
        {
            "omdb.test" => omdb?.Invoke(r) ?? Json(OmdbMiss),
            "m.media-amazon.com" => pictureStatus == HttpStatusCode.OK
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(picture ?? PortraitJpeg()) }
                : new HttpResponseMessage(pictureStatus),
            "tmdb.test" => tmdbPoster is null ? Results() : Results(tmdbPoster),
            "img.test" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 5, 5, 5, 5 }) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
    }

    private int Asked(string host) => _http.Requests.Count(u => u.Host == host);

    private static string OmdbFilePath(string imdbId) => Path.Combine(AppPaths.GetPostersDirectory(), "omdb", imdbId + ".jpg");

    [Fact]
    public async Task OmdbAMovie_IsLookedUpByTitleAndYear_ItsPictureIsSavedOnce_AndOnlyThoseThingsAreSent()
    {
        Serve(omdb: _ => Json(OmdbHit("Fight Club", "movie")));
        using var service = Service();
        var file = @"C:\Media\Movies\Fight Club (1999).mkv";

        Assert.Null(service.GetPosterId(OmdbOnly, file));                 // not known yet: queued, never blocks
        var id = await WaitForId(service, OmdbOnly, file);

        Assert.NotNull(id);
        var lookup = Assert.Single(_http.Requests, u => u.Host == "omdb.test");
        Assert.Equal("/", lookup.AbsolutePath);
        Assert.Contains("apikey=omdbkey1", lookup.Query);
        Assert.Contains("t=Fight%20Club", lookup.Query);
        Assert.Contains("y=1999", lookup.Query);
        Assert.Contains("type=movie", lookup.Query);
        Assert.DoesNotContain("Media", lookup.ToString());                // never a path
        Assert.DoesNotContain("mkv", lookup.ToString());                  // never the file name
        Assert.Equal(0, Asked("tmdb.test"));                              // the other source is not asked

        Assert.True(File.Exists(OmdbFilePath("tt0137523")));              // downloaded while looking up, so its shape could be checked

        var first = await service.GetImagePathAsync(OmdbOnly, id!, "w154");
        var second = await service.GetImagePathAsync(OmdbOnly, id!, "w92");      // one size: whichever is asked for
        Assert.Equal(OmdbFilePath("tt0137523"), first);
        Assert.Equal(first, second);
        Assert.Equal(1, Asked("m.media-amazon.com"));                     // once, not once per request

        Assert.Equal(id, service.GetPosterId(OmdbOnly, file));            // remembered
        Assert.Equal(1, Asked("omdb.test"));
    }

    [Fact]
    public async Task OmdbATvShow_UsesTheSeriesType_AndAShowWithoutAYearSendsNone()
    {
        Serve(omdb: _ => Json(OmdbHit("Breaking Bad", "series", "tt0903747")));
        using var service = Service();

        Assert.NotNull(await WaitForId(service, OmdbOnly, "Breaking Bad - S05E14 - Ozymandias.mkv"));

        var lookup = Assert.Single(_http.Requests, u => u.Host == "omdb.test");
        Assert.Contains("type=series", lookup.Query);
        Assert.DoesNotContain("&y=", lookup.Query);
        Assert.NotNull(service.GetPosterId(OmdbOnly, "Breaking Bad - S05E15 - Granite State.mkv"));     // the season costs one lookup
        Assert.Equal(1, Asked("omdb.test"));
    }

    [Theory]
    [InlineData("Live with Lil! AGENTS OF SHIELD", "series", "a different title that merely contains the words")]
    [InlineData("Agents of SHIELD", "movie", "the wrong kind of thing")]
    public async Task OmdbAnAnswerThatIsNotPlainlyTheSameThing_IsNoMatch_AndIsRemembered(string title, string type, string why)
    {
        Serve(omdb: _ => Json(OmdbHit(title, type)));
        using var service = Service();

        service.GetPosterId(OmdbOnly, "Agents of SHIELD - S01E01 - Pilot.mkv");
        await WaitFor(() => Asked("omdb.test") == 1);

        Assert.Null(service.GetPosterId(OmdbOnly, "Agents of SHIELD - S01E02.mkv"));
        await Task.Delay(100);
        Assert.True(Asked("omdb.test") == 1, why);                        // remembered as not found, not asked again
        Assert.Equal(0, Asked("m.media-amazon.com"));                     // and no picture was even downloaded for it
        Assert.False(File.Exists(OmdbFilePath("tt0137523")));
    }

    [Fact]
    public async Task OmdbALandscapePicture_IsRefused_AndNothingIsSavedForIt()
    {
        Serve(omdb: _ => Json(OmdbHit("Agents of SHIELD", "series")), picture: PortraitJpeg(300, 169));
        using var service = Service();

        service.GetPosterId(OmdbOnly, "Agents of SHIELD - S01E01 - Pilot.mkv");
        await WaitFor(() => Asked("m.media-amazon.com") == 1);
        await Task.Delay(100);

        Assert.Null(service.GetPosterId(OmdbOnly, "Agents of SHIELD - S01E01 - Pilot.mkv"));
        Assert.False(File.Exists(OmdbFilePath("tt0137523")));
    }

    [Theory]
    [InlineData("N/A")]                                                   // OMDb has the title but no picture
    [InlineData("http://m.media-amazon.com/images/x.jpg")]                // not https
    [InlineData("https://evil.test/images/x.jpg")]                        // not where OMDb's pictures live
    [InlineData("https://m.media-amazon.com.evil.test/x.jpg")]
    [InlineData("file:///C:/secret.jpg")]
    public async Task OmdbAPictureAddress_ThatIsNotAnAmazonHttpsAddress_IsNeverDownloaded(string poster)
    {
        Serve(omdb: _ => Json(OmdbHit("Fight Club", "movie", poster: poster)));
        using var service = Service();

        service.GetPosterId(OmdbOnly, "Fight Club (1999).mkv");
        await WaitFor(() => Asked("omdb.test") == 1);

        Assert.Single(_http.Requests);                                    // only the lookup
        Assert.Null(service.GetPosterId(OmdbOnly, "Fight Club (1999).mkv"));
    }

    [Fact]
    public async Task OmdbAnUnsafeImdbId_IsNotUsedAsAFileName()
    {
        Serve(omdb: _ => Json(OmdbHit("Fight Club", "movie", imdbId: "../../evil")));
        using var service = Service();

        service.GetPosterId(OmdbOnly, "Fight Club (1999).mkv");
        await WaitFor(() => Asked("omdb.test") == 1);

        Assert.Equal(0, Asked("m.media-amazon.com"));
        Assert.Null(service.GetPosterId(OmdbOnly, "Fight Club (1999).mkv"));
    }

    [Fact]
    public async Task WithoutTheTick_TheOtherSourceIsNeverAsked_EvenWhenItsKeyIsEntered()
    {
        Serve();    // OMDb knows nothing
        var settings = new PosterSettings { Enabled = true, Source = PosterSource.Omdb, OmdbApiKey = "omdbkey1", TmdbApiKey = "test-key-123", FallbackToOther = false };
        using var service = Service();

        service.GetPosterId(settings, "Fight Club (1999).mkv");
        await WaitFor(() => Asked("omdb.test") == 1);
        await Task.Delay(100);

        Assert.Equal(0, Asked("tmdb.test"));
    }

    [Fact]
    public async Task OmdbFirst_ThenTmdb_WhenOmdbHasNothing()
    {
        Serve();    // OMDb: not found; TMDb: a poster
        using var service = Service();

        var id = await WaitForId(service, OmdbThenTmdb, "Fight Club (1999).mkv");

        Assert.NotNull(id);
        Assert.Equal(1, Asked("omdb.test"));
        Assert.Equal(1, Asked("tmdb.test"));
        var picture = await service.GetImagePathAsync(OmdbThenTmdb, id!, "w154");    // it is TMDb's picture, at the size asked for
        Assert.Equal("/t/p/w154/abcdef12345.jpg", _http.Requests.Single(u => u.Host == "img.test").AbsolutePath);
        Assert.NotNull(picture);
    }

    [Fact]
    public async Task OmdbFirst_WhenOmdbHasIt_TmdbIsNeverAsked()
    {
        Serve(omdb: _ => Json(OmdbHit("Fight Club", "movie")));
        using var service = Service();

        Assert.NotNull(await WaitForId(service, OmdbThenTmdb, "Fight Club (1999).mkv"));

        Assert.Equal(0, Asked("tmdb.test"));
    }

    [Fact]
    public async Task TmdbFirst_ThenOmdb_WhenTmdbHasNothing()
    {
        Serve(omdb: _ => Json(OmdbHit("Fight Club", "movie")), tmdbPoster: null);
        using var service = Service();

        var id = await WaitForId(service, TmdbThenOmdb, "Fight Club (1999).mkv");

        Assert.NotNull(id);
        Assert.Equal(1, Asked("tmdb.test"));
        Assert.Equal(1, Asked("omdb.test"));
        Assert.Equal(OmdbFilePath("tt0137523"), await service.GetImagePathAsync(TmdbThenOmdb, id!, "w154"));
    }

    [Fact]
    public async Task OmdbBothSourcesHavingNothing_IsRememberedAsNotFound_AndNeitherIsAskedAgain()
    {
        Serve(tmdbPoster: null);
        using var service = Service();

        service.GetPosterId(OmdbThenTmdb, "Fight Club (1999).mkv");
        await WaitFor(() => Asked("omdb.test") == 1 && Asked("tmdb.test") == 1);

        Assert.Null(service.GetPosterId(OmdbThenTmdb, "Fight Club (1999).mkv"));
        await Task.Delay(100);
        Assert.Equal(1, Asked("omdb.test"));
        Assert.Equal(1, Asked("tmdb.test"));
    }

    [Fact]
    public async Task ANotFound_IsAskedAgain_WhenAnotherSourceIsTurnedOnAsABackup()
    {
        Serve(tmdbPoster: null);
        using var service = Service();

        service.GetPosterId(OmdbOnly, "Fight Club (1999).mkv");
        await WaitFor(() => Asked("omdb.test") == 1);
        await Task.Delay(100);
        Assert.Equal(0, Asked("tmdb.test"));

        Serve(tmdbPoster: "/abcdef12345.jpg");                              // the user enters a TMDb key and ticks the backup
        Assert.NotNull(await WaitForId(service, OmdbThenTmdb, "Fight Club (1999).mkv"));
        Assert.Equal(1, Asked("tmdb.test"));
        Assert.Equal(1, Asked("omdb.test"));                               // OMDb's "not found" was not asked again
    }

    [Fact]
    public async Task Switching_FromTmdbToOmdb_LooksTheTitleUpAgain_InsteadOfShowingTheOtherSourcesPicture()
    {
        Serve(omdb: _ => Json(OmdbHit("Fight Club", "movie")));
        using var service = Service();
        var id = await WaitForId(service, On, "Fight Club (1999).mkv");     // TMDb, as before
        Assert.NotNull(id);

        Assert.Null(service.GetPosterId(OmdbOnly, "Fight Club (1999).mkv"));  // TMDb's picture is not shown while OMDb is the chosen source
        Assert.Equal(id, await WaitForId(service, OmdbOnly, "Fight Club (1999).mkv"));
        Assert.Equal(OmdbFilePath("tt0137523"), await service.GetImagePathAsync(OmdbOnly, id!, "w154"));

        Assert.Equal(id, service.GetPosterId(OmdbThenTmdb, "Fight Club (1999).mkv"));     // an OMDb picture still counts when OMDb is in use
        Assert.Null(service.GetPosterId(On, "Fight Club (1999).mkv"));                    // ...and not when only TMDb is
    }

    [Fact]
    public async Task OmdbARejectedKey_IsNotRetried_TheCardSaysWhy_AndTheBackupStillAnswers()
    {
        Serve(omdb: _ => Json("{\"Response\":\"False\",\"Error\":\"Invalid API key!\"}", HttpStatusCode.Unauthorized));
        using var service = Service();

        var id = await WaitForId(service, OmdbThenTmdb, "Fight Club (1999).mkv");

        Assert.NotNull(id);                                                // TMDb answered
        Assert.Contains("OMDb rejected", service.GetCacheInfo().Problem);
        Assert.DoesNotContain("TMDb", service.GetCacheInfo().Problem);

        service.GetPosterId(OmdbThenTmdb, "Inception (2010).mkv");
        await Task.Delay(150);
        Assert.Equal(1, Asked("omdb.test"));                               // the refused key is not tried again
    }

    [Fact]
    public async Task OmdbARejectedKeyWithNoBackup_IsNotRememberedAsNotFound_SoAFixedKeyWorks()
    {
        Serve(omdb: _ => Json("{\"Response\":\"False\",\"Error\":\"Invalid API key!\"}", HttpStatusCode.Unauthorized));
        using var service = Service();

        service.GetPosterId(OmdbOnly, "Fight Club (1999).mkv");
        await WaitFor(() => service.GetCacheInfo().Problem is not null);

        Serve(omdb: _ => Json(OmdbHit("Fight Club", "movie")));
        var fixedKey = new PosterSettings { Enabled = true, Source = PosterSource.Omdb, OmdbApiKey = "fixedkey" };
        Assert.NotNull(await WaitForId(service, fixedKey, "Fight Club (1999).mkv"));
        Assert.Null(service.GetCacheInfo().Problem);                       // an answer clears the complaint
    }

    [Fact]
    public async Task OmdbTheDailyLimit_PausesLookupsForAWhile_AndIsNeverRememberedAsNotFound()
    {
        Serve(omdb: _ => Json("{\"Response\":\"False\",\"Error\":\"Request limit reached!\"}", HttpStatusCode.Unauthorized));
        using var service = Service();

        service.GetPosterId(OmdbOnly, "Fight Club (1999).mkv");
        await WaitFor(() => service.GetCacheInfo().Problem is not null);
        Assert.Contains("limit", service.GetCacheInfo().Problem);

        service.GetPosterId(OmdbOnly, "Inception (2010).mkv");
        await Task.Delay(150);
        Assert.Equal(1, Asked("omdb.test"));                               // paused: not asked again

        _clock.Now = _clock.Now.AddHours(2);
        Serve(omdb: _ => Json(OmdbHit("Fight Club", "movie")));
        Assert.NotNull(await WaitForId(service, OmdbOnly, "Fight Club (1999).mkv"));
    }

    [Fact]
    public async Task OmdbWhenItCannotBeReached_NothingIsRememberedAsNotFound()
    {
        _http.Respond = _ => throw new HttpRequestException("no network");
        using var service = Service();

        service.GetPosterId(OmdbOnly, "Fight Club (1999).mkv");
        await WaitFor(() => service.GetCacheInfo().Problem is not null);
        Assert.Contains("OMDb could not be reached", service.GetCacheInfo().Problem);

        _clock.Now = _clock.Now.AddMinutes(6);
        Serve(omdb: _ => Json(OmdbHit("Fight Club", "movie")));
        Assert.NotNull(await WaitForId(service, OmdbOnly, "Fight Club (1999).mkv"));
    }

    [Fact]
    public async Task OmdbAPictureThatCouldNotBeDownloaded_IsNotRememberedAsNotFound()
    {
        Serve(omdb: _ => Json(OmdbHit("Fight Club", "movie")), pictureStatus: HttpStatusCode.ServiceUnavailable);
        using var service = Service();

        service.GetPosterId(OmdbOnly, "Fight Club (1999).mkv");
        await WaitFor(() => Asked("m.media-amazon.com") == 1);
        await Task.Delay(100);

        _clock.Now = _clock.Now.AddMinutes(6);
        Serve(omdb: _ => Json(OmdbHit("Fight Club", "movie")));
        Assert.NotNull(await WaitForId(service, OmdbOnly, "Fight Club (1999).mkv"));
    }

    [Fact]
    public async Task OmdbASavedPicture_ThatWasCleanedUp_IsDownloadedAgain_AndAWithdrawnOneIsForgotten()
    {
        Serve(omdb: _ => Json(OmdbHit("Fight Club", "movie")));
        using var service = Service();
        var id = (await WaitForId(service, OmdbOnly, "Fight Club (1999).mkv"))!;

        File.Delete(OmdbFilePath("tt0137523"));
        Assert.Equal(OmdbFilePath("tt0137523"), await service.GetImagePathAsync(OmdbOnly, id, "w154"));
        Assert.True(File.Exists(OmdbFilePath("tt0137523")));
        Assert.Equal(2, Asked("m.media-amazon.com"));

        File.Delete(OmdbFilePath("tt0137523"));
        Serve(omdb: _ => Json(OmdbHit("Fight Club", "movie")), pictureStatus: HttpStatusCode.NotFound);
        Assert.Null(await service.GetImagePathAsync(OmdbOnly, id, "w154"));       // gone from the host: forgotten...
        service.GetPosterId(OmdbOnly, "Fight Club (1999).mkv");
        await WaitFor(() => Asked("omdb.test") == 2);                              // ...so the title is looked up again
    }

    [Fact]
    public async Task OmdbTwoTitlesWithTheSamePicture_ShareTheSavedFile_WithoutDownloadingItTwice()
    {
        Serve(omdb: _ => Json(OmdbHit("Breaking Bad", "series", "tt0903747")));
        using var service = Service();

        Assert.NotNull(await WaitForId(service, OmdbOnly, "Breaking Bad - S01E01.mkv"));
        Assert.NotNull(await WaitForId(service, OmdbOnly, "Breaking Bad (2008) - S01E01.mkv"));     // same show, with a year: another lookup

        Assert.Equal(2, Asked("omdb.test"));
        Assert.Equal(1, Asked("m.media-amazon.com"));                      // the picture was already saved
    }

    [Fact]
    public async Task OmdbTheTestButton_SaysWhatHappened()
    {
        using var service = Service();

        _http.Respond = _ => Json("{\"Title\":\"The Shawshank Redemption\",\"Response\":\"True\"}");
        Assert.True((await service.TestKeyAsync(PosterSource.Omdb, " omdbkey1 ")).Success);
        var request = _http.Requests.Last();
        Assert.Equal("omdb.test", request.Host);
        Assert.Contains("apikey=omdbkey1&", request.Query);               // trimmed
        Assert.DoesNotContain("tmdb", request.Host);

        _http.Respond = _ => Json("{\"Response\":\"False\",\"Error\":\"Invalid API key!\"}", HttpStatusCode.Unauthorized);
        var rejected = await service.TestKeyAsync(PosterSource.Omdb, "bad");
        Assert.False(rejected.Success);
        Assert.Contains("activation link", rejected.Message);             // the usual mistake is named

        _http.Respond = _ => Json("{\"Response\":\"False\",\"Error\":\"Request limit reached!\"}", HttpStatusCode.Unauthorized);
        var limited = await service.TestKeyAsync(PosterSource.Omdb, "x");
        Assert.False(limited.Success);
        Assert.Contains("daily limit", limited.Message);

        _http.Respond = _ => throw new HttpRequestException("no network");
        var offline = await service.TestKeyAsync(PosterSource.Omdb, "x");
        Assert.False(offline.Success);
        Assert.Contains("Could not reach OMDb", offline.Message);

        var empty = await service.TestKeyAsync(PosterSource.Omdb, "  ");
        Assert.False(empty.Success);
        Assert.Contains("OMDb", empty.Message);
    }

    [Fact]
    public async Task OmdbThePicturesAreCounted_Cleaned_AndCleared_LikeTheOthers()
    {
        Serve(omdb: _ => Json(OmdbHit("Fight Club", "movie")));
        using var service = Service();
        Assert.NotNull(await WaitForId(service, OmdbOnly, "Fight Club (1999).mkv"));

        var info = service.GetCacheInfo();
        Assert.Equal(1, info.Posters);
        Assert.True(info.Bytes > 0);

        var stray = Path.Combine(AppPaths.GetPostersDirectory(), "omdb", "tt9999999.jpg");     // nothing points at it
        await File.WriteAllBytesAsync(stray, new byte[] { 1 });
        Assert.Equal(1, service.CleanUp(30));
        Assert.False(File.Exists(stray));
        Assert.True(File.Exists(OmdbFilePath("tt0137523")));                                   // the one in use stays

        _clock.Now = _clock.Now.AddDays(40);
        Assert.Equal(1, service.CleanUp(30));                                                  // not seen for 40 days: removed
        Assert.False(File.Exists(OmdbFilePath("tt0137523")));

        var id = service.GetPosterId(OmdbOnly, "Fight Club (1999).mkv");                          // still remembered
        Assert.NotNull(id);
        Assert.NotNull(await service.GetImagePathAsync(OmdbOnly, id!, "w154"));                    // its picture is downloaded again
        Assert.Equal(1, service.ClearCache());
        Assert.False(Directory.Exists(Path.Combine(AppPaths.GetPostersDirectory(), "omdb")));
        Assert.Equal(0, service.GetCacheInfo().Posters);
    }

    [Fact]
    public void AnOmdbChoice_WithoutItsKey_AsksNobody()
    {
        using var service = Service();

        Assert.Null(service.GetPosterId(new PosterSettings { Enabled = true, Source = PosterSource.Omdb, OmdbApiKey = "  ", TmdbApiKey = "test-key-123", FallbackToOther = true }, "Fight Club (1999).mkv"));
        Assert.Null(service.GetPosterId(new PosterSettings { Enabled = false, Source = PosterSource.Omdb, OmdbApiKey = "omdbkey1" }, "Fight Club (1999).mkv"));

        Assert.Empty(_http.Requests);
    }
}
