using System.Text.RegularExpressions;

namespace Compressarr.Core.Conversion;

public sealed record EpisodeInfo(
    bool HasSeasonAndEpisode,
    string? Season,
    string? Episode,
    string ShowName,
    string EpisodeFileName);

/// <summary>
/// TV-vs-Movie auto-detection: the single dispatch point used throughout the app (conversion,
/// routing, reporting) to decide whether a file is a TV episode. Ported verbatim from
/// Get-CompressarrEpisodeInfo/Test-CompressarrIsTVFile, including the deliberately uncapped
/// \d+ digit groups (the original tool this was based on capped both at \d{1,2}, which silently
/// mis-parsed any season/episode number running 3+ digits, e.g. absolute-numbering schemes like
/// S01E123).
/// </summary>
public static partial class ContentClassifier
{
    [GeneratedRegex(@"S*(\d+)(x|E)(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodePattern();

    [GeneratedRegex(@"^(.*?\(\d{4}\))")]
    private static partial Regex MovieYearTagPattern();

    public static EpisodeInfo GetEpisodeInfo(string fileName)
    {
        var pattern = EpisodePattern();

        // Text inside a Plex tag (an edition named "4x3") is a name, not an episode marker: look for the marker with the tags blanked out.
        var splitFull = pattern.Split(PlexTags.Mask(fileName));
        var season = splitFull.Length > 1 ? splitFull[1] : null;
        var separator = splitFull.Length > 2 ? splitFull[2] : null;
        var episode = splitFull.Length > 3 ? splitFull[3] : null;

        // A bare "NxNN" marker (no literal "E") is ambiguous with a raw resolution tag like
        // "720x480" or "1920x1080" - reject it when either side is outside what a real season/
        // episode number would plausibly be. No real show has 100+ seasons (every real
        // resolution's width is 3+ digits, so this alone catches ordinary resolution tags), and
        // even the most prolific daily soap operas/game shows top out well under 1000 episodes
        // in a single season (observed real-world ceiling ~365), so a 4+ digit "episode" number
        // is essentially never real either - catches a different collision, e.g. a movie's year
        // landing right after a stray digit and an "x" ("4x2020"). The unambiguous "E" separator
        // (never used in a resolution tag) is left completely alone, so legitimate 3+ digit
        // season numbers - e.g. year-based numbering like "S1944E01" - keep working exactly as
        // before.
        if (string.Equals(separator, "x", StringComparison.OrdinalIgnoreCase) &&
            (season?.Length > 2 || episode?.Length > 3))
        {
            season = null;
            episode = null;
        }

        var epiName = Path.GetFileName(fileName);
        var splitName = pattern.Split(PlexTags.Mask(epiName));
        var showName = splitName.Length > 0 ? epiName[..splitName[0].Length] : "";      // the text before the marker, from the real name (the masked one only found where it ends)
        if (showName.Length > 0)
        {
            // Drop the single separator character (usually '.', ' ', or '-') that sat directly
            // in front of the season marker.
            showName = showName[..^1].Trim();
        }
        showName = showName.TrimEnd('-').Trim();

        var hasSeasonAndEpisode = !string.IsNullOrEmpty(season) && !string.IsNullOrEmpty(episode);

        return new EpisodeInfo(hasSeasonAndEpisode, season, episode, showName, epiName);
    }

    public static bool IsTvFile(string fileName) => GetEpisodeInfo(fileName).HasSeasonAndEpisode;

    /// <summary>The per-movie subfolder name a converted movie is filed under: the base filename
    /// up through and including its "(YYYY)" year tag, with anything after that discarded -
    /// "Caddyshack (1980) {edition-Director's Cut}.mkv" becomes "Caddyshack (1980)". Filenames
    /// with no year tag fall back to the full base filename unchanged.</summary>
    public static string GetMovieFolderName(string fileName)
    {
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var match = MovieYearTagPattern().Match(baseName);
        return match.Success ? match.Groups[1].Value.Trim() : baseName.Trim();
    }
}
