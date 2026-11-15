using System.Text.RegularExpressions;
using Compressarr.Core.Conversion;
using Compressarr.Core.Posters;

namespace Compressarr.Core.Renaming;

/// <summary>What a file's name says it is. For a TV file the title is the show's and <see cref="Season"/> and <see cref="Episodes"/> are set; for a movie only the title and (when the name has one) the year.
/// <see cref="Region"/> is TMDB's country code for a "US" / "UK" tag in a show's name ("The Office US"), used to tell two shows of one title apart. <see cref="Tags"/> is the Plex tags
/// the name already ends with (an edition), minus any {tmdb-...} tag, kept so the new name carries them on.</summary>
public sealed record ParsedName(bool IsTv, string Title, int? Year, string? Region, int? Season, IReadOnlyList<int> Episodes, string Tags);

/// <summary>Reads a title, a year and a season and episode out of the kind of name video files come with ("Mission.Impossible.Fallout.2018.1080p.BluRay.x264-GRP.mkv",
/// "The.Office.US.S02E03.720p.HDTV.mkv"), for the TMDB renamer. Pure: no file is touched, and nothing here knows about TMDB. When the file's own name is not enough (no show name, no year) the
/// folders above it, up to the lane's Input folder, are tried.</summary>
public static partial class ReleaseNameParser
{
    private const int MaxTitleLength = 100;

