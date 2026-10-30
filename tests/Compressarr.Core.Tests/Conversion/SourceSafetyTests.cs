using System.Diagnostics;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Queue;
using Compressarr.Core.Routing;

namespace Compressarr.Core.Tests.Conversion;

file sealed class RemovingTrashService : ITrashService
{
    public void DeleteFile(string path, DeleteAfterConvertMode mode) { if (File.Exists(path)) File.Delete(path); }
    public void DeleteFolder(string path, DeleteAfterConvertMode mode) { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
}

/// <summary>The rules that keep a source from being deleted, overwritten or swept when it is not the file the conversion was about: what a path is inside, whether a source is still the
/// file that was encoded, how an output that lands on its own source is resolved, and which folders a cleanup may touch at all.</summary>
public class SourceSafetyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("compressarr-sourcesafety-").FullName;

    private readonly List<string> _junctions = new();

    public void Dispose()
    {
        foreach (var junction in _junctions) { try { Directory.Delete(junction, recursive: false); } catch { /* removed with the folder below if it can be */ } }   // the link only, never what it points at
        Directory.Delete(_root, recursive: true);
    }

    private string Folder(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Touch(string folder, string name, string content = "x")
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>A junction (mklink /J): carries the same ReparsePoint flag a symlink does, and needs no privilege. Windows only; false when it could not be made.</summary>
    private bool TryMakeJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        mklink.WaitForExit();
        if (!Directory.Exists(link)) return false;
        _junctions.Add(link);
        return true;
    }

    // ---- PathContainment ---------------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"D:\Input\Show", @"D:\Input", true)]
    [InlineData(@"D:\Input\Show\Season 1\a.mkv", @"D:\Input", true)]
    [InlineData(@"D:\Input\Show\", @"D:\Input\", true)]
    [InlineData(@"d:\input\SHOW", @"D:\Input", true)]
    [InlineData(@"D:\Input", @"D:\Input", false)]                  // the root itself is not inside it
    [InlineData(@"D:\Input2\Show", @"D:\Input", false)]            // a text prefix is not containment
    [InlineData(@"D:\Inputs", @"D:\Input", false)]
    [InlineData(@"D:\", @"D:\Input", false)]                       // a parent
    [InlineData(@"E:\Input\Show", @"D:\Input", false)]             // another drive
    [InlineData(@"D:\Input\..\Other\a.mkv", @"D:\Input", false)]   // relative segments are resolved first
    [InlineData(@"D:\Input\Show\..\..\Other", @"D:\Input", false)]
    [InlineData(@"D:\Input\a.mkv", @"D:\", true)]                  // a drive root as the Input folder
    [InlineData("", @"D:\Input", false)]
    [InlineData(@"D:\Input\a.mkv", "", false)]
    [InlineData(null, @"D:\Input", false)]
    public void IsStrictlyInside_DecidesOnWholePathSegments(string? path, string? root, bool expected)
    {
        if (!OperatingSystem.IsWindows()) return;   // drive letters
        Assert.Equal(expected, PathContainment.IsStrictlyInside(path, root));
    }

    [Fact]
    public void IsStrictlyInside_AnInvalidPath_IsNotInside()
    {
        Assert.False(PathContainment.IsStrictlyInside("a\0b", _root));
    }

    [Fact]
    public void CrossesLink_IsTrueForALinkOnTheWay_AndFalseForPlainFolders()
    {
        var input = Folder("Input");
        var plain = Folder("Input", "Show", "Season 1");
        Assert.False(PathContainment.CrossesLink(plain, input));

        var target = Folder("Elsewhere");
        var link = Path.Combine(input, "Linked");
        if (!TryMakeJunction(link, target)) return;

        Assert.True(PathContainment.CrossesLink(link, input));
        Assert.True(PathContainment.CrossesLink(Path.Combine(link, "Show"), input));
    }

    // ---- source still the file that was encoded ----------------------------------------------------------------------------------------------

    [Fact]
    public void SourceUnchangedSinceEncode_MatchesOnSizeAndTime_AndAnEntryWithNoStampIsNeverTakenAsUnchanged()
    {
        var path = Touch(Folder("Input"), "a.mkv", "twelve bytes");
        var entry = new ResumeEntry { LaneId = "lane1", FullName = path };

        Assert.False(ResumeKeeping.SourceUnchangedSinceEncode(entry));       // no stamp: nothing to compare against is not the same as nothing having changed

        ResumeKeeping.NoteEncodeStart(entry);
        Assert.Equal(12, entry.EncodedFromSize);
        Assert.True(ResumeKeeping.SourceUnchangedSinceEncode(entry));

        File.WriteAllText(path, "a different, longer file");
        Assert.False(ResumeKeeping.SourceUnchangedSinceEncode(entry));       // another size

        ResumeKeeping.NoteEncodeStart(entry);
        File.SetLastWriteTimeUtc(path, entry.EncodedFromModifiedUtc!.Value.AddHours(1));
        Assert.False(ResumeKeeping.SourceUnchangedSinceEncode(entry));       // same size, another time

        File.SetLastWriteTimeUtc(path, entry.EncodedFromModifiedUtc!.Value.AddSeconds(1));
        Assert.True(ResumeKeeping.SourceUnchangedSinceEncode(entry));        // within the rounding file systems do

        File.Delete(path);
        Assert.True(ResumeKeeping.SourceUnchangedSinceEncode(entry));        // nothing there to protect
    }

    [Fact]
    public void SourceUnchangedSinceEncode_ASameSizeReplacementWithTheTimeRestored_IsStillNoticed_ByCreationTimeAndByContent()
    {
        var path = Touch(Folder("Input"), "a.mkv", new string('a', 4096));
        var entry = new ResumeEntry { LaneId = "lane1", FullName = path };
        ResumeKeeping.NoteEncodeStart(entry);
        Assert.NotNull(entry.EncodedFromFingerprint);
        Assert.NotNull(entry.EncodedFromCreatedUtc);
        Assert.True(ResumeKeeping.SourceUnchangedSinceEncode(entry));

        // Different bytes, the same length, the modified time put back: size and time alone cannot tell.
        var modified = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, new string('b', 4096));
        File.SetLastWriteTimeUtc(path, modified);
        Assert.False(ResumeKeeping.SourceUnchangedSinceEncode(entry));       // the content fingerprint does

        File.WriteAllText(path, new string('a', 4096));
        File.SetLastWriteTimeUtc(path, modified);
        Assert.True(ResumeKeeping.SourceUnchangedSinceEncode(entry));        // the same bytes again: the same file as far as can be told

        File.SetCreationTimeUtc(path, entry.EncodedFromCreatedUtc!.Value.AddHours(2));
        Assert.False(ResumeKeeping.SourceUnchangedSinceEncode(entry));       // a file created later (delete and copy in) is another file
    }

    [Fact]
    public void ContentFingerprint_ReadsOnlyTheEnds_AndSeesAChangeInEither()
    {
        var folder = Folder("Input");
        var bytes = new byte[3 * 1024 * 1024 + 123];
        new Random(7).NextBytes(bytes);
        var path = Path.Combine(folder, "big.mkv");
        File.WriteAllBytes(path, bytes);
        var original = ResumeKeeping.ContentFingerprint(path);

        Assert.Equal(original, ResumeKeeping.ContentFingerprint(path));                       // stable
        bytes[10] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        Assert.NotEqual(original, ResumeKeeping.ContentFingerprint(path));                    // the head changed
        bytes[10] ^= 0xFF;
        bytes[^10] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        Assert.NotEqual(original, ResumeKeeping.ContentFingerprint(path));                    // the tail changed
        Assert.Null(ResumeKeeping.ContentFingerprint(Path.Combine(folder, "missing.mkv")));   // unreadable is not a fingerprint
    }

    [Fact]
    public void CheckSource_WithNoStamp_OnlyASourceNewerThanItsEncodeIsKnownToDiffer_AnOlderOneIsUnknown_NeverUnchanged()
    {
        var folder = Folder("Input");
        var source = Touch(folder, "a.mkv");
        var staged = Touch(folder, "a.compressarr-1.mkv");
        var entry = new ResumeEntry { LaneId = "lane1", FullName = source };

        File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddDays(-5));
        File.SetCreationTimeUtc(source, DateTime.UtcNow.AddDays(-5));
        File.SetLastWriteTimeUtc(staged, DateTime.UtcNow.AddDays(-1));
        Assert.Equal(ResumeKeeping.SourceCheck.Unknown, ResumeKeeping.CheckSource(entry, staged));        // older than the encode proves nothing: a different file can carry an old date
        Assert.False(ResumeKeeping.SourceUnchangedSinceEncode(entry, staged));

        File.SetLastWriteTimeUtc(source, DateTime.UtcNow);
        Assert.Equal(ResumeKeeping.SourceCheck.Changed, ResumeKeeping.CheckSource(entry, staged));        // written after it finished: another file
        Assert.Equal(ResumeKeeping.SourceCheck.Unknown, ResumeKeeping.CheckSource(entry));                // nothing to compare with: unknown, not "fine"
        Assert.Equal(ResumeKeeping.SourceCheck.Unknown, ResumeKeeping.CheckSource(entry, Path.Combine(folder, "gone.mkv")));

        File.Delete(source);
        Assert.Equal(ResumeKeeping.SourceCheck.Unchanged, ResumeKeeping.CheckSource(entry, staged));      // nothing there to delete or overwrite
    }

    [Fact]
    public void CheckSource_AV7StampWithNoFingerprint_IsUnknownWhenSizeAndTimeMatch_AndChangedWhenTheyDoNot()
    {
        var path = Touch(Folder("Input"), "a.mkv", new string('a', 4096));
        var info = new FileInfo(path);
        var entry = new ResumeEntry { LaneId = "lane1", FullName = path, EncodedFromSize = info.Length, EncodedFromModifiedUtc = info.LastWriteTimeUtc };    // what an earlier 3.0 build wrote

        Assert.Equal(ResumeKeeping.SourceCheck.Unknown, ResumeKeeping.CheckSource(entry));      // same size and time say nothing about the bytes
        File.WriteAllText(path, new string('z', 4096));
        File.SetLastWriteTimeUtc(path, entry.EncodedFromModifiedUtc!.Value);
        Assert.Equal(ResumeKeeping.SourceCheck.Unknown, ResumeKeeping.CheckSource(entry));      // different bytes, same length, time put back: still cannot be told, so never Unchanged
        Assert.False(ResumeKeeping.SourceUnchangedSinceEncode(entry));

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(1));
        Assert.Equal(ResumeKeeping.SourceCheck.Changed, ResumeKeeping.CheckSource(entry));
    }

    [Fact]
    public void ARealCleanupAfterVideoB_UnderSkip_NeverRemovesTheCompanionAVideoFreeChildHoldsForA()
    {
        // Input/batch/A-source/A.en.srt (left by a Skip when A finished) and Input/batch/B.mkv, which finishes later: the real coordinator, companion service and a recursive delete.
        var input = Folder("Input");
        var batch = Folder("Input", "batch");
        var aSource = Folder("Input", "batch", "A-source");
        var subtitle = Touch(aSource, "A.en.srt", "unique subtitle");
        var config = new CompressarrConfig();
        config.Processing.DeleteAfterConvert = DeleteAfterConvertMode.Delete;
        config.Processing.UnmatchedCompanionAction = DeleteAfterConvertMode.Delete;
        config.Processing.OnDestinationCollision = DestinationCollisionMode.Skip;
        var coordinator = new SourceCleanupCoordinator(new CoordinatorTestArr(), new CompanionFileService(new RemovingTrashService()), new CoordinatorTestLogger());

        var error = coordinator.TryCleanUpSourceFolder(config, batch, input, "", Path.Combine(batch, "B.mkv"));

        Assert.Null(error);
        Assert.Equal("unique subtitle", File.ReadAllText(subtitle));
        Assert.True(Directory.Exists(batch));
    }

    // ---- an output that lands on its own source ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(DeleteAfterConvertMode.Maintain, true, DestinationCollisionMode.Rename, false)]    // keep the source: the output takes the next free name
    [InlineData(DeleteAfterConvertMode.Delete, false, DestinationCollisionMode.Rename, false)]     // a source that is no longer the encoded file is kept too
    [InlineData(DeleteAfterConvertMode.Recycle, false, DestinationCollisionMode.Rename, false)]
    [InlineData(DeleteAfterConvertMode.Delete, true, DestinationCollisionMode.Overwrite, true)]    // the source goes the way the setting says first
    [InlineData(DeleteAfterConvertMode.Recycle, true, DestinationCollisionMode.Overwrite, true)]
    public void OutputCollisionMode_WhenTheOutputWouldReplaceItsOwnSource(DeleteAfterConvertMode keep, bool sourceUnchanged, DestinationCollisionMode expectedMode, bool expectedDisposeFirst)
    {
        var source = Touch(Folder("Input"), "Movie.mkv");

        var mode = FileOutcomeHelpers.OutputCollisionMode(source, source, DestinationCollisionMode.Overwrite, keep, sourceUnchanged, out var disposeFirst);

        Assert.Equal(expectedMode, mode);
        Assert.Equal(expectedDisposeFirst, disposeFirst);
    }

    [Fact]
    public void OutputCollisionMode_LeavesEveryOtherCaseAlone()
    {
        var input = Folder("Input");
        var source = Touch(input, "Movie.mkv");
        var other = Path.Combine(input, "Movie (2).mkv");

        // another destination, a policy that is not Overwrite, or no source at the path: nothing to protect, nothing to dispose of
        Assert.Equal(DestinationCollisionMode.Overwrite, FileOutcomeHelpers.OutputCollisionMode(other, source, DestinationCollisionMode.Overwrite, DeleteAfterConvertMode.Maintain, true, out var a));
        Assert.Equal(DestinationCollisionMode.Skip, FileOutcomeHelpers.OutputCollisionMode(source, source, DestinationCollisionMode.Skip, DeleteAfterConvertMode.Maintain, true, out var b));
        Assert.Equal(DestinationCollisionMode.Overwrite, FileOutcomeHelpers.OutputCollisionMode(source + ".gone", source + ".gone", DestinationCollisionMode.Overwrite, DeleteAfterConvertMode.Delete, true, out var c));
        Assert.False(a || b || c);
    }

    // ---- what a cleanup may touch ------------------------------------------------------------------------------------------------------------

    private static readonly string[] Videos = { "mkv", "mp4" };

    [Fact]
    public void CleanUpEmptySourceFolder_AFolderOutsideTheInputRoot_IsNeitherSweptNorRemoved()
    {
        var input = Folder("Input");
        var outside = Folder("Elsewhere", "Personal");
        var diary = Touch(outside, "diary.txt", "personal");

        new CompanionFileService(new RemovingTrashService()).CleanUpEmptySourceFolder(outside, input, Videos, DeleteAfterConvertMode.Delete, DeleteAfterConvertMode.Delete);

        Assert.True(File.Exists(diary));
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public void CleanUpEmptySourceFolder_ASiblingWhoseNameStartsLikeTheRoot_IsNotInsideIt()
    {
        var input = Folder("Input");
        var sibling = Folder("Input2", "Show");
        var note = Touch(sibling, "note.txt");

        new CompanionFileService(new RemovingTrashService()).CleanUpEmptySourceFolder(sibling, input, Videos, DeleteAfterConvertMode.Delete, DeleteAfterConvertMode.Delete);

        Assert.True(File.Exists(note));
    }

    [Fact]
    public void CleanUpEmptySourceFolder_TheRootItself_AMissingRoot_AndABlankRoot_AreLeftAlone()
    {
        var input = Folder("Input");
        var leftover = Touch(input, "notes.txt");
        var service = new CompanionFileService(new RemovingTrashService());

        service.CleanUpEmptySourceFolder(input, input, Videos, DeleteAfterConvertMode.Delete, DeleteAfterConvertMode.Delete);
        service.CleanUpEmptySourceFolder(input, Path.Combine(_root, "NoSuchRoot"), Videos, DeleteAfterConvertMode.Delete, DeleteAfterConvertMode.Delete);
        service.CleanUpEmptySourceFolder(input, "", Videos, DeleteAfterConvertMode.Delete, DeleteAfterConvertMode.Delete);

        Assert.True(File.Exists(leftover));
    }

    [Fact]
    public void CleanUpEmptySourceFolder_InsideTheRoot_StillSweepsAndRemovesTheFolderAndEmptyParents()
    {
        var input = Folder("Input");
        var folder = Folder("Input", "Show", "Season 1");
        Touch(folder, "poster.txt");

        new CompanionFileService(new RemovingTrashService()).CleanUpEmptySourceFolder(folder, input, Videos, DeleteAfterConvertMode.Delete, DeleteAfterConvertMode.Delete);

        Assert.False(Directory.Exists(Path.Combine(input, "Show")));   // the folder and its emptied parent are gone ...
        Assert.True(Directory.Exists(input));                          // ... the root is not
    }

    [Fact]
    public void CleanUpEmptySourceFolder_ALinkOnTheWay_IsNotFollowed()
    {
        var input = Folder("Input");
        var target = Folder("Elsewhere");
        var precious = Touch(target, "precious.txt");
        var link = Path.Combine(input, "Linked");
        if (!TryMakeJunction(link, target)) return;

        new CompanionFileService(new RemovingTrashService()).CleanUpEmptySourceFolder(link, input, Videos, DeleteAfterConvertMode.Delete, DeleteAfterConvertMode.Delete);

        Assert.True(File.Exists(precious));
    }

    // ---- a companion left on purpose ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void WantedCompanionRemains_OnlyForAFileOfTheVideosNameWithAWantedExtension()
    {
        var folder = Folder("Input", "Movie");
        var video = Path.Combine(folder, "Movie (2020).mkv");   // already gone, as after a delete: only its name matters
        var wanted = new[] { "srt", "nfo" };

        Assert.False(CompanionFileService.WantedCompanionRemains(folder, video, wanted));          // empty folder

        Touch(folder, "notes.txt");
        Touch(folder, "Movie (2020) extras.srt");                                                    // another name (no '.' after the stem)
        Touch(folder, "Other.en.srt");
        Touch(folder, "Movie (2020).txt");                                                           // the video's name, but not a wanted type
        Assert.False(CompanionFileService.WantedCompanionRemains(folder, video, wanted));

        Touch(folder, "Movie (2020).en.srt");
        Assert.True(CompanionFileService.WantedCompanionRemains(folder, video, wanted));
        Assert.False(CompanionFileService.WantedCompanionRemains(Path.Combine(_root, "NoSuchFolder"), video, wanted));
    }

    // ---- queue admission ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void FindOrCreatePendingEntry_OnlyAdmitsAFileStrictlyInsideTheLanesInput()
    {
        var input = Folder("Input");
        var inside = Touch(Folder("Input", "Show"), "a.mkv");
        var outside = Touch(Folder("Elsewhere"), "b.mkv");
        var sibling = Touch(Folder("Input2"), "c.mkv");
        var state = new List<ResumeEntry>();

        Assert.NotNull(QueueRules.FindOrCreatePendingEntry(state, "lane1", inside, input));
        Assert.Null(QueueRules.FindOrCreatePendingEntry(state, "lane1", outside, input));
        Assert.Null(QueueRules.FindOrCreatePendingEntry(state, "lane1", sibling, input));
        Assert.Null(QueueRules.FindOrCreatePendingEntry(state, "lane1", Path.Combine(input, "..", "Elsewhere", "b.mkv"), input));
        Assert.Null(QueueRules.FindOrCreatePendingEntry(state, "lane1", input, input));
        Assert.Single(state);
    }
}
