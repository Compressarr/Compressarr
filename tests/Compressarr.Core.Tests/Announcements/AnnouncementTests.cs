using System.Net;
using System.Text;
using Compressarr.Core.Announcements;
using Compressarr.Core.Config;

namespace Compressarr.Core.Tests.Announcements;

public class AnnouncementReaderTests
{
    private static string File(params string[] entries) => $$"""{ "schema": 1, "announcements": [ {{string.Join(",", entries)}} ] }""";

    private static string Entry(string id = "a1", string date = "2026-10-14", string title = "Hello", string extra = "") =>
        $$"""{ "id": "{{id}}", "date": "{{date}}", "title": "{{title}}"{{(extra.Length > 0 ? "," + extra : "")}} }""";

    [Fact]
    public void AGoodFile_IsRead_WithDefaultsForTheOptionalFields()
    {
        var result = AnnouncementReader.Parse(File(Entry(extra: """ "text": "Body", "kind": "beta", "important": true """)));

        Assert.Equal(AnnouncementFileStatus.Ok, result.Status);
        var a = Assert.Single(result.Items);
        Assert.Equal(("a1", new DateOnly(2026, 10, 14), "beta", "Hello", "Body", true), (a.Id, a.Date, a.Kind, a.Title, a.Text, a.Important));
        Assert.Null(a.Link);
        Assert.Equal("notice", Assert.Single(AnnouncementReader.Parse(File(Entry())).Items).Kind); // no kind -> notice
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{ \"announcements\": [] }")]                       // no schema
    [InlineData("{ \"schema\": 1, \"announcements\": \"oops\" }")]   // wrong shape
    public void ADamagedFile_IsInvalid_AndShowsNothing(string json)
    {
        var result = AnnouncementReader.Parse(json);

        Assert.Equal(AnnouncementFileStatus.Invalid, result.Status);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void ANewerFormat_IsNotGuessedAt()
    {
        var result = AnnouncementReader.Parse("""{ "schema": 2, "announcements": [ { "id": "x", "date": "2026-10-14", "title": "t" } ] }""");

        Assert.Equal(AnnouncementFileStatus.UnknownSchema, result.Status);
        Assert.Empty(result.Items);
    }

    [Fact]
    public void AnEntryThatBreaksARule_IsSkippedOnItsOwn_AndNeverHidesTheGoodOnes()
    {
        var long121 = new string('t', 121);
        var result = AnnouncementReader.Parse(File(
            Entry("good1"),
            Entry("no-date-format", date: "14/10/2026"),
            Entry("long-title", title: long121),
            Entry("bad-kind", extra: """ "kind": "advert" """),
            Entry("bad-version", extra: """ "maxAppVersion": "soon" """),
            Entry("bad-expiry", extra: """ "expires": "tomorrow" """),
            """{ "date": "2026-10-14", "title": "no id" }""",
            "42",
            Entry("good1", title: "same id again"),
            Entry("good2")));

        Assert.Equal(AnnouncementFileStatus.Ok, result.Status);
        Assert.Equal(new[] { "good1", "good2" }, result.Items.Select(i => i.Id).ToArray());
        Assert.Equal("Hello", result.Items[0].Title); // a repeated id keeps the first
    }

    [Fact]
    public void TheFileIsSizeLimited_AndOnlyTheFirstFiftyEntriesAreRead()
    {
        var huge = File(Entry(extra: $$""" "text": "{{new string('x', AnnouncementReader.MaxFileBytes)}}" """));
        Assert.Equal(AnnouncementFileStatus.Invalid, AnnouncementReader.Parse(huge).Status);

        var many = File(Enumerable.Range(0, 80).Select(i => Entry($"id{i}")).ToArray());
        Assert.Equal(AnnouncementReader.MaxEntries, AnnouncementReader.Parse(many).Items.Count);
    }

    [Theory]
    [InlineData("https://github.com/MrWizardCT/Compressarr/releases/tag/v2.2.0", true)]
    [InlineData("https://github.com/MrWizardCT", true)]
    [InlineData("https://compressarr.tv", true)]
    [InlineData("https://www.compressarr.tv/news", true)]
    [InlineData("http://compressarr.tv", false)]                              // not https
    [InlineData("https://github.com/SomeoneElse/Compressarr", false)]          // not the project's page
    [InlineData("https://github.com/MrWizardCT-evil/x", false)]                // a look-alike path
    [InlineData("https://compressarr.tv.evil.example/", false)]               // a look-alike host
    [InlineData("https://evilcompressarr.tv/", false)]
    [InlineData("https://user:pass@compressarr.tv/", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    public void ALinkIsOnlyKept_WhenItPointsAtTheProject(string link, bool allowed)
    {
        Assert.Equal(allowed, AnnouncementReader.LinkAllowed(link));

        var item = Assert.Single(AnnouncementReader.Parse(File(Entry(extra: $$""" "link": "{{link}}" """))).Items); // a refused link keeps the message
        Assert.Equal(allowed ? link : null, item.Link);
    }
}

public class AppVersionTests
{
    [Theory]
    [InlineData("2.2.0-beta.5", "2.2.0", -1)]
    [InlineData("2.2.0", "2.2.0-beta.5", 1)]
    [InlineData("2.2.0-beta.5", "2.2.0-beta.6", -1)]
    [InlineData("2.2.0-beta.10", "2.2.0-beta.9", 1)]      // numbers, not text
    [InlineData("2.2.0-beta.5", "2.2.0-rc.1", -1)]
    [InlineData("2.1.9", "2.2.0-beta.1", -1)]
    [InlineData("2.2", "2.2.0", 0)]
    [InlineData("2.2.0+abc123", "2.2.0", 0)]              // build metadata is ignored
    [InlineData("v2.2.0", "2.2.0", 0)]
    [InlineData("2.10.0", "2.9.0", 1)]
    public void VersionsCompareTheWayAnnouncementsExpect(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(AppVersion.Parse(a)!.CompareTo(AppVersion.Parse(b))));

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("2")]
    [InlineData("2.x.0")]
    [InlineData("2.2.0-")]
    public void ATextThatIsNotAVersion_IsNull(string text) => Assert.Null(AppVersion.Parse(text));
}

public class AnnouncementRulesTests
{
    private static Announcement Make(string date = "2026-10-14", string? min = null, string? max = null, string? expires = null) =>
        new("a", DateOnly.Parse(date), "notice", "t", "", null, false, min, max, expires is null ? null : DateOnly.Parse(expires));

    [Fact]
    public void AMessage_IsListedFromItsDateForThirtyDays_AndNotBefore()
    {
        var a = Make("2026-10-14");

        Assert.False(AnnouncementRules.InWindow(a, new DateOnly(2026, 10, 13)));   // scheduled, not yet dated
        Assert.True(AnnouncementRules.InWindow(a, new DateOnly(2026, 10, 14)));
        Assert.True(AnnouncementRules.InWindow(a, new DateOnly(2026, 11, 13)));    // day 30
        Assert.False(AnnouncementRules.InWindow(a, new DateOnly(2026, 11, 14)));   // day 31
    }

    [Fact]
    public void AnExpiredMessage_IsHidden_EvenInsideTheWindow()
    {
        var a = Make("2026-10-14", expires: "2026-10-20");

        Assert.True(AnnouncementRules.InWindow(a, new DateOnly(2026, 10, 20)));
        Assert.False(AnnouncementRules.InWindow(a, new DateOnly(2026, 10, 21)));
    }

    [Fact]
    public void TheVersionBounds_AreInclusive_AndAPreReleaseIsBelowItsRelease()
    {
        var beta6News = Make(max: "2.2.0-beta.5");    // "beta.6 is out" - for people still on beta.5 or older
        var forTwoTwo = Make(min: "2.2.0");

        Assert.True(AnnouncementRules.InVersionRange(beta6News, AppVersion.Parse("2.2.0-beta.5")));
        Assert.True(AnnouncementRules.InVersionRange(beta6News, AppVersion.Parse("2.1.9")));
        Assert.False(AnnouncementRules.InVersionRange(beta6News, AppVersion.Parse("2.2.0-beta.6")));
        Assert.False(AnnouncementRules.InVersionRange(forTwoTwo, AppVersion.Parse("2.2.0-beta.6")));
        Assert.True(AnnouncementRules.InVersionRange(forTwoTwo, AppVersion.Parse("2.2.0")));
        Assert.True(AnnouncementRules.InVersionRange(beta6News, null));              // an unreadable own version sees everything
    }
}

/// <summary>Points AppPaths at a throwaway folder for the life of a test, so nothing here touches this machine's real Compressarr data.
/// AppPaths.TestOverrideAppDataDirectory is one static, so every class that sets it is in the "AppDataOverride" collection and never
/// runs in parallel with another.</summary>
[CollectionDefinition("AppDataOverride")]
public sealed class AppDataOverrideCollection;

[Collection("AppDataOverride")]
public abstract class AnnouncementAppDataBase : IDisposable
{
    protected string AppData { get; } = Directory.CreateTempSubdirectory("compressarr-appdata-tests-").FullName;

    protected AnnouncementAppDataBase() => AppPaths.TestOverrideAppDataDirectory = AppData;

    public void Dispose()
    {
        AppPaths.TestOverrideAppDataDirectory = null;
        try { Directory.Delete(AppData, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }
}

internal sealed class FakeClock : TimeProvider
{
    public DateTimeOffset Now { get; set; }
    public FakeClock(DateTimeOffset now) => Now = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

public class AnnouncementServiceTests : AnnouncementAppDataBase
{
    private const string Url = "https://example.invalid/announcements.json";

    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public Factory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private readonly Handler _http = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero));
    private readonly JsonConfigStore _config = new();
    private readonly JsonAnnouncementStateStore _state = new();
    private string _version = "2.2.0-beta.5";

    private AnnouncementService Service() =>
        new(new Factory(_http), _config, _state, _clock, TimeSpan.FromHours(24), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(5), Url, () => AppVersion.Parse(_version));

    private static HttpResponseMessage Json(string body, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
        if (etag is not null) response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return response;
    }

    private static string FileWith(params (string Id, string Date)[] items) =>
        $$"""{ "schema": 1, "announcements": [ {{string.Join(",", items.Select(i => $$"""{ "id": "{{i.Id}}", "date": "{{i.Date}}", "title": "Title {{i.Id}}" }"""))}} ] }""";

    private void TurnOff()
    {
        var config = _config.Load(AppPaths.GetConfigFilePath());
        config.Announcements.Enabled = false;
        _config.Save(config, AppPaths.GetConfigFilePath());
    }

    [Fact]
    public async Task ACheck_StoresTheFile_AndSendsNothingAboutTheUser()
    {
        _http.Respond = _ => Json(FileWith(("a", "2026-10-15")), "\"v1\"");

        Assert.True(await Service().CheckNowAsync());

        var request = Assert.Single(_http.Requests);
        Assert.Equal(Url, request.RequestUri!.ToString());
        Assert.Equal("Compressarr-Announcements", request.Headers.UserAgent.ToString());   // no version number in it
        Assert.Null(request.Headers.Authorization);
        var state = _state.Load();
        Assert.Equal("\"v1\"", state.ETag);
        Assert.NotNull(state.CachedJson);
        Assert.Equal("2026-10-15", state.Baseline);
    }

    [Fact]
    public async Task AFreshInstall_ShowsOldNewsAsRead_AndTheFirstNewPostAsUnread()
    {
        _http.Respond = _ => Json(FileWith(("old", "2026-10-10"), ("today", "2026-10-15")));
        var service = Service();
        await service.CheckNowAsync();

        var first = service.GetView();
        Assert.Equal(1, first.Unread);   // today's message is not "before the first look"
        Assert.True(first.Items.Single(i => i.Id == "old").Read);
        Assert.False(first.Items.Single(i => i.Id == "today").Read);

        _clock.Now = _clock.Now.AddDays(2);
        _http.Respond = _ => Json(FileWith(("old", "2026-10-10"), ("today", "2026-10-15"), ("later", "2026-10-17")));
        await service.CheckNowAsync();

        var second = service.GetView();
        Assert.Equal(new[] { "later", "today", "old" }.OrderBy(x => x).ToArray(), second.Items.Select(i => i.Id).OrderBy(x => x).ToArray());
        Assert.Equal(2, second.Unread);
    }

    [Fact]
    public async Task MarkingRead_ClearsTheBell_ForThoseMessagesOnly()
    {
        _http.Respond = _ => Json(FileWith(("a", "2026-10-15"), ("b", "2026-10-15")));
        var service = Service();
        await service.CheckNowAsync();
        Assert.Equal(2, service.GetView().Unread);

        service.MarkRead(new[] { "a" });
        Assert.Equal(1, service.GetView().Unread);

        service.MarkRead(null);   // everything on the list
        Assert.Equal(0, service.GetView().Unread);
    }

    [Fact]
    public async Task AMessage_OlderThanThirtyDays_LeavesTheList()
    {
        _http.Respond = _ => Json(FileWith(("a", "2026-10-15")));
        var service = Service();
        await service.CheckNowAsync();

        _clock.Now = _clock.Now.AddDays(30);
        Assert.Single(service.GetView().Items);
        _clock.Now = _clock.Now.AddDays(1);
        Assert.Empty(service.GetView().Items);
    }

    [Fact]
    public async Task TheVersionBounds_DecideWhoSeesAMessage()
    {
        _http.Respond = _ => Json("""{ "schema": 1, "announcements": [ { "id": "b6", "date": "2026-10-15", "title": "beta 6 is out", "maxAppVersion": "2.2.0-beta.5" } ] }""");
        var service = Service();
        await service.CheckNowAsync();

        Assert.Single(service.GetView().Items);
        _version = "2.2.0-beta.6";
        Assert.Empty(service.GetView().Items);
    }

    [Fact]
    public async Task AnUnchangedFile_IsAskedForWithItsETag_AndKeepsWhatWasStored()
    {
        _http.Respond = _ => Json(FileWith(("a", "2026-10-15")), "\"v1\"");
        var service = Service();
        await service.CheckNowAsync();

        _http.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotModified);
        Assert.True(await service.CheckNowAsync());

        Assert.Equal("\"v1\"", _http.Requests[1].Headers.IfNoneMatch.ToString());
        Assert.Single(service.GetView().Items);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AFailedCheck_KeepsTheLastGoodCopy_AndIsQuiet(HttpStatusCode status)
    {
        _http.Respond = _ => Json(FileWith(("a", "2026-10-15")));
        var service = Service();
        await service.CheckNowAsync();

        _http.Respond = _ => new HttpResponseMessage(status);
        Assert.False(await service.CheckNowAsync());

        Assert.Single(service.GetView().Items);
    }

    [Fact]
    public async Task ABadFile_NeverReplacesAGoodOne()
    {
        _http.Respond = _ => Json(FileWith(("a", "2026-10-15")));
        var service = Service();
        await service.CheckNowAsync();

        _http.Respond = _ => Json("<html>rate limited</html>");
        Assert.False(await service.CheckNowAsync());
        _http.Respond = _ => throw new HttpRequestException("offline");
        Assert.False(await service.CheckNowAsync());

        Assert.Equal("a", Assert.Single(service.GetView().Items).Id);
    }

    [Fact]
    public async Task ANewerFileFormat_ShowsNothingNewAndSaysAnUpdateIsNeeded()
    {
        _http.Respond = _ => Json("""{ "schema": 9, "announcements": [] }""");
        var service = Service();

        Assert.True(await service.CheckNowAsync());

        Assert.True(service.GetView().NeedsNewerApp);
    }

    [Fact]
    public async Task AnOversizedDownload_IsRefused()
    {
        _http.Respond = _ => Json(new string('x', AnnouncementReader.MaxFileBytes + 10));

        Assert.False(await Service().CheckNowAsync());
        Assert.Null(_state.Load().CachedJson);
    }

    [Fact]
    public async Task WhenOff_NoRequestIsEverMade_AndTheBellHasNothing()
    {
        _http.Respond = _ => Json(FileWith(("a", "2026-10-15")));
        var service = Service();
        await service.CheckNowAsync();
        TurnOff();

        Assert.False(await service.CheckNowAsync());

        Assert.Single(_http.Requests);                 // only the one made while it was on
        var view = service.GetView();
        Assert.False(view.Enabled);
        Assert.Empty(view.Items);
        Assert.Equal(0, view.Unread);
    }

    [Fact]
    public async Task TheLoop_ChecksAtStart_ThenOnItsSchedule_AndOnlyWhileOn()
    {
        _http.Respond = _ => Json(FileWith(("a", "2026-10-15")));
        var service = new AnnouncementService(new Factory(_http), _config, _state, TimeProvider.System, TimeSpan.FromHours(24), TimeSpan.FromHours(1), TimeSpan.FromMilliseconds(20), Url, () => null);

        service.Start();
        for (var i = 0; i < 100 && _http.Requests.Count == 0; i++) await Task.Delay(20);
        await Task.Delay(150);   // many ticks go by; the next check is a day away
        await service.StopAsync();

        Assert.Single(_http.Requests);
    }

    [Fact]
    public async Task ADamagedStateFile_OnlyCostsTheReadMarks()
    {
        File.WriteAllText(AppPaths.GetAnnouncementsFilePath(), "{ not json");

        Assert.Empty(Service().GetView().Items);
        _http.Respond = _ => Json(FileWith(("a", "2026-10-15")));
        Assert.True(await Service().CheckNowAsync());
    }
}
