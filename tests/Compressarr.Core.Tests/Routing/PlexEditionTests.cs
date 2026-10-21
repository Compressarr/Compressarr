using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Posters;
using Compressarr.Core.Routing;

namespace Compressarr.Core.Tests.Routing;

/// <summary>Plex's <c>{edition-Name}</c> tag (Plex Pass). Movies: the tag stays in the file name and every edition of a movie shares the one
/// "Title (Year)" folder. TV shows: Plex puts the edition on the show's top-level folder, so a tag after the show name and year (before the
/// season and episode) becomes part of the show folder exactly as written; Plex has no episode-level edition, so a tag after the episode
/// stays in the file name only. Anything that is not exactly Plex's form is left as it always was.</summary>
public class PlexEditionTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("compressarr-edition-tests-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string CreateSourceFile(string name)
    {
        var path = Path.Combine(_tempDir, "source", name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "data");
        return path;
    }

    private string Base(string kind)
    {
        var path = Path.Combine(_tempDir, kind);
        Directory.CreateDirectory(path);
        return path;
    }

    // ---- TV ---------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Babylon 5 (1994) {edition-Bluray Remaster} - S01E01 - Midnight on the Firing Line.mkv", "Babylon 5 (1994) {edition-Bluray Remaster}")]
    [InlineData("Babylon 5 (1994) {edition-Bluray Remaster} S01E01.mkv", "Babylon 5 (1994) {edition-Bluray Remaster}")]
    [InlineData("The Wire {edition-4x3} - S01E01.mkv", "The Wire {edition-4x3}")]                         // an "x" between digits in the name is not an episode marker
    [InlineData("The Office (US) (2005) {tmdb-2316} {edition-Superfan} - S03E10.mkv", "The Office (US) (2005) {tmdb-2316} {edition-Superfan}")]
    [InlineData("Babylon 5 (1994) - S01E01 - Midnight.mkv", "Babylon 5 (1994)")]                          // no edition: as always
    public void ATvShowsEditionTag_BeforeTheEpisode_IsPartOfItsShowName(string fileName, string showName)
    {
        var info = ContentClassifier.GetEpisodeInfo(fileName);

        Assert.True(info.HasSeasonAndEpisode);
        Assert.Equal(showName, info.ShowName);
    }

    [Fact]
    public void MoveTvFile_AnEditionOnTheShow_GoesIntoTheEditionsShowFolder_KeptExactlyAsWritten()
    {
        var router = new FileRouter();
        var name = "Babylon 5 (1994) {edition-Bluray Remaster} - S01E01 - Midnight on the Firing Line.mkv";
        var source = CreateSourceFile(name);
        var tv = Base("TV");

        var destination = router.MoveTvFile(source, name, tv)!;

        Assert.Equal(Path.Combine(tv, "Babylon 5 (1994) {edition-Bluray Remaster}", "Season 01", name), destination);
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public void MoveTvFile_TheShowAndItsEdition_AreTwoFolders_SideBySide_AsPlexWantsThem()
    {
        var router = new FileRouter();
        var plain = "Babylon 5 (1994) - S01E01 - Midnight.mkv";
        var remaster = "Babylon 5 (1994) {edition-Bluray Remaster} - S01E01 - Midnight.mkv";
        var tv = Base("TV");

        router.MoveTvFile(CreateSourceFile(plain), plain, tv);
        router.MoveTvFile(CreateSourceFile(remaster), remaster, tv);

        Assert.True(File.Exists(Path.Combine(tv, "Babylon 5 (1994)", "Season 01", plain)));
        Assert.True(File.Exists(Path.Combine(tv, "Babylon 5 (1994) {edition-Bluray Remaster}", "Season 01", remaster)));
    }

    [Fact]
    public void MoveTvFile_AnEditionTagAfterTheEpisode_StaysInTheFileName_AndNeverMakesAFolder()
    {
        // Plex has no episode-level edition: only the show folder carries one.
        var router = new FileRouter();
        var name = "Babylon 5 (1994) - S01E01 - Midnight {edition-Bluray Remaster}.mkv";
        var tv = Base("TV");

        var destination = router.MoveTvFile(CreateSourceFile(name), name, tv)!;

        Assert.Equal(Path.Combine(tv, "Babylon 5 (1994)", "Season 01", name), destination);
    }

    [Theory]
    [InlineData("Babylon 5 (1994) {edition Bluray Remaster} - S01E01.mkv", "Babylon 5 (1994) {edition Bluray Remaster}")]       // no hyphen: not Plex's form
    [InlineData("Babylon 5 (1994) [edition-Bluray Remaster] - S01E01.mkv", "Babylon 5 (1994) [edition-Bluray Remaster]")]         // wrong brackets
    [InlineData("Babylon 5 {edition-Bluray Remaster} (1994) - S01E01.mkv", "Babylon 5 {edition-Bluray Remaster} (1994)")]         // before the year
    public void MoveTvFile_ANameThatIsNotPlexsForm_IsFiledExactlyAsItAlwaysWas(string name, string folder)
    {
        var router = new FileRouter();
        var tv = Base("TV");

        var destination = router.MoveTvFile(CreateSourceFile(name), name, tv)!;

        Assert.Equal(Path.Combine(tv, folder, "Season 01", name), destination);
    }

    // ---- an edition carried only by the source show folder -------------------------------------------------------------------

    private static readonly string In = Path.Combine(Path.GetTempPath(), "Input");

    private static string Src(params string[] parts) => Path.Combine(new[] { In }.Concat(parts).ToArray());

    [Theory]
    [InlineData("Babylon 5 (1994) {edition-Bluray Remaster}", "Season 01", "Babylon 5 (1994) - S01E01 - Midnight.mkv", "Babylon 5 (1994)", "{edition-Bluray Remaster}")]
    [InlineData("Babylon 5 (1994) {edition-Bluray Remaster}", "Season 1", "S01E01.mkv", "Babylon 5 (1994)", "{edition-Bluray Remaster}")]
    [InlineData("Babylon 5 (1994) {edition-Bluray Remaster}", "Specials", "x.mkv", "Babylon 5 (1994)", "{edition-Bluray Remaster}")]
    [InlineData("Babylon.5.(1994) {edition-Bluray Remaster}", "Season 01", "x.mkv", "Babylon 5 (1994)", "{edition-Bluray Remaster}")]                // same show, punctuated differently
    [InlineData("Babylon 5 (1994) {tmdb-3} {edition-Bluray Remaster}", "Season 01", "x.mkv", "Babylon 5 (1994)", "{edition-Bluray Remaster}")]   // with an id tag too
    [InlineData("Babylon 5 (1994) {EDITION-Bluray Remaster}", "Season 01", "x.mkv", "Babylon 5 (1994)", "{EDITION-Bluray Remaster}")]            // kept as written
    public void TheSourceShowFolder_WithAnEdition_GivesItsTag(string showFolder, string seasonFolder, string file, string showName, string expected) =>
        Assert.Equal(expected, PlexTags.EditionOfSourceShowFolder(Src(showFolder, seasonFolder, file), In, showName));

    [Fact]
    public void TheSourceShowFolder_ACaseWithNoSeasonFolder_IsTheFolderHoldingTheFile() =>
        Assert.Equal("{edition-Bluray Remaster}", PlexTags.EditionOfSourceShowFolder(Src("Babylon 5 (1994) {edition-Bluray Remaster}", "x.mkv"), In, "Babylon 5 (1994)"));

    [Theory]
    [InlineData("Babylon 5 (1994)", "Season 01", "Babylon 5 (1994)")]                                              // no tag on the folder
    [InlineData("Babylon 5 (1994) {edition Bluray Remaster}", "Season 01", "Babylon 5 (1994)")]                   // not Plex's form
    [InlineData("Babylon 5 (1994) {edition-Bluray Remaster}", "Season 01", "Crusade (1999)")]                     // a different show: never taken for this one
    [InlineData("Babylon 5 {edition-Bluray Remaster} (1994)", "Season 01", "Babylon 5 (1994)")]                   // tag before the year
    [InlineData("Collection {edition-Archive}", "Season 01", "Babylon 5 (1994)")]                                 // a folder that merely holds shows
    public void TheSourceShowFolder_NoTagThatBelongsToThisShow_GivesNothing(string showFolder, string seasonFolder, string showName) =>
        Assert.Null(PlexTags.EditionOfSourceShowFolder(Src(showFolder, seasonFolder, "x.mkv"), In, showName));

    [Fact]
    public void TheSourceShowFolder_ATagAboveTheShowFolder_IsNotUsed()
    {
        // Input\Collection {edition-Archive}\Babylon 5 (1994)\Season 01\x.mkv: the show folder is Babylon 5 (1994), which has no tag.
        Assert.Null(PlexTags.EditionOfSourceShowFolder(Src("Collection {edition-Archive}", "Babylon 5 (1994)", "Season 01", "x.mkv"), In, "Babylon 5 (1994)"));
    }

    [Fact]
    public void TheSourceShowFolder_TheInputFolderItself_IsNeverTheShowFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "Babylon 5 (1994) {edition-Bluray Remaster}");

        Assert.Null(PlexTags.EditionOfSourceShowFolder(Path.Combine(root, "Season 01", "x.mkv"), root, "Babylon 5 (1994)"));
        Assert.Null(PlexTags.EditionOfSourceShowFolder(Path.Combine(root, "x.mkv"), root, "Babylon 5 (1994)"));
    }

    [Fact]
    public void MoveTvFile_AnEditionFromTheSourceFolder_IsAddedToTheShowFolder_AndTheFileKeepsItsName()
    {
        var router = new FileRouter();
        var name = "Babylon 5 (1994) - S01E01 - Midnight.mkv";
        var tv = Base("TV");

        var destination = router.MoveTvFile(CreateSourceFile(name), name, tv, DestinationCollisionMode.Overwrite, "{edition-Bluray Remaster}")!;

        Assert.Equal(Path.Combine(tv, "Babylon 5 (1994) {edition-Bluray Remaster}", "Season 01", name), destination);
    }

    [Fact]
    public void MoveTvFile_AnEditionAlreadyInTheFileName_WinsOverTheSourceFolders()
    {
        var router = new FileRouter();
        var name = "Babylon 5 (1994) {edition-Director's Cut} - S01E01.mkv";
        var tv = Base("TV");

        var destination = router.MoveTvFile(CreateSourceFile(name), name, tv, DestinationCollisionMode.Overwrite, "{edition-Bluray Remaster}")!;

        Assert.Equal(Path.Combine(tv, "Babylon 5 (1994) {edition-Director's Cut}", "Season 01", name), destination);
    }

    [Fact]
    public void RouteFile_AMovie_IgnoresASourceShowEdition()
    {
        var router = new FileRouter();
        var name = "Blade Runner (1982).mkv";
        var movies = Base("Movies");

        var destination = router.RouteFile(CreateSourceFile(name), name, isTv: false, tvShowBasePath: Base("TV"), movieBasePath: movies, moveFiles: true,
            DestinationCollisionMode.Overwrite, "{edition-Final Cut}")!;

        Assert.Equal(Path.Combine(movies, "Blade Runner (1982)", name), destination);
    }

    // ---- movies -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void MoveMovieFile_EveryEditionOfAMovie_SharesTheOneMovieFolder_WithTheTagInTheFileName()
    {
        var router = new FileRouter();
        var plain = "Blade Runner (1982).mkv";
        var directors = "Blade Runner (1982) {edition-Director's Cut}.mkv";
        var final = "Blade Runner (1982) {imdb-tt0083658} {edition-Final Cut}.mkv";
        var movies = Base("Movies");

        router.MoveMovieFile(CreateSourceFile(plain), plain, movies);
        router.MoveMovieFile(CreateSourceFile(directors), directors, movies);
        router.MoveMovieFile(CreateSourceFile(final), final, movies);

        var folder = Path.Combine(movies, "Blade Runner (1982)");
        Assert.Equal(new[] { Path.Combine(folder, plain), Path.Combine(folder, directors), Path.Combine(folder, final) }.Order(), Directory.GetFiles(folder).Order());
        Assert.Single(Directory.GetDirectories(movies));
    }

    [Fact]
    public void MoveMovieFile_AMovieWithNoYear_IsFiledExactlyAsItAlwaysWas_EvenWithAnEditionTag()
    {
        var router = new FileRouter();
        var name = "Blade Runner {edition-Final Cut}.mkv";
        var movies = Base("Movies");

        var destination = router.MoveMovieFile(CreateSourceFile(name), name, movies)!;

        Assert.Equal(Path.Combine(movies, "Blade Runner {edition-Final Cut}", name), destination);
    }

    [Theory]
    [InlineData("Blade Runner (1982) {edition-4x3}.mkv")]               // an edition named for its aspect ratio is not season 4, episode 3
    [InlineData("Blade Runner (1982) {edition-1x2 Cut}.mkv")]
    [InlineData("Blade Runner (1982) {edition-S01E01}.mkv")]
    public void AnEditionName_IsNeverReadAsASeasonAndEpisode(string fileName)
    {
        Assert.False(ContentClassifier.IsTvFile(fileName));
        Assert.Equal("Blade Runner (1982)", ContentClassifier.GetMovieFolderName(fileName));
    }

    [Fact]
    public void MoveMovieFile_AnEditionNamedForItsAspectRatio_StaysAMovie()
    {
        var router = new FileRouter();
        var name = "Blade Runner (1982) {edition-4x3}.mkv";
        var movies = Base("Movies");

        var destination = router.MoveMovieFile(CreateSourceFile(name), name, movies)!;

        Assert.Equal(Path.Combine(movies, "Blade Runner (1982)", name), destination);
        Assert.Null(router.MoveTvFile(CreateSourceFile(name), name, Base("TV")));      // and is never taken for an episode
    }

    [Fact]
    public void ATvEpisodeMarker_AfterATagWithAnEditionNamed4x3_IsStillFound()
    {
        var info = ContentClassifier.GetEpisodeInfo("The Wire (2002) {edition-4x3} - S02E05 - Undertow.mkv");

        Assert.True(info.HasSeasonAndEpisode);
        Assert.Equal(("02", "05"), (info.Season, info.Episode));
        Assert.Equal("The Wire (2002) {edition-4x3}", info.ShowName);
    }

    [Fact]
    public void TheTagMask_BlanksOnlyRealTags_AndKeepsTheLength()
    {
        Assert.Equal("Show " + new string('_', 13) + " S01E01.mkv", PlexTags.Mask("Show {edition-4x3} S01E01.mkv"));
        Assert.Equal("Show {edition 4x3}.mkv", PlexTags.Mask("Show {edition 4x3}.mkv"));      // not Plex's form: left alone
    }

    // ---- poster lookups ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Babylon 5 (1994) {edition-Bluray Remaster} - S01E01 - Midnight.mkv", "Babylon 5", 1994)]
    [InlineData("Babylon 5 {edition-Bluray Remaster} - S01E01.mkv", "Babylon 5", null)]
    [InlineData("Show Name (2020) {tmdb-123456} - S01E01.mkv", "Show Name", 2020)]
    [InlineData("Show Name (2020) {edition-Extended} {tmdb-123456} - S01E01.mkv", "Show Name", 2020)]      // any order
    [InlineData("The Office (US) (2005) {edition-Superfan} - S03E10.mkv", "The Office (US)", 2005)]
    public void ATvShowsPosterLookup_NeverSendsAPlexTag(string fileName, string title, int? year)
    {
        var query = PosterNameParser.Parse(fileName)!;

        Assert.True(query.IsTv);
        Assert.Equal((title, year), (query.Title, query.Year));
    }

    [Fact]
    public void ATvShowsEditions_ShareOnePoster_WithTheShowItself()
    {
        var plain = PosterNameParser.Parse("Babylon 5 (1994) - S01E01.mkv")!;
        var edition = PosterNameParser.Parse("Babylon 5 (1994) {edition-Bluray Remaster} - S01E02.mkv")!;

        Assert.Equal(plain.Id, edition.Id);
    }

    [Theory]
    [InlineData("Babylon 5 (1994) {edition Bluray Remaster} - S01E01.mkv")]    // not Plex's form: nothing is guessed, the text is simply part of the name
    public void ANameThatIsNotPlexsForm_IsNotStripped(string fileName)
    {
        var query = PosterNameParser.Parse(fileName)!;

        Assert.Contains("edition", query.Title);
    }

    [Theory]
    [InlineData("Blade Runner (1982) {edition-Director's Cut}.mkv", "Blade Runner", 1982)]
    [InlineData("Blade Runner (1982) {imdb-tt0083658} {edition-Final Cut}.mkv", "Blade Runner", 1982)]
    public void AMoviesPosterLookup_NeverSendsAPlexTag(string fileName, string title, int year)
    {
        var query = PosterNameParser.Parse(fileName)!;

        Assert.False(query.IsTv);
        Assert.Equal((title, year), (query.Title, query.Year));
    }

    // ---- the tag itself ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Babylon 5 (1994) {edition-Bluray Remaster}", "Babylon 5 (1994)")]
    [InlineData("Babylon 5 (1994) {EDITION-Bluray Remaster}", "Babylon 5 (1994)")]
    [InlineData("Show (2020) {tvdb-1} {edition-A} {imdb-tt1}", "Show (2020)")]
    [InlineData("Show (2020)", "Show (2020)")]
    [InlineData("Show {edition-A} (2020)", "Show {edition-A} (2020)")]                     // not at the end
    [InlineData("Show (2020) {edition-}", "Show (2020) {edition-}")]                       // nothing in it
    [InlineData("Show (2020) {edition-Bluray/Remaster}", "Show (2020) {edition-Bluray/Remaster}")]       // a character a folder name cannot hold
    [InlineData("Show (2020) {edition-123456789012345678901234567890123}", "Show (2020) {edition-123456789012345678901234567890123}")]   // 33 characters: over Plex's 32
    [InlineData("Show (2020) {edition-12345678901234567890123456789012}", "Show (2020)")]                                              // exactly 32
    public void OnlyPlexsTags_AtTheEndOfAName_AreRecognised(string name, string expected) =>
        Assert.Equal(expected, PlexTags.StripTrailing(name));
}
