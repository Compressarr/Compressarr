using System.Diagnostics;
using Compressarr.Core.Conversion;

namespace Compressarr.Core.Tests.Conversion;

public class VideoFileScannerTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("compressarr-scanner-tests-").FullName;
    private readonly VideoFileScanner _scanner = new();

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void FindVideoFiles_NestedSubfolders_ScansRecursively()
    {
        var sub = Path.Combine(_tempDir, "Show", "Season 01");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(_tempDir, "top.mkv"), "video");
        File.WriteAllText(Path.Combine(sub, "episode.mkv"), "video");

        var results = _scanner.FindVideoFiles(_tempDir, new[] { "mkv" }, minSizeBytes: 0, limit: 0);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void FindVideoFiles_SkipsAnOriginalParkedForAReplacement_ItIsNeverNewWork()
    {
        var film = Path.Combine(_tempDir, "Movie (2020)");
        var parked = Path.Combine(film, FileOutcomeHelpers.ParkedOriginalFolderPrefix + "0123456789abcdef");
        Directory.CreateDirectory(parked);
        File.WriteAllText(Path.Combine(film, "Movie (2020).mkv"), "the published encode");
        File.WriteAllText(Path.Combine(parked, "Movie (2020).mkv"), "the converted original, waiting to be removed");

        var results = _scanner.FindVideoFiles(_tempDir, new[] { "mkv" }, minSizeBytes: 0, limit: 0);

        Assert.Equal(Path.Combine(film, "Movie (2020).mkv"), Assert.Single(results).FullName);
    }

    [Fact]
    public void FindVideoFiles_LimitCapsHowManyFilesAreReturned_AndZeroMeansNoLimit()
    {
        // Settings' "Max files per run" - a folder that suddenly fills up shouldn't be picked up
        // all at once. 0 (the default) means unlimited.
        for (var i = 0; i < 5; i++) File.WriteAllText(Path.Combine(_tempDir, $"f{i}.mkv"), "video");

        Assert.Equal(2, _scanner.FindVideoFiles(_tempDir, new[] { "mkv" }, minSizeBytes: 0, limit: 2).Count);
        Assert.Equal(5, _scanner.FindVideoFiles(_tempDir, new[] { "mkv" }, minSizeBytes: 0, limit: 0).Count);
        Assert.Equal(5, _scanner.FindVideoFiles(_tempDir, new[] { "mkv" }, minSizeBytes: 0, limit: 99).Count);
    }

    [Fact]
    public void FindVideoFiles_ReparsePointDirectory_IsNotTraversed()
    {
        // Real bug flagged in the v2.1.3 code review: the scanner descended into every
        // subdirectory unconditionally, including a junction/symlink that could point outside
        // the lane's Input tree entirely (or loop back upward into it) - a real risk here since
        // Compressarr later moves and deletes whatever files it finds.
        //
        // Uses a junction (mklink /J), not Directory.CreateSymbolicLink - a real symlink needs
        // elevation or Developer Mode on Windows, which this sandbox has neither of, but a
        // junction needs no special privilege at all and carries the exact same
        // FileAttributes.ReparsePoint flag the scanner's guard actually checks for. Junctions
        // (and cmd.exe/mklink) are Windows-only, matching this codebase's own Windows-first test
        // environment - xUnit v2 has no dynamic per-platform skip, so this opts out via an early
        // return rather than failing on a platform the guard itself still applies to (a real
        // symlink) just not via this specific test's setup mechanism.
        if (!OperatingSystem.IsWindows()) return;

        var outsideDir = Directory.CreateTempSubdirectory("compressarr-scanner-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outsideDir, "should-not-be-found.mkv"), "video");

            var linkPath = Path.Combine(_tempDir, "escape-link");
            var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{outsideDir}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            mklink.WaitForExit();
            Assert.True(Directory.Exists(linkPath), $"Test setup failed to create junction: {mklink.StandardError.ReadToEnd()}");

            File.WriteAllText(Path.Combine(_tempDir, "inside.mkv"), "video");

            var results = _scanner.FindVideoFiles(_tempDir, new[] { "mkv" }, minSizeBytes: 0, limit: 0);

            Assert.Single(results);
            Assert.Equal("inside.mkv", results[0].Name);
        }
        finally
        {
            try { Directory.Delete(outsideDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void FindVideoFiles_RespectsExtensionAndMinSize()
    {
        File.WriteAllText(Path.Combine(_tempDir, "big.mkv"), new string('a', 100));
        File.WriteAllText(Path.Combine(_tempDir, "small.mkv"), "a");
        File.WriteAllText(Path.Combine(_tempDir, "ignored.txt"), new string('a', 100));

        var results = _scanner.FindVideoFiles(_tempDir, new[] { "mkv" }, minSizeBytes: 50, limit: 0);

        Assert.Single(results);
        Assert.Equal("big.mkv", results[0].Name);
    }
}
