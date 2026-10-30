namespace Compressarr.Core.Routing;

/// <summary>Whether a path lies inside a folder, decided on full paths and whole path segments - never on a text prefix, so "D:\Input2" is not inside "D:\Input".
/// Used wherever the app is about to do something destructive to a source (queue a file, sweep its folder): a lane's Input folder is a boundary, not just the
/// place scans start from.</summary>
public static class PathContainment
{
    /// <summary>True when <paramref name="path"/> is below <paramref name="root"/> - never the root itself. False for a blank, invalid or different-drive path.</summary>
    public static bool IsStrictlyInside(string? path, string? root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;

        try
        {
            var inner = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var outer = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return outer.Length > 0
                && inner.Length > outer.Length
                && inner.StartsWith(outer, StringComparison.OrdinalIgnoreCase)
                && inner[outer.Length] is '\\' or '/';
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>True when <paramref name="directory"/>, or any folder between it and <paramref name="root"/>, is a link (a junction or symlink) - or cannot be inspected. What
    /// lies behind a link is not known to be inside the root, so nothing is removed through one. Expects <paramref name="directory"/> to be inside the root.</summary>
    public static bool CrossesLink(string directory, string root)
    {
        try
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            while (current.Length > rootFull.Length)
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
                current = Path.GetDirectoryName(current) ?? "";
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }
}
