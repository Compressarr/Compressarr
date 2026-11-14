namespace Compressarr.Core.Conversion;

public interface IVideoFileScanner
{
    /// <summary>Recursively finds video files under inputPath matching vidTypes (extensions
    /// without a leading dot), above minSizeBytes, capped at limit results.</summary>
    IReadOnlyList<FileInfo> FindVideoFiles(string inputPath, IReadOnlyList<string> vidTypes, long minSizeBytes, int limit);
}

/// <summary>Ported from Find-CompressarrVideoFiles.</summary>
public sealed class VideoFileScanner : IVideoFileScanner
{
    public IReadOnlyList<FileInfo> FindVideoFiles(string inputPath, IReadOnlyList<string> vidTypes, long minSizeBytes, int limit)
    {
        if (!Directory.Exists(inputPath)) return Array.Empty<FileInfo>();

        var extensions = vidTypes
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => "." + t.Trim().TrimStart('.'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var effectiveLimit = limit > 0 ? limit : int.MaxValue;

        var results = new List<FileInfo>();
        foreach (var path in EnumerateFilesSafe(inputPath))
        {
            var file = new FileInfo(path);
            if (!extensions.Contains(file.Extension)) continue;
            if (file.Length <= minSizeBytes) continue;

            results.Add(file);
            if (results.Count >= effectiveLimit) break;
        }

        return results;
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        // Get-ChildItem -Recurse -ErrorAction SilentlyContinue tolerates unreadable
        // subdirectories mid-walk rather than aborting the whole scan; Directory.EnumerateFiles
        // throws on the first one, so this walks the tree manually to match that behavior.
        var stack = new Stack<string>();

        // Guards against a junction/symlink pointing back upward into the tree it's inside (an
        // infinite loop) or a legitimate non-reparse-point path alias resolving to the same real
        // directory twice - belt-and-suspenders alongside the reparse-point check below, which
        // handles the far more common case directly.
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rootCanonical = TryGetCanonicalPath(root);
        if (rootCanonical is not null) visited.Add(rootCanonical);
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var file in files) yield return file;

            IEnumerable<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(dir);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var subDir in subDirs)
            {
                // A junction/symlink can point completely outside the lane's Input tree, or loop
                // back upward into it - Compressarr later moves and deletes whatever it finds
                // here, so unlike a purely read-only listing this needs to actively refuse to
                // follow one, not just tolerate whatever's there.
                FileAttributes attrs;
                try
                {
                    attrs = File.GetAttributes(subDir);
                }
                catch (UnauthorizedAccessException) { continue; }
                catch (IOException) { continue; }

                if (attrs.HasFlag(FileAttributes.ReparsePoint)) continue;

                // An original set aside for a same-path replacement (see FileOutcomeHelpers.PublishOverSource) is a full-size video that has already been converted, waiting to be
                // removed: it is never new work, and queueing it would encode the same film again.
                if (Path.GetFileName(subDir).StartsWith(FileOutcomeHelpers.ParkedOriginalFolderPrefix, StringComparison.OrdinalIgnoreCase)) continue;

                var canonical = TryGetCanonicalPath(subDir);
                if (canonical is not null && !visited.Add(canonical)) continue;

                stack.Push(subDir);
            }
        }
    }

    private static string? TryGetCanonicalPath(string path)
    {
        try { return new DirectoryInfo(path).FullName; }
        catch { return null; }
    }
}
