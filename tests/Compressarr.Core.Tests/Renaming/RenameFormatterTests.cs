using Compressarr.Core.Config;
using Compressarr.Core.Renaming;

namespace Compressarr.Core.Tests.Renaming;

public class RenameFormatterTests
{
    [Fact]
    public void AMovieIsTitleAndYear()
    {
        Assert.Equal("Heat (1995)", RenameFormatter.MovieStem("Heat", 1995, "", null));
    }

    [Fact]
    public void AMovieCanCarryTheTmdbIdAndKeepsItsEditionTag()
    {
        Assert.Equal("Heat (1995) {tmdb-949}", RenameFormatter.MovieStem("Heat", 1995, "", 949));
        Assert.Equal("Heat (1995) {tmdb-949} {edition-Director's Cut}", RenameFormatter.MovieStem("Heat", 1995, "{edition-Director's Cut}", 949));
        Assert.Equal("Heat (1995) {edition-Director's Cut}", RenameFormatter.MovieStem("Heat", 1995, "{edition-Director's Cut}", null));
    }

    [Theory]
    [InlineData("Mission: Impossible - Fallout", "Mission Impossible - Fallout")]   // a colon is dropped
    [InlineData("WALL·E", "WALL-E")]                                               // TMDB's middle dot becomes a plain hyphen
    [InlineData("Face/Off", "Face-Off")]
    [InlineData("What's Up, Doc?", "What's Up, Doc")]
    [InlineData("Ocean’s Eleven", "Ocean's Eleven")]                               // the curly apostrophe becomes the plain one
    [InlineData("  Spaced    Out  ", "Spaced Out")]
    [InlineData("Ends With Dots...", "Ends With Dots")]                            // Windows cannot end a name with a dot
    [InlineData("A \"Quoted\" <Title>*", "A Quoted Title")]
    public void ATitleIsMadeSafeForAFileName(string title, string expected)
    {
        Assert.Equal(expected, RenameFormatter.SafeTitle(title, 120));
    }

    [Fact]
    public void ALongTitleIsCutOffAtTheLimit()
    {
        var safe = RenameFormatter.SafeTitle(new string('a', 300), 120);

        Assert.Equal(120, safe.Length);
    }

    [Fact]
    public void AnEpisodeIsShowYearNumbersAndTitle()
    {
        Assert.Equal("Breaking Bad (2008) - S01E01 - Pilot",
            RenameFormatter.EpisodeStem("Breaking Bad", 2008, 1, new[] { 1 }, new string?[] { "Pilot" }, TvNumbering.S00E00, ""));
        Assert.Equal("Breaking Bad (2008) - 1x01 - Pilot",
            RenameFormatter.EpisodeStem("Breaking Bad", 2008, 1, new[] { 1 }, new string?[] { "Pilot" }, TvNumbering.SxE, ""));
    }

    [Fact]
    public void AMultiEpisodeFileJoinsTheTitles()
    {
        Assert.Equal("Friends (1994) - S01E01-E02 - Pilot + The One with the Sonogram at the End",
            RenameFormatter.EpisodeStem("Friends", 1994, 1, new[] { 1, 2 }, new string?[] { "Pilot", "The One with the Sonogram at the End" }, TvNumbering.S00E00, ""));
        Assert.Equal("Friends (1994) - 1x01-02 - Pilot + Second",
            RenameFormatter.EpisodeStem("Friends", 1994, 1, new[] { 1, 2 }, new string?[] { "Pilot", "Second" }, TvNumbering.SxE, ""));
    }

    [Fact]
    public void AMissingEpisodeTitleOrYearLeavesThatPartOut()
    {
        Assert.Equal("Some Show - S02E10", RenameFormatter.EpisodeStem("Some Show", null, 2, new[] { 10 }, new string?[] { "" }, TvNumbering.S00E00, ""));
        Assert.Equal("Some Show - S02E10", RenameFormatter.EpisodeStem("Some Show", null, 2, new[] { 10 }, new string?[] { null }, TvNumbering.S00E00, ""));
    }

    [Fact]
    public void ThreeDigitSeasonsAndEpisodesAreNotCutShort()
    {
        Assert.Equal("Show - S12E123", RenameFormatter.EpisodeStem("Show", null, 12, new[] { 123 }, new string?[] { null }, TvNumbering.S00E00, ""));
    }
}
