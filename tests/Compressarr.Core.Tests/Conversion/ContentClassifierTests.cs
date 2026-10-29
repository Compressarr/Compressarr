using Compressarr.Core.Conversion;

namespace Compressarr.Core.Tests.Conversion;

public class ContentClassifierTests
{
    [Theory]
    [InlineData("Show.Name.S01E01.mkv", true, "01", "01")]
    [InlineData("Show.Name.S01E123.mkv", true, "01", "123")]
    [InlineData("Show.Name.S123E01.mkv", true, "123", "01")]
    [InlineData("Show.Name.1x01.mkv", true, "1", "01")]
    [InlineData("Show.Name.3x04.mkv", true, "3", "04")]
    [InlineData("Show.Name.12x05.mkv", true, "12", "05")]
    // Year-based season numbering (a real convention for daily/dated shows) must keep working
    // unchanged - the "E" separator is never used in a resolution tag, so it's never subject to
    // the digit-count rejection below.
    [InlineData("Show.Name.S1944E01.mkv", true, "1944", "01")]
    // Boundary cases for the "x"-separator rejection: season up to 2 digits and episode up to 3
    // digits must still pass (a real show can plausibly reach these, e.g. a prolific soap opera
    // season with up to ~365 episodes).
    [InlineData("Show.Name.99x05.mkv", true, "99", "05")]
    [InlineData("Show.Name.12x365.mkv", true, "12", "365")]
    public void GetEpisodeInfo_TvFilenames_ExtractsSeasonAndEpisode(string fileName, bool expectedHasBoth, string expectedSeason, string expectedEpisode)
    {
        var info = ContentClassifier.GetEpisodeInfo(fileName);

        Assert.Equal(expectedHasBoth, info.HasSeasonAndEpisode);
        Assert.Equal(expectedSeason, info.Season);
        Assert.Equal(expectedEpisode, info.Episode);
    }

    [Theory]
    [InlineData("Caddyshack (1980).mkv")]
    [InlineData("Some Movie Title.mp4")]
    // Real resolution tags (never used with an "E" separator) must not be mistaken for a season/
    // episode marker - confirmed happening in the wild: an older DivX/XviD-era rip carrying a raw
    // pixel-dimension tag would otherwise get routed as a TV episode into a nonsense "Season 720"
    // folder.
    [InlineData("The Matrix (1999) 720x480.mkv")]
    [InlineData("Old Rip (2003) 1920x1080.mkv")]
    [InlineData("Old Rip (2003) 320x240.mkv")]
    // A small season-like number followed by an implausibly large "episode" (a movie's own year,
    // in this case) - the season-digit check alone wouldn't catch this, since "4" is only 1
    // digit; the episode-digit check (>3 digits, i.e. >=1000) is what rejects it.
    [InlineData("Some Movie 4x2020.mkv")]
    public void GetEpisodeInfo_MovieFilenames_HasNoSeasonEpisode(string fileName)
    {
        var info = ContentClassifier.GetEpisodeInfo(fileName);

        Assert.False(info.HasSeasonAndEpisode);
    }

    [Fact]
    public void IsTvFile_MatchesHasSeasonAndEpisode()
    {
        Assert.True(ContentClassifier.IsTvFile("Show.S01E01.mkv"));
        Assert.False(ContentClassifier.IsTvFile("Caddyshack (1980).mkv"));
    }

    [Theory]
    [InlineData("Show.Name.S01E01.mkv", "tv")]
    [InlineData("Show Name - 2x07 - Title.mkv", "tv")]
    [InlineData("Dora and Friends Into the City! S02E07 - A Swiper Emergency.mkv", "tv")]
    [InlineData("Caddyshack (1980).mkv", "movie")]
    [InlineData("Caddyshack (1980) {edition-Director's Cut}.mkv", "movie")]
    [InlineData("Beach Day 2019.mkv", "video")]                  // no (year) tag and no episode: a home video or clip, handled like a movie
    [InlineData("Holiday 1920x1080.mkv", "video")]               // a resolution is not an episode
    [InlineData("Show S01E01 (2020).mkv", "tv")]                 // an episode wins over a year
    public void KindOf_SaysTvMovieOrVideo(string fileName, string expected)
    {
        Assert.Equal(expected, ContentClassifier.KindOf(fileName));
        Assert.Equal(expected == "tv", ContentClassifier.IsTvFile(fileName));   // a video is still handled as a movie everywhere else
    }

    [Theory]
    [InlineData("Caddyshack (1980) {edition-Director's Cut}.mkv", "Caddyshack (1980)")]
    [InlineData("Caddyshack (1980).mkv", "Caddyshack (1980)")]
    [InlineData("No Year Tag At All.mkv", "No Year Tag At All")]
    public void GetMovieFolderName_ExtractsThroughYearTag(string fileName, string expected)
    {
        var result = ContentClassifier.GetMovieFolderName(fileName);

        Assert.Equal(expected, result);
    }
}
