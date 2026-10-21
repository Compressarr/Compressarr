using System.Text.RegularExpressions;

namespace Compressarr.Core.Conversion;

/// <summary>
/// The curly-brace tags Plex reads from a show or movie name: <c>{edition-Director's Cut}</c> (Plex Pass; up to 32 characters of the
/// edition's name) and the provider ids <c>{tmdb-123}</c>, <c>{tvdb-123}</c> and <c>{imdb-tt123}</c>. Plex puts an edition on a TV show's
/// top-level folder, after the show name and year ("Babylon 5 (1994) {edition-Bluray Remaster}"), and on a movie's file name and/or folder
/// name. Compressarr keeps such a tag exactly as written wherever it files a name (see FileRouter); this is the one place that knows what
/// the tags look like, so a name can be read without them. Anything that is not exactly this form is not a tag and is left untouched.
/// </summary>
public static partial class PlexTags
{
    /// <summary>One or more tags, in any order, at the very end of a name. The name part may not hold the characters a folder name cannot.</summary>
    [GeneratedRegex(@"(?:\s*\{(?:edition|tmdb|tvdb|imdb)-[^{}\\/:*?""<>|]{1,32}\})+\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingTags();

    [GeneratedRegex(@"\{(?:edition|tmdb|tvdb|imdb)-[^{}\\/:*?""<>|]{1,32}\}", RegexOptions.IgnoreCase)]
    private static partial Regex AnyTag();

    /// <summary>The same name with every Plex tag blanked out to underscores (same length, so positions still line up). The season-and-episode
    /// search runs on this, because what is inside a tag is a name, never an episode marker: an edition called "4x3" (the aspect ratio) must not
    /// read as season 4, episode 3.</summary>
    public static string Mask(string name) => AnyTag().Replace(name, m => new string('_', m.Length));

    [GeneratedRegex(@"\{edition-[^{}\\/:*?""<>|]{1,32}\}", RegexOptions.IgnoreCase)]
    private static partial Regex AnEdition();

    [GeneratedRegex(@"^(?:Season\s*\d+|Specials|S\d{1,3})$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolderName();

    /// <summary>Whether the tags at the end of a name include an edition: "Babylon 5 (1994) {edition-Bluray Remaster}" does.</summary>
    public static bool HasTrailingEdition(string name)
    {
        var tags = TrailingTags().Match(name);
        return tags.Success && AnEdition().IsMatch(tags.Value);
    }

    /// <summary>The edition tag (as written, e.g. "{edition-Bluray Remaster}") a TV file's SOURCE show folder carries, for a file whose own
    /// name has none. The show folder is the file's folder, or - when that is a season folder ("Season 01", "Specials") - the one above it, and
    /// it must lie inside the lane's Input folder. Plex keeps two versions of a show apart by the edition on the show folder, so a source laid out
    /// "Babylon 5 (1994) {edition-Bluray Remaster}\Season 01\Babylon 5 (1994) - S01E01.mkv" must land in the matching edition folder. The folder
    /// is only trusted when it is the same show as the file name says (its name without the Plex tags matches the file's show name, ignoring case
    /// and punctuation): a folder that merely holds the file, such as a collection, is never mistaken for it. Null when there is no such tag.</summary>
    public static string? EditionOfSourceShowFolder(string sourceFileFullName, string inputRoot, string showName)
    {
        if (string.IsNullOrWhiteSpace(sourceFileFullName) || string.IsNullOrWhiteSpace(inputRoot) || string.IsNullOrWhiteSpace(showName)) return null;

        var folder = Path.GetDirectoryName(sourceFileFullName);
        if (string.IsNullOrEmpty(folder)) return null;
        if (SeasonFolderName().IsMatch(Path.GetFileName(folder))) folder = Path.GetDirectoryName(folder);
        if (string.IsNullOrEmpty(folder) || !IsInside(folder, inputRoot)) return null;

        var folderName = Path.GetFileName(folder);
        var tags = TrailingTags().Match(folderName);
        if (!tags.Success) return null;

        var edition = AnEdition().Matches(tags.Value).LastOrDefault();
        if (edition is null) return null;

        return Key(StripTrailing(folderName)) == Key(StripTrailing(showName)) && Key(showName).Length > 0 ? edition.Value : null;
    }

    private static bool IsInside(string folder, string root)
    {
        try
        {
            var inner = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var outer = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return inner.StartsWith(outer + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || inner.StartsWith(outer + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A name reduced to its lower-case letters and digits, so "Babylon.5.(1994)" and "Babylon 5 (1994)" are the same show.</summary>
    private static string Key(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>The name without the Plex tags at its end: "Babylon 5 (1994) {edition-Bluray Remaster}" becomes "Babylon 5 (1994)".</summary>
    public static string StripTrailing(string name) => TrailingTags().Replace(name, "");
}
