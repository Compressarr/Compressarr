using System.Text;
using Compressarr.Core.Config;

namespace Compressarr.Core.Renaming;

/// <summary>Builds the new file name (without its extension) from what TMDB says. Pure. The name is made safe for any file system: a colon is dropped ("Mission: Impossible" becomes
/// "Mission Impossible"), a slash or bar becomes a hyphen ("Face/Off" is "Face-Off"), TMDB's middle dot becomes a plain hyphen ("WALL·E" is "WALL-E"), characters no file name can hold are
/// removed, and an apostrophe is the plain one.</summary>
public static class RenameFormatter
{
    private const int MaxTitleLength = 120;
    private const int MaxEpisodeTitleLength = 80;

    /// <summary>"Title (Year)", then {tmdb-id} when asked for, then the Plex tags the original name ended with.</summary>
    public static string MovieStem(string title, int year, string tags, int? tmdbId)
    {
        var stem = $"{SafeTitle(title, MaxTitleLength)} ({year})";
        if (tmdbId is { } id) stem += $" {{tmdb-{id}}}";
        return string.IsNullOrWhiteSpace(tags) ? stem : $"{stem} {tags.Trim()}";
    }

    /// <summary>"Show (Year) - S01E02 - Episode Title", with the episode numbers and the title(s) as TMDB gives them. A multi-episode file joins the titles with " + ". A missing title leaves that part out.</summary>
    public static string EpisodeStem(string show, int? showYear, int season, IReadOnlyList<int> episodes, IReadOnlyList<string?> episodeTitles, TvNumbering numbering, string tags)
    {
        var stem = new StringBuilder(SafeTitle(show, MaxTitleLength));
        if (showYear is { } y) stem.Append($" ({y})");
        stem.Append(" - ").Append(Numbers(season, episodes, numbering));

        var titles = episodeTitles.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => SafeTitle(t!, MaxEpisodeTitleLength)).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (titles.Count > 0) stem.Append(" - ").Append(string.Join(" + ", titles));

        if (!string.IsNullOrWhiteSpace(tags)) stem.Append(' ').Append(tags.Trim());
        return stem.ToString();
    }

    internal static string Numbers(int season, IReadOnlyList<int> episodes, TvNumbering numbering)
    {
        var first = episodes[0];
        var last = episodes[^1];
        return numbering == TvNumbering.SxE
            ? episodes.Count == 1 ? $"{season}x{first:00}" : $"{season}x{first:00}-{last:00}"
            : episodes.Count == 1 ? $"S{season:00}E{first:00}" : $"S{season:00}E{first:00}-E{last:00}";
    }

    /// <summary>The text made safe to use inside a file name (see the class summary). Empty when nothing is left.</summary>
    public static string SafeTitle(string text, int maxLength)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case ':' or '"' or '?' or '*' or '<' or '>':
                    break;                                              // dropped
                case '/' or '\\' or '|' or '·':
                    sb.Append('-');                                     // a slash, a bar and TMDB's middle dot
                    break;
                case '’' or '‘' or 'ʼ':
                    sb.Append('\'');
                    break;
                default:
                    if (!char.IsControl(c)) sb.Append(c);
                    break;
            }
        }

        var collapsed = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (collapsed.Length > maxLength) collapsed = collapsed[..maxLength].TrimEnd();
        return collapsed.TrimEnd('.', ' ');                             // Windows cannot have a name ending in a dot or space
    }
}