    // A season and episode ("S01E02", "1x02"), then any further episodes of a multi-episode file ("E03", "-E03"). The marker must not sit inside a longer word or number,
    // which is what keeps a resolution such as 1920x1080 or 720x480 from reading as "season 20, episode 108".
    [GeneratedRegex(@"^(?<show>.*?)[\s._\-\[(]*(?<![A-Za-z0-9])(?:S(?<s>\d{1,3})[\s._-]?E(?<e>\d{1,3})|(?<s2>\d{1,2})x(?<e2>\d{2,3}))(?!\d)(?<more>(?:[\s._-]?E\d{1,3}(?!\d))*)", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeMarker();

    [GeneratedRegex(@"E(\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeNumber();

    [GeneratedRegex(@"\[[^\]]*\]")]
    private static partial Regex BracketedGroup();

    [GeneratedRegex(@"^(?:19|20)\d{2}$")]
    private static partial Regex YearToken();

    // Where the title stops when there is no year: the first sign of how the release was made.
    [GeneratedRegex(@"^(?:2160p|1080p|1080i|720p|576p|480p|4k|uhd|bluray|blu-ray|bdrip|brrip|webrip|web-dl|webdl|web|hdtv|dvdrip|hdrip|remux|x264|x265|h264|h265|hevc|avc|xvid|aac|ac3|dts|atmos|hdr|hdr10|proper|repack|extended|unrated|imax)$", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseNoise();

    [GeneratedRegex(@"\s*[\(\[]?\b((?:19|20)\d{2})\b[\)\]]?\s*$")]
    private static partial Regex TrailingYear();

    [GeneratedRegex(@"\s*\(?\b(US|UK|AU|CA|NZ|DE|FR|IE)\b\)?\s*$")]
    private static partial Regex TrailingRegion();

    [GeneratedRegex(@"^(?:Season\s*\d+|Specials|S\d{1,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolderName();

    [GeneratedRegex(@"\{tmdb-[^{}\\/:*?""<>|]{1,32}\}", RegexOptions.IgnoreCase)]
    private static partial Regex TmdbTag();

    /// <summary>TMDB's country code for the two-letter tag a release puts after a show's name. "UK" is "GB" there.</summary>
    private static string? RegionCode(string tag) => tag.ToUpperInvariant() switch { "UK" => "GB", var other => other };

    /// <summary>True when the name already carries a {tmdb-...} tag: it was named from TMDB (by this renamer or by hand) and is left as it is.</summary>
    public static bool HasTmdbTag(string fileName) => TmdbTag().IsMatch(fileName);

    /// <summary>Reads <paramref name="fileFullName"/>; null when nothing usable can be told (an empty title, or a name the rest of the app reads as an episode but this cannot).
    /// <paramref name="inputRoot"/> bounds how far up the folders are looked at.</summary>
    public static ParsedName? Parse(string fileFullName, string inputRoot)
    {
        var fileName = Path.GetFileName(fileFullName);
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var tagLess = PlexTags.StripTrailing(stem);                                  // a prefix of the stem: what was cut off is the tags
        var tags = TmdbTag().Replace(stem[tagLess.Length..], "");
        tags = string.Join(' ', tags.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        var marker = EpisodeMarker().Match(PlexTags.Mask(tagLess));
        if (marker.Success)
        {
            return ParseEpisode(fileFullName, inputRoot, tagLess, marker, tags);
        }

        // The rest of the app reads this as an episode but the stricter pattern here does not: not understood, rather than treated as a movie.
        if (ContentClassifier.IsTvFile(fileName)) return null;

        return ParseMovie(fileFullName, inputRoot, tagLess, tags);
    }

    private static ParsedName? ParseEpisode(string fileFullName, string inputRoot, string tagLess, Match marker, string tags)
    {
        var season = int.Parse(marker.Groups["s"].Success ? marker.Groups["s"].Value : marker.Groups["s2"].Value);
        var first = int.Parse(marker.Groups["e"].Success ? marker.Groups["e"].Value : marker.Groups["e2"].Value);
        var episodes = new List<int> { first };
        foreach (Match more in EpisodeNumber().Matches(marker.Groups["more"].Value)) episodes.Add(int.Parse(more.Groups[1].Value));
        episodes = episodes.Distinct().OrderBy(n => n).ToList();

        var showText = tagLess[..marker.Groups["show"].Length];
        var (title, year, region) = ShowTitle(showText);
        if (title.Length == 0)
        {
            // "S01E01.mkv" in a show's folder: the folder is the show (a "Season 01" folder is looked through).
            var folder = ShowFolderName(fileFullName, inputRoot);
            if (folder is not null) (title, year, region) = ShowTitle(folder);
        }

        if (title.Length == 0) return null;
        return new ParsedName(IsTv: true, title, year, region, season, episodes, tags);
    }

    private static (string Title, int? Year, string? Region) ShowTitle(string text)
    {
        var name = Clean(PlexTags.StripTrailing(text));
        int? year = null;
        string? region = null;

        // Either order: "Doctor Who 2005 US" and "The Office US 2005" both mean what they say.
        for (var pass = 0; pass < 2; pass++)
        {
            var y = TrailingYear().Match(name);
            if (year is null && y.Success && y.Index > 0)
            {
                year = int.Parse(y.Groups[1].Value);
                name = name[..y.Index].Trim();
                continue;
            }

            var r = TrailingRegion().Match(name);
            if (region is null && r.Success && r.Index > 0)
            {
                region = RegionCode(r.Groups[1].Value);
                name = name[..r.Index].Trim();
            }
        }

        return (Clean(name), year, region);
    }

    private static string? ShowFolderName(string fileFullName, string inputRoot)
    {
        var folder = Path.GetDirectoryName(fileFullName);
        while (!string.IsNullOrEmpty(folder) && Compressarr.Core.Routing.PathContainment.IsStrictlyInside(folder, inputRoot))
        {
            var name = Path.GetFileName(folder);
            if (!SeasonFolderName().IsMatch(name)) return name;
            folder = Path.GetDirectoryName(folder);
        }

        return null;
    }

    private static ParsedName? ParseMovie(string fileFullName, string inputRoot, string tagLess, string tags)
    {
        var (title, year) = MovieTitle(tagLess);
        if (year is null)
        {
            // No year in the file's name: the folder it is in may carry one ("Heat (1995)\movie.mkv"), but only the folder's own year is borrowed, and only when the folder is
            // the same film as the file says (or the file says nothing useful) - a folder that merely holds the file never decides what it is.
            var folder = Path.GetDirectoryName(fileFullName);
            if (!string.IsNullOrEmpty(folder) && Compressarr.Core.Routing.PathContainment.IsStrictlyInside(folder, inputRoot))
            {
                var (folderTitle, folderYear) = MovieTitle(PlexTags.StripTrailing(Path.GetFileName(folder)));
                if (folderYear is not null && folderTitle.Length > 0 && (title.Length == 0 || PosterNameParser.TitlesMatch(title, folderTitle)))
                {
                    title = folderTitle;
                    year = folderYear;
                }
            }
        }

        if (title.Length == 0) return null;
        return new ParsedName(IsTv: false, title, year, Region: null, Season: null, Episodes: Array.Empty<int>(), tags);
    }

    /// <summary>The title and year of a movie name: the title is everything before the LAST plausible year (so "Blade Runner 2049 2017" is Blade Runner 2049, 2017, and a film called
    /// "1917" keeps its title). Without a year the title ends at the first sign of how the release was made.</summary>
    private static (string Title, int? Year) MovieTitle(string name)
    {
        var spaced = BracketedGroup().Replace(name, " ").Replace('.', ' ').Replace('_', ' ').Replace('(', ' ').Replace(')', ' ');
        var tokens = spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var latest = DateTime.UtcNow.Year + 1;

        var yearIndex = -1;
        for (var i = 1; i < tokens.Length; i++)
        {
            if (YearToken().IsMatch(tokens[i]) && int.Parse(tokens[i]) <= latest) yearIndex = i;
        }

        if (yearIndex > 0) return (Clean(string.Join(' ', tokens[..yearIndex])), int.Parse(tokens[yearIndex]));

        var noise = Array.FindIndex(tokens, t => ReleaseNoise().IsMatch(t));
        return (Clean(string.Join(' ', noise >= 0 ? tokens[..noise] : tokens)), null);       // a name that starts with release noise has no title at all
    }

    private static string Clean(string text)
    {
        var spaced = text.Replace('.', ' ').Replace('_', ' ');
        var collapsed = string.Join(' ', spaced.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '-');
        return collapsed.Length > MaxTitleLength ? collapsed[..MaxTitleLength].TrimEnd() : collapsed;
    }
}
