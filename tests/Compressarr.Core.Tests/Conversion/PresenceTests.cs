using System.Diagnostics;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Routing;

namespace Compressarr.Core.Tests.Conversion;

/// <summary>A folder reached through a junction whose target can be taken offline and brought back: while it is offline the path is still there (the junction shows in its parent) but cannot be followed,
/// which is how a dropped share or an unmounted volume looks - <see cref="File.Exists"/> answers false for everything below it, though the files are on disk. Windows only; <see cref="Create"/> returns
/// null when the junction cannot be made (the caller then skips). The folder behind it is filled by <c>populate</c> BEFORE the junction exists (some hosts refuse to create things through a junction).</summary>
internal sealed class OfflineJunction : IDisposable
{
    public string Link { get; }
    private readonly string _target;
    private readonly string _away;

    private OfflineJunction(string link, string target) { Link = link; _target = target; _away = target + "-offline"; }

    public static OfflineJunction? Create(string root, Action<string> populate)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var target = Path.Combine(root, "volume");
        var link = Path.Combine(root, "mount");
        Directory.CreateDirectory(target);
        populate(target);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
        process!.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link) ? new OfflineJunction(link, target) : null;
    }

    /// <summary>The real folder behind the junction.</summary>
    public string Target => _target;
    public void TakeOffline() => Directory.Move(_target, _away);
    public void BringOnline() => Directory.Move(_away, _target);

    public void Dispose()
    {
        try { if (Directory.Exists(Link)) Directory.Delete(Link); } catch { /* best effort */ }
    }
}

/// <summary>A file that cannot be reached is not a file that is gone: <see cref="File.Exists"/> answers false for both, so recovery uses <see cref="FileOutcomeHelpers.PresenceOf"/>.</summary>
public class PresenceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("compressarr-presence-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private sealed class SwallowingTrash : ITrashService
    {
        public void DeleteFile(string path, DeleteAfterConvertMode mode) { }
        public void DeleteFolder(string path, DeleteAfterConvertMode mode) { }
    }

    [Fact]
    public void AFileThatIsThere_AFileThatIsGone_AndAFolderThatIsGone_AreTold_Apart()
    {
        var folder = Path.Combine(_dir, "film");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "a.mkv");
        File.WriteAllText(file, "x");

        Assert.Equal(FileOutcomeHelpers.FilePresence.Present, FileOutcomeHelpers.PresenceOf(file));
        Assert.Equal(FileOutcomeHelpers.FilePresence.Absent, FileOutcomeHelpers.PresenceOf(Path.Combine(folder, "gone.mkv")));
        Assert.Equal(FileOutcomeHelpers.FilePresence.Absent, FileOutcomeHelpers.PresenceOf(Path.Combine(folder, ".compressarr-original-0123456789abcdef", "a.mkv")));   // a parked folder that was removed
        Assert.Equal(FileOutcomeHelpers.FilePresence.Absent, FileOutcomeHelpers.PresenceOf(Path.Combine(_dir, "no", "such", "folder", "a.mkv")));                          // a whole folder chain that was removed
    }

    [Fact]
    public void AFileThatCannotBeReached_IsUnknown_NotAbsent_AndComesBackWhenItCan()
    {
        var relative = Path.Combine("film", ".compressarr-original-0123456789abcdef", "a.mkv");
        using var mount = OfflineJunction.Create(_dir, target =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(target, relative))!);
            File.WriteAllText(Path.Combine(target, relative), "x");
        });
        if (mount is null) return;   // junctions are Windows only (and may be refused on a locked-down host)
        var file = Path.Combine(mount.Link, relative);
        Assert.Equal(FileOutcomeHelpers.FilePresence.Present, FileOutcomeHelpers.PresenceOf(file));

        mount.TakeOffline();
        Assert.False(File.Exists(file));                                                              // the trap: File.Exists says "not there"
        Assert.Equal(FileOutcomeHelpers.FilePresence.Unknown, FileOutcomeHelpers.PresenceOf(file));
        Assert.False(FileOutcomeHelpers.DisposeSource(new SwallowingTrash(), file, DeleteAfterConvertMode.Delete));   // a removal is not confirmed by it either

        mount.BringOnline();
        Assert.Equal(FileOutcomeHelpers.FilePresence.Present, FileOutcomeHelpers.PresenceOf(file));
    }
}
