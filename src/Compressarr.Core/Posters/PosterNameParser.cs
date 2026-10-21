using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Compressarr.Core.Conversion;

namespace Compressarr.Core.Posters;

/// <summary>What to look a poster up by: a TV show (by name) or a movie (by title and year). Only this text is ever sent to TMDb - never
/// a path, a file size or anything else about the file.</summary>
public sealed record PosterQuery(bool IsTv, string Title, int? Year)
{
    /// <summary>Two files that mean the same show or movie share one key, so a whole season costs one lookup.</summary>
    public string CacheKey => $"{(IsTv ? "tv" : "movie")}|{Title.ToLowerInvariant()}|{Year}";

    /// <summary>A short, URL-safe id for the cached poster (it is what the browser asks the app for).</summary>
    public string Id => _id ??= Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CacheKey)))[..16].ToLowerInvariant();

    private string? _id; // worked out once: the Monitor page asks for it for every queue row on every poll
}

/// <summary>Works out what a queued file is, from its name alone, using the same TV-versus-movie rule the rest of the app uses
/// (ContentClassifier). A TV file looks up its show; a movie must carry a "(YYYY)" year tag, because a title with no year matches the wrong
/// film too often - a file without one simply gets no poster rather than a wrong one.</summary>
public static partial class PosterNameParser
{
    private const int MaxTitleLength = 100;

    [GeneratedRegex(@"^(?<title>.+?)[\s._-]*\((?<year>\d{4})\)")]
    private static partial Regex MovieWithYear();

    [GeneratedRegex(@"\((?<year>\d{4})\)\s*$")]
    private static partial Regex TrailingYear();

    [GeneratedRegex(@"\s*\([^)]*\)\s*$")]
    private static partial Regex TrailingParenthetical();

    public static PosterQuery? Parse(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var episode = ContentClassifier.GetEpisodeInfo(fileName);
        if (episode.HasSeasonAndEpisode)
        {
            return ParseShow(episode.ShowName);
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var match = MovieWithYear().Match(baseName);
        if (!match.Success) return null;

        var title = Clean(match.Groups["title"].Value);
        var year = int.Parse(match.Groups["year"].Value);
        if (title.Length == 0 || year < 1888 || year > 2100) return null;

        return new PosterQuery(IsTv: false, title, year);
    }

    /// <summary>Whether a source's title is the title the file name gave: equal once case, accents, punctuation and a leading "The" are set aside
    /// ("Mission Impossible Fallout" is "Mission: Impossible - Fallout"). A longer title that merely contains the name does not match, so a
    /// different show that happens to share the words ("Live with Lil! Agents of SHIELD") is never taken for it.</summary>
    internal static bool TitlesMatch(string wanted, string found)
    {
        var a = Plain(wanted);
        var b = Plain(found);
        return a.Length > 0 && a == b;
    }

    private static string Plain(string title)
    {
        var decomposed = title.Replace("&", " and ").Normalize(System.Text.NormalizationForm.FormD).TrimStart();
        if (decomposed.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) decomposed = decomposed[4..];

        var letters = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) letters.Append(char.ToLowerInvariant(c));
        }

        return letters.ToString();
    }

    private static PosterQuery? ParseShow(string showName)
    {
        // A Plex tag after the year ("Babylon 5 (1994) {edition-Bluray Remaster}", "Show (2020) {tmdb-123}") is not part of the title: it is never
        // sent to a poster source, and the year in front of it still narrows the search.
        var name = PlexTags.StripTrailing(showName.Trim()).Trim();
        int? year = null;

        // "Show Name (2019)" narrows the search; a different tag such as "(US)" or "(UK)" is not part of the title TMDb knows it by.
        var yearMatch = TrailingYear().Match(name);
        if (yearMatch.Success)
        {
            year = int.Parse(yearMatch.Groups["year"].Value);
            name = name[..yearMatch.Index];
        }
        else
        {
            name = TrailingParenthetical().Replace(name, "");
        }

        var title = Clean(name);
        if (title.Length == 0) return null;
        if (year is < 1888 or > 2100) year = null;

        return new PosterQuery(IsTv: true, title, year);
    }

    /// <summary>Dots and underscores between words ("Breaking.Bad") become spaces; runs of spaces collapse; the length is capped.</summary>
    private static string Clean(string text)
    {
        var spaced = text.Replace('.', ' ').Replace('_', ' ');
        var collapsed = string.Join(' ', spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '-');
        return collapsed.Length > MaxTitleLength ? collapsed[..MaxTitleLength].TrimEnd() : collapsed;
    }
}
