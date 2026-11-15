using Compressarr.Core.Renaming;

namespace Compressarr.Core.Tests.Renaming;

/// <summary>The fixtures are what TMDB really answered for these searches (checked live on 2026-11-15), trimmed to the fields the matcher reads.</summary>
public class TmdbMatcherTests
{
    private static TmdbCandidate Film(int id, string title, int? year, string? original = null) =>
        new(id, IsTv: false, title, original ?? title, year, Array.Empty<string>(), 1, null, null);

    private static TmdbCandidate Show(int id, string name, int? year, params string[] countries) =>
        new(id, IsTv: true, name, name, year, countries, 1, null, null);

    private static ParsedName Movie(string title, int? year) => new(false, title, year, null, null, Array.Empty<int>(), "");

    private static ParsedName Tv(string title, int? year = null, string? region = null) => new(true, title, year, region, 1, new[] { 1 }, "");

    [Fact]
    public void ExactTitleAndYear_IsMatched()
    {
        var decision = TmdbMatcher.Decide(Movie("Blade Runner 2049", 2017), new[] { Film(335984, "Blade Runner 2049", 2017) });

        Assert.Equal(MatchOutcome.Matched, decision.Outcome);
        Assert.Equal(335984, decision.Candidate!.Id);
    }

    [Fact]
    public void TheYearBreaksATieBetweenFilmsOfOneTitle_AnObscureNeighbourDoesNotMakeItAmbiguous()
    {
        // "Dune.2021": the 1984 film is two years out; the obscure (2020) "Dűne" is one out and equal once the accent is set aside.
        var decision = TmdbMatcher.Decide(Movie("Dune", 2021), new[] { Film(438631, "Dune", 2021), Film(841, "Dune", 1984), Film(697620, "Dune", 2020, original: "Dűne") });

        Assert.Equal(MatchOutcome.Matched, decision.Outcome);
        Assert.Equal(438631, decision.Candidate!.Id);
    }

    [Fact]
    public void ALeadingTheOnlyMatchesWhenNothingBetterDoes()
    {
        // "Alien.1979": "The Alien (1979)" equals it only once "The" is dropped; "Alien" equals it exactly.
        var decision = TmdbMatcher.Decide(Movie("Alien", 1979), new[] { Film(348, "Alien", 1979), Film(820362, "Alien Portrait", 1979), Film(1309211, "The Alien", 1979) });

        Assert.Equal(MatchOutcome.Matched, decision.Outcome);
        Assert.Equal(348, decision.Candidate!.Id);

        Assert.Equal(1309211, TmdbMatcher.Decide(Movie("Alien", 1979), new[] { Film(1309211, "The Alien", 1979) }).Candidate!.Id);   // alone, it is still a match
    }

    [Fact]
    public void AMovieWithNoYearIsNeverMatched_EvenWhenOneFilmFits()
    {
        var decision = TmdbMatcher.Decide(Movie("Heat", null), new[] { Film(949, "Heat", 1995) });

        Assert.Equal(MatchOutcome.NoMatch, decision.Outcome);
        Assert.Contains("no year", decision.Why);
    }

    [Fact]
    public void AYearOneOutIsAccepted_TwoOutIsNot()
    {
        Assert.Equal(MatchOutcome.Matched, TmdbMatcher.Decide(Movie("Heat", 1996), new[] { Film(949, "Heat", 1995) }).Outcome);
        Assert.Equal(MatchOutcome.NoMatch, TmdbMatcher.Decide(Movie("Heat", 1997), new[] { Film(949, "Heat", 1995) }).Outcome);
    }

    [Fact]
    public void TwoFilmsEquallyCloseAreAmbiguous_NotGuessed()
    {
        var decision = TmdbMatcher.Decide(Movie("Heat", 2019), new[] { Film(1, "Heat", 2018), Film(2, "Heat", 2020) });

        Assert.Equal(MatchOutcome.Ambiguous, decision.Outcome);
        Assert.Null(decision.Candidate);
    }

