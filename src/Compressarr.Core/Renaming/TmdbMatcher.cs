using System.Globalization;
using System.Text;

namespace Compressarr.Core.Renaming;

/// <summary>One film or show TMDB returned for a search. <see cref="Year"/> is the release (movie) or first air (TV) year; <see cref="OriginCountries"/> is only filled for a show.</summary>
public sealed record TmdbCandidate(int Id, bool IsTv, string Title, string? OriginalTitle, int? Year, IReadOnlyList<string> OriginCountries, double Popularity, string? Overview, string? PosterPath)
{
    public string Display => Year is { } y ? $"{Title} ({y})" : Title;
}

public enum MatchOutcome
{
    /// <summary>Exactly one candidate fits: it is the answer.</summary>
    Matched,

    /// <summary>More than one fits equally well. Not guessed: the file stays as it is and can be matched by hand.</summary>
    Ambiguous,

    /// <summary>Nothing fits (or the name does not say enough to tell, such as a movie with no year).</summary>
    NoMatch
}

public sealed record MatchDecision(MatchOutcome Outcome, TmdbCandidate? Candidate, string Why);

/// <summary>Decides which of TMDB's search results, if any, a file name means. Deliberately strict: a wrong rename silently mislabels a library, so only a candidate that is clearly the one is
/// taken. Candidates are scored (title: exactly equal 2, equal once a leading "The" is set aside 1; year: equal 2, one year out 1) and the best score wins only when it is alone at the top.
/// A movie with no year is never matched (a bare title matches the wrong film too often). Pure.</summary>
public static class TmdbMatcher
{
    public static MatchDecision Decide(ParsedName name, IReadOnlyList<TmdbCandidate> results)
    {
        if (!name.IsTv && name.Year is null) return new(MatchOutcome.NoMatch, null, "the name has no year, and a bare title is not guessed at");

        var scored = new List<(TmdbCandidate Candidate, int Score)>();
        foreach (var candidate in results)
        {
            var title = TitleScore(name.Title, candidate);
            if (title == 0) continue;

            var year = YearScore(name.Year, candidate.Year);
            if (year < 0) continue;

            if (name.Region is { } region && !candidate.OriginCountries.Contains(region, StringComparer.OrdinalIgnoreCase)) continue;

            scored.Add((candidate, title + year));
        }

        if (scored.Count == 0)
        {
            return new(MatchOutcome.NoMatch, null, name.Year is { } y ? $"TMDB has no {Kind(name)} titled \"{name.Title}\" from {y}" : $"TMDB has no {Kind(name)} titled \"{name.Title}\"");
        }

        var best = scored.Max(s => s.Score);
        var top = scored.Where(s => s.Score == best).Select(s => s.Candidate).ToList();
        return top.Count == 1
            ? new(MatchOutcome.Matched, top[0], "")
            : new(MatchOutcome.Ambiguous, null, $"more than one {Kind(name)} fits: {string.Join(" | ", top.Take(4).Select(c => c.Display))}");
    }

    private static string Kind(ParsedName name) => name.IsTv ? "show" : "film";

    private static int TitleScore(string wanted, TmdbCandidate candidate)
    {
        var best = 0;
        foreach (var found in new[] { candidate.Title, candidate.OriginalTitle })
        {
            if (string.IsNullOrWhiteSpace(found)) continue;
            if (Key(wanted, dropThe: false) == Key(found, dropThe: false) && Key(wanted, dropThe: false).Length > 0) best = Math.Max(best, 2);
            else if (Key(wanted, dropThe: true) == Key(found, dropThe: true) && Key(wanted, dropThe: true).Length > 0) best = Math.Max(best, 1);
        }

        return best;
    }

    /// <summary>The year's score: 2 for the same year, 1 for one year out (a release date differs by country), 0 for every candidate when no year was asked for (a show named without one),
    /// and -1 when the candidate is ruled out: two or more years out, or no year at all when one was asked for.</summary>
    private static int YearScore(int? wanted, int? found)
    {
        if (wanted is null) return 0;
        if (found is null) return -1;
        return Math.Abs(wanted.Value - found.Value) switch { 0 => 2, 1 => 1, _ => -1 };
    }

    /// <summary>A title reduced to its letters and digits, accents and case set aside ("Mission: Impossible - Fallout" is "Mission Impossible Fallout"), optionally without a leading "The".</summary>
    internal static string Key(string title, bool dropThe)
    {
        var decomposed = title.Replace("&", " and ").Normalize(NormalizationForm.FormD).TrimStart();
        if (dropThe && decomposed.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) decomposed = decomposed[4..];

        var letters = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) letters.Append(char.ToLowerInvariant(c));
        }

        return letters.ToString();
    }
}
