using Compressarr.Core.Renaming;

namespace Compressarr.Core.Tests.Renaming;

public class ReleaseNameParserTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "compressarr-parser-root");

    private static ParsedName? Parse(string relativePath) => ReleaseNameParser.Parse(Path.Combine(Root, relativePath), Root);

    [Theory]
    [InlineData("Mission.Impossible.Fallout.2018.1080p.BluRay.x264-GRP.mkv", "Mission Impossible Fallout", 2018)]
    [InlineData("Blade.Runner.2049.2017.2160p.UHD.BluRay.x265-GRP.mkv", "Blade Runner 2049", 2017)]   // the LAST year is the release year
    [InlineData("1917.2019.1080p.BluRay.mkv", "1917", 2019)]                                          // a title that is a year keeps it
    [InlineData("Heat (1995).mkv", "Heat", 1995)]
    [InlineData("Dune [YTS.MX] 2021 1080p.mkv", "Dune", 2021)]
    [InlineData("Ocean.s.Eleven.1960.DVDRip.mkv", "Ocean s Eleven", 1960)]
    [InlineData("Spider-Man.Into.the.Spider-Verse.2018.1080p.WEB-DL.mkv", "Spider-Man Into the Spider-Verse", 2018)]
    public void AMovieNameGivesItsTitleAndYear(string file, string title, int year)
    {
        var parsed = Parse(file)!;

        Assert.False(parsed.IsTv);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
    }

    [Theory]
    [InlineData("Heat.mkv", "Heat")]
    [InlineData("Heat.720p.BluRay.mkv", "Heat")]
    [InlineData("Blade.Runner.2049.1080p.mkv", "Blade Runner 2049")]   // 2049 is not a year that has happened (or is about to)
    public void AMovieNameWithNoYearHasNone(string file, string title)
    {
        var parsed = Parse(file)!;

        Assert.Equal(title, parsed.Title);
        Assert.Null(parsed.Year);
    }

    [Fact]
    public void ThePlexEditionTagIsKeptAndATmdbTagIsNot()
    {
        var parsed = Parse("Heat (1995) {tmdb-949} {edition-Director's Cut}.mkv")!;

        Assert.Equal("Heat", parsed.Title);
        Assert.Equal(1995, parsed.Year);
        Assert.Equal("{edition-Director's Cut}", parsed.Tags);
    }

    [Fact]
    public void ANameWithATmdbTagIsRecognisedAsAlreadyNamed()
    {
        Assert.True(ReleaseNameParser.HasTmdbTag("Heat (1995) {tmdb-949}.mkv"));
        Assert.False(ReleaseNameParser.HasTmdbTag("Heat (1995) {edition-Director's Cut}.mkv"));
    }

    [Theory]
    [InlineData("Breaking.Bad.S01E01.1080p.BluRay.mkv", "Breaking Bad", null, null, 1, new[] { 1 })]
    [InlineData("The.Office.US.S02E03.720p.HDTV.x264-GRP.mkv", "The Office", null, "US", 2, new[] { 3 })]
    [InlineData("The.Office.UK.S01E01.mkv", "The Office", null, "GB", 1, new[] { 1 })]                  // TMDB calls the UK GB
    [InlineData("Doctor.Who.2005.S01E01.720p.HDTV.mkv", "Doctor Who", 2005, null, 1, new[] { 1 })]
    [InlineData("Friends.S01E01E02.1080p.BluRay.mkv", "Friends", null, null, 1, new[] { 1, 2 })]
    [InlineData("Show.Name.S03E04-E05.mkv", "Show Name", null, null, 3, new[] { 4, 5 })]
    [InlineData("Show Name - 1x05 - Some Title.mkv", "Show Name", null, null, 1, new[] { 5 })]
    [InlineData("Show.Name.(2019).S01E02.mkv", "Show Name", 2019, null, 1, new[] { 2 })]
    public void AnEpisodeNameGivesTheShowSeasonAndEpisodes(string file, string show, int? year, string? region, int season, int[] episodes)
    {
        var parsed = Parse(file)!;

        Assert.True(parsed.IsTv);
        Assert.Equal(show, parsed.Title);
        Assert.Equal(year, parsed.Year);
        Assert.Equal(region, parsed.Region);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episodes, parsed.Episodes);
    }

    [Fact]
    public void AResolutionIsNotReadAsASeasonAndEpisode()
    {
        var parsed = Parse("Some.Movie.2019.1920x1080.mkv")!;

        Assert.False(parsed.IsTv);
        Assert.Equal("Some Movie", parsed.Title);
        Assert.Equal(2019, parsed.Year);
    }

    [Fact]
    public void AnEpisodeWithNoShowNameTakesItFromTheShowFolder_LookingThroughTheSeasonFolder()
    {
        var parsed = Parse(Path.Combine("Breaking Bad (2008)", "Season 01", "S01E01.mkv"))!;

        Assert.Equal("Breaking Bad", parsed.Title);
        Assert.Equal(2008, parsed.Year);
        Assert.Equal(1, parsed.Season);
    }

    [Fact]
    public void AnEpisodeWithNoShowNameAndNoFolderToTellIsNotUnderstood()
    {
        Assert.Null(Parse("S01E01.mkv"));
    }

    [Fact]
    public void AMovieTakesItsYearFromItsFolder_OnlyWhenTheFolderIsTheSameFilm()
    {
        Assert.Equal(1995, Parse(Path.Combine("Heat (1995)", "Heat.mkv"))!.Year);
        Assert.Null(Parse(Path.Combine("Heat (1995)", "movie.mkv"))!.Year);                 // the folder is not asked to name the film
        Assert.Null(Parse(Path.Combine("Collection (2008)", "Heat.mkv"))!.Year);            // a folder for something else lends nothing
    }

    [Fact]
    public void ANameWithNothingLeftIsNotUnderstood()
    {
        Assert.Null(Parse(".mkv"));
        Assert.Null(Parse("1080p.mkv"));
    }
}
