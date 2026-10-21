using Compressarr.Core.Config;
using Compressarr.Core.Conversion;

namespace Compressarr.Core.Routing;

/// <summary>Thrown by FileRouter when DestinationCollisionMode.Skip is configured and the
/// destination already exists - deliberately distinct from a real move failure (unreachable path,
/// disk full, permission error) so ConversionOrchestrator can treat it as a warning, not an error:
/// the file wasn't moved because that's exactly what was configured, not because something went
/// wrong.</summary>
public sealed class DestinationCollisionSkippedException : Exception
{
    public DestinationCollisionSkippedException(string destPath)
        : base($"Destination already exists, move skipped: '{destPath}'")
    {
    }
}

public interface IFileRouter
{
    /// <summary>Dispatches on the file's auto-detected content type to the matching base path -
    /// a lane's TvShowBasePath for TV episodes, MovieBasePath for everything else. Returns null
    /// (no-op) if moveFiles is false. Throws DestinationCollisionSkippedException if the
    /// destination already exists and collisionMode is Skip.
    ///
    /// sourcePath and desiredFileName are deliberately independent: sourcePath is the actual file
    /// on disk to move (may be a collision-safe staging name, e.g. "Movie.compressarr-a1b2c3.mkv" -
    /// not necessarily anything a human would recognize), while desiredFileName is what content
    /// classification (season/episode for TV, title for movies) and the destination's own leaf
    /// filename are derived FROM, regardless of what sourcePath happens to be called. This is what
    /// lets a caller stage an encoded file under a name that can never collide with another
    /// in-flight or still-pending attempt, right up until the moment it's actually routed.
    ///
    /// showEdition is a Plex edition tag ("{edition-Bluray Remaster}") the file's SOURCE show folder carried when the file name itself has none
    /// (PlexTags.EditionOfSourceShowFolder): a TV episode is then filed in the edition's show folder. Ignored for movies and for a name that
    /// already carries an edition.</summary>
    string? RouteFile(string sourcePath, string desiredFileName, bool isTv, string tvShowBasePath, string movieBasePath, bool moveFiles,
        DestinationCollisionMode collisionMode = DestinationCollisionMode.Overwrite, string? showEdition = null);
}

/// <summary>Ported from Move-CompressarrMovieFile/Move-CompressarrTVFile/Move-CompressarrRoutedFile.</summary>
public sealed class FileRouter : IFileRouter
{
    public string? RouteFile(string sourcePath, string desiredFileName, bool isTv, string tvShowBasePath, string movieBasePath, bool moveFiles,
        DestinationCollisionMode collisionMode = DestinationCollisionMode.Overwrite, string? showEdition = null)
    {
        if (!moveFiles) return null;

        return isTv
            ? MoveTvFile(sourcePath, desiredFileName, tvShowBasePath, collisionMode, showEdition)
            : MoveMovieFile(sourcePath, desiredFileName, movieBasePath, collisionMode);
    }

    /// <summary>Resolves destPath against an existing file at that path per collisionMode: unchanged
    /// for Overwrite (the caller's File.Move(overwrite: true) handles it) or when nothing's there
    /// yet, a uniquified sibling path for Rename, or throws for Skip. A pure static function so
    /// collision behavior is testable independent of an actual file move.</summary>
    internal static string ResolveCollision(string destPath, DestinationCollisionMode collisionMode)
    {
        if (!File.Exists(destPath)) return destPath;

        return collisionMode switch
        {
            DestinationCollisionMode.Skip => throw new DestinationCollisionSkippedException(destPath),
            DestinationCollisionMode.Rename => MakeUniquePath(destPath),
            _ => destPath
        };
    }

    private static string MakeUniquePath(string destPath)
    {
        var dir = Path.GetDirectoryName(destPath) ?? "";
        var nameWithoutExt = Path.GetFileNameWithoutExtension(destPath);
        var ext = Path.GetExtension(destPath);

        var n = 2;
        string candidate;
        do
        {
            candidate = Path.Combine(dir, $"{nameWithoutExt} ({n}){ext}");
            n++;
        } while (File.Exists(candidate));

        return candidate;
    }

    /// <summary>The lane's TV/Movie base folder is where the user's library lives: Compressarr creates the show/season and
    /// movie folders inside it, but never the base folder itself. A base folder that is missing (renamed, a drive or share
    /// that is gone, a typo) is a destination problem to report and retry - not something to quietly recreate, which
    /// would start a second, empty library in the wrong place. Reported as "destination unavailable" (ERROR 102).</summary>
    private static void RequireBaseFolder(string outputBase, string kind)
    {
        if (!Directory.Exists(outputBase))
        {
            throw new DirectoryNotFoundException($"The {kind} base folder '{outputBase}' does not exist. Compressarr creates the folders inside it, but not the base folder itself - fix the path on the Lanes page or restore the folder.");
        }
    }

    public string? MoveTvFile(string sourcePath, string desiredFileName, string outputBase, DestinationCollisionMode collisionMode = DestinationCollisionMode.Overwrite, string? showEdition = null)
    {
        if (string.IsNullOrWhiteSpace(outputBase))
        {
            throw new InvalidOperationException($"Compressarr: cannot move '{sourcePath}' - TV Show base path is not configured for this lane.");
        }

        var info = ContentClassifier.GetEpisodeInfo(desiredFileName);
        if (!info.HasSeasonAndEpisode)
        {
            return null;
        }

        RequireBaseFolder(outputBase, "TV Show");
        // Plex keeps two versions of a show apart by an edition tag on the show's folder. A tag in the file's own show name is already part of
        // the folder name; otherwise the one the source show folder carried is added (and nothing else is ever invented).
        var showFolderName = showEdition is not null && !PlexTags.HasTrailingEdition(info.ShowName) ? info.ShowName + " " + showEdition : info.ShowName;
        var destFolder = Path.Combine(outputBase, showFolderName, "Season " + info.Season);
        Directory.CreateDirectory(destFolder);
        var destPath = ResolveCollision(Path.Combine(destFolder, info.EpisodeFileName), collisionMode);

        File.Move(sourcePath, destPath, overwrite: true);
        return destPath;
    }

    public string? MoveMovieFile(string sourcePath, string desiredFileName, string outputBase, DestinationCollisionMode collisionMode = DestinationCollisionMode.Overwrite)
    {
        if (string.IsNullOrWhiteSpace(outputBase))
        {
            throw new InvalidOperationException($"Compressarr: cannot move '{sourcePath}' - Movie base path is not configured for this lane.");
        }

        var movieFolderName = ContentClassifier.GetMovieFolderName(desiredFileName);

        // Every movie gets its own folder directly under outputBase - no bucket/range-folder
        // auto-detection. An earlier version tried to auto-detect year-bucket folders (e.g.
        // "01. Movies 1920-1979") by scanning outputBase for any folder whose name merely
        // contained "movie", but that matched ordinary movie folders too (a title like "Scary
        // Movie (2026)" contains "Movie") and silently nested every subsequent movie inside
        // whichever one happened to be the sole match - confirmed misrouting real files in
        // production. Bucket folders aren't used, so removed rather than made safer.
        RequireBaseFolder(outputBase, "Movie");
        var movieDestFolder = Path.Combine(outputBase, movieFolderName);
        Directory.CreateDirectory(movieDestFolder);
        var destPath = ResolveCollision(Path.Combine(movieDestFolder, desiredFileName), collisionMode);
        File.Move(sourcePath, destPath, overwrite: true);
        return destPath;
    }
}
