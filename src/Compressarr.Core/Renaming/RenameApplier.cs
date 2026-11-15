namespace Compressarr.Core.Renaming;

/// <summary>Renames a video file in its own folder, together with the files that belong to it. Shared by the automatic renamer and by the queue's Match / Fix match / Restore, so they all treat a video's
/// companions (subtitles, .nfo) the same way.</summary>
internal static class RenameApplier
{
    /// <summary>Renames <paramref name="fullName"/> to <paramref name="newName"/> in the same folder, never over another file (a name that is taken throws, and nothing changes), then the files that
    /// belong to it. Compressarr finds a video's subtitles and other companion files by the video's own name followed by a ".", so those carry the new name too - or they would be left behind and swept up as
    /// leftovers when the video is done. A companion that cannot be renamed keeps its name and is reported through <paramref name="problem"/>; the video's rename stands. Returns the video's new full path.</summary>
    public static string Rename(string fullName, string newName, IReadOnlyList<string> vidTypes, Action<string>? problem = null)
    {
        var directory = Path.GetDirectoryName(fullName)!;
        var oldStem = Path.GetFileNameWithoutExtension(fullName);
        var newStem = Path.GetFileNameWithoutExtension(newName);
        var videoExtensions = vidTypes.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => "." + t.Trim().TrimStart('.')).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var companions = Directory.EnumerateFiles(directory)
            .Where(f => !string.Equals(f, fullName, StringComparison.OrdinalIgnoreCase))
            .Where(f => Path.GetFileName(f).StartsWith(oldStem + ".", StringComparison.OrdinalIgnoreCase))
            .Where(f => !videoExtensions.Contains(Path.GetExtension(f)))
            .ToList();

        var newPath = Path.Combine(directory, newName);
        File.Move(fullName, newPath);       // overwrite is off: if the name was taken since it was planned, this throws and nothing changes

        foreach (var companion in companions)
        {
            var renamed = Path.Combine(directory, newStem + "." + Path.GetFileName(companion)[(oldStem.Length + 1)..]);
            try
            {
                if (File.Exists(renamed))
                {
                    problem?.Invoke($"[TMDB] \"{Path.GetFileName(companion)}\" kept its name: \"{Path.GetFileName(renamed)}\" already exists.");
                    continue;
                }

                File.Move(companion, renamed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problem?.Invoke($"[TMDB] Could not rename \"{Path.GetFileName(companion)}\" with its video: {ex.Message}");
            }
        }

        return newPath;
    }
}