    [Fact]
    public void AccentsPunctuationAndAMiddleDotAreSetAside()
    {
        Assert.Equal(MatchOutcome.Matched, TmdbMatcher.Decide(Movie("Mission Impossible Fallout", 2018), new[] { Film(353081, "Mission: Impossible - Fallout", 2018) }).Outcome);
        Assert.Equal(MatchOutcome.Matched, TmdbMatcher.Decide(Movie("WALL-E", 2008), new[] { Film(10681, "WALL·E", 2008) }).Outcome);
        Assert.Equal(MatchOutcome.Matched, TmdbMatcher.Decide(Movie("Amelie", 2001), new[] { Film(194, "Amélie", 2001) }).Outcome);
        Assert.Equal(MatchOutcome.Matched, TmdbMatcher.Decide(Movie("Ocean s Eleven", 1960), new[] { Film(299, "Ocean's Eleven", 1960) }).Outcome);
    }

    [Fact]
    public void TheOriginalTitleCountsToo()
    {
        Assert.Equal(MatchOutcome.Matched, TmdbMatcher.Decide(Movie("Gisaengchung", 2019), new[] { Film(496243, "Parasite", 2019, original: "Gisaengchung") }).Outcome);
    }

    [Fact]
    public void NothingFound_IsNoMatch()
    {
        Assert.Equal(MatchOutcome.NoMatch, TmdbMatcher.Decide(Movie("Some Home Video Nobody Made", 2019), Array.Empty<TmdbCandidate>()).Outcome);
        Assert.Equal(MatchOutcome.NoMatch, TmdbMatcher.Decide(Tv("Obscure Show Nobody Knows"), Array.Empty<TmdbCandidate>()).Outcome);
    }

    [Fact]
    public void AShowWithOneExactTitleIsMatchedWithoutAYear_ButSeveralAreAmbiguous()
    {
        var breakingBad = new[] { Show(1396, "Breaking Bad", 2008), Show(337563, "Breaking Bad: Original Minisodes", 2009) };
        Assert.Equal(1396, TmdbMatcher.Decide(Tv("Breaking Bad"), breakingBad).Candidate!.Id);

        var doctorWho = new[] { Show(57243, "Doctor Who", 2005), Show(121, "Doctor Who", 1963), Show(239770, "Doctor Who", 2024), Show(5, "Doctor Who", null) };
        Assert.Equal(MatchOutcome.Ambiguous, TmdbMatcher.Decide(Tv("Doctor Who"), doctorWho).Outcome);
        Assert.Equal(57243, TmdbMatcher.Decide(Tv("Doctor Who", year: 2005), doctorWho).Candidate!.Id);
    }

    [Fact]
    public void ARegionTagPicksTheShowFromThatCountry()
    {
        var offices = new[] { Show(2316, "The Office", 2005, "US"), Show(2996, "The Office", 2001, "GB") };

        Assert.Equal(2316, TmdbMatcher.Decide(Tv("The Office", region: "US"), offices).Candidate!.Id);
        Assert.Equal(2996, TmdbMatcher.Decide(Tv("The Office", region: "GB"), offices).Candidate!.Id);
        Assert.Equal(MatchOutcome.Ambiguous, TmdbMatcher.Decide(Tv("The Office"), offices).Outcome);
        Assert.Equal(MatchOutcome.NoMatch, TmdbMatcher.Decide(Tv("The Office", region: "AU"), offices).Outcome);
    }

    [Fact]
    public void AShowWhoseYearWasAskedForAndIsMissingOrFarOutIsRuledOut()
    {
        Assert.Equal(MatchOutcome.NoMatch, TmdbMatcher.Decide(Tv("Doctor Who", year: 2005), new[] { Show(5, "Doctor Who", null) }).Outcome);
        Assert.Equal(MatchOutcome.NoMatch, TmdbMatcher.Decide(Tv("Doctor Who", year: 2005), new[] { Show(121, "Doctor Who", 1963) }).Outcome);
    }
}
