using Compressarr.Core.Config;
using Compressarr.Core.Routing;

namespace Compressarr.Core.Tests.Routing;

/// <summary>Deletes for real (both Delete and Recycle modes) so tests can assert on actual
/// filesystem state without depending on OS recycle-bin behavior — CompanionFileService's own
/// routing/guard logic is what's under test here, not the trash backend.</summary>
file sealed class FakeTrashService : ITrashService
{
    public void DeleteFile(string path, DeleteAfterConvertMode mode)
    {
        if (mode == DeleteAfterConvertMode.Maintain) return;
        if (File.Exists(path)) File.Delete(path);
    }

    public void DeleteFolder(string path, DeleteAfterConvertMode mode)
    {
        if (mode == DeleteAfterConvertMode.Maintain) return;
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}

public class CompanionFileServiceTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("compressarr-companion-tests-").FullName;
    private readonly CompanionFileService _service = new(new FakeTrashService());
    private static readonly string[] VidTypes = { "mkv", "mp4" };
    private static readonly string[] CompanionExtensions = { "srt" };

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    // The video's own routed destination path - companions always take their destination
    // filename from THIS path's stem, never from their own original name (confirmed: companion
    // filenames must always match the video's own filename). Most tests route to a name that
    // equals the source video's own name, matching the pre-fix tests' original behavior; a few
    // deliberately route to a DIFFERENT name to prove the new stem-following behavior itself.
    private static string RoutedPath(string destDir, string fileName) => Path.Combine(destDir, fileName);

    // ---- MoveCompanionFiles: matching/moving companions alongside the video - never touches
    // the source folder itself (see CleanUpEmptySourceFolder below for that half) ----

    [Fact]
    public void MoveCompanionFiles_OtherVideoStillInFolder_StillMovesThisFilesOwnCompanion()
    {
        // Mirrors the real production layout: a whole season's episodes and subtitles sitting
        // flat in one shared lane Input folder (no per-episode subfolders). Reported live: waiting
        // for "this is the only video left" before moving anything meant a file's own subtitle sat
        // stranded until the WHOLE batch finished - or never, if a sibling was skipped/removed, or
        // even just because Stop Monitoring landed right after this one file finished. Companions
        // now move per-file, by name match, regardless of what else is still in the folder.
        var sourceDir = Path.Combine(_tempDir, "Input", "Show");
        Directory.CreateDirectory(sourceDir);
        var converted = Path.Combine(sourceDir, "episode1.mkv");
        var otherVideo = Path.Combine(sourceDir, "episode2.mkv");
        var subtitle = Path.Combine(sourceDir, "episode1.srt");
        File.WriteAllText(converted, "x");
        File.WriteAllText(otherVideo, "x");
        File.WriteAllText(subtitle, "x");

        var destDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(destDir);

        _service.MoveCompanionFiles(converted, sourceDir, RoutedPath(destDir, "episode1.mkv"), DeleteAfterConvertMode.Delete, CompanionExtensions, DeleteAfterConvertMode.Recycle);

        Assert.True(File.Exists(Path.Combine(destDir, "episode1.srt")), "this file's own subtitle must move even while another video is still here");
        Assert.True(File.Exists(otherVideo), "the other, still-untouched video must survive");
    }

    [Fact]
    public void MoveCompanionFiles_AnotherVideosOwnCompanion_IsNeverTouchedByThisFilesMove()
    {
        // Stem-based matching must stay scoped to THIS file only - confirmed live, an earlier
        // version of this logic swept a sibling's own subtitle right along with the real one the
        // moment some "is it safe to sweep now" guard passed.
        var sourceDir = Path.Combine(_tempDir, "Input");
        Directory.CreateDirectory(sourceDir);
        var converted = Path.Combine(sourceDir, "episode1.mkv");
        var convertedSubtitle = Path.Combine(sourceDir, "episode1.eng.srt");
        var otherVideo = Path.Combine(sourceDir, "episode2.mkv");
        var otherSubtitle = Path.Combine(sourceDir, "episode2.eng.srt");
        File.WriteAllText(converted, "x");
        File.WriteAllText(convertedSubtitle, "x");
        File.WriteAllText(otherVideo, "x");
        File.WriteAllText(otherSubtitle, "x");

        var destDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(destDir);

        _service.MoveCompanionFiles(converted, sourceDir, RoutedPath(destDir, "episode1.mkv"), DeleteAfterConvertMode.Delete, CompanionExtensions, DeleteAfterConvertMode.Recycle);

        Assert.True(File.Exists(Path.Combine(destDir, "episode1.eng.srt")), "the processed file's own subtitle must move");
        Assert.True(File.Exists(otherVideo), "the other video must survive");
        Assert.True(File.Exists(otherSubtitle), "the other video's OWN subtitle must survive too - not this file's to touch");
        Assert.False(File.Exists(Path.Combine(destDir, "episode2.eng.srt")), "the other subtitle must not be moved to destination");
    }

    [Fact]
    public void MoveCompanionFiles_MaintainMode_CopiesSiblingsAndLeavesSourceUntouched()
    {
        var sourceDir = Path.Combine(_tempDir, "Input", "Movie");
        Directory.CreateDirectory(sourceDir);
        var converted = Path.Combine(sourceDir, "movie.mkv");
        var subtitle = Path.Combine(sourceDir, "movie.srt");
        File.WriteAllText(converted, "x");
        File.WriteAllText(subtitle, "x");

        var destDir = Path.Combine(_tempDir, "Output", "Movie");
        Directory.CreateDirectory(destDir);

        _service.MoveCompanionFiles(converted, sourceDir, RoutedPath(destDir, "movie.mkv"), DeleteAfterConvertMode.Maintain, CompanionExtensions, DeleteAfterConvertMode.Recycle);

        Assert.True(File.Exists(subtitle), "source sibling must survive Maintain mode");
        Assert.True(File.Exists(Path.Combine(destDir, "movie.srt")), "sibling must be copied to destination");
    }

    [Fact]
    public void MoveCompanionFiles_StemMatchedFileNotOnCompanionList_IsRemovedNotMoved()
    {
        // The bug this whole feature exists to fix: a stem-matched file that isn't a recognized
        // companion type must never be blindly moved (or, in the old design, blindly deleted
        // along with genuinely unrelated content) - it's disposed of per unmatchedCompanionAction
        // instead, independent of what companion types are actually configured.
        var sourceDir = Path.Combine(_tempDir, "Input", "Movie");
        Directory.CreateDirectory(sourceDir);
        var converted = Path.Combine(sourceDir, "movie.mkv");
        var subtitle = Path.Combine(sourceDir, "movie.srt");
        var unwantedStemMatch = Path.Combine(sourceDir, "movie.bak");
        File.WriteAllText(converted, "x");
        File.WriteAllText(subtitle, "x");
        File.WriteAllText(unwantedStemMatch, "x");

        var destDir = Path.Combine(_tempDir, "Output", "Movie");
        Directory.CreateDirectory(destDir);

        _service.MoveCompanionFiles(converted, sourceDir, RoutedPath(destDir, "movie.mkv"), DeleteAfterConvertMode.Delete, CompanionExtensions, DeleteAfterConvertMode.Recycle);

        Assert.True(File.Exists(Path.Combine(destDir, "movie.srt")), "a recognized companion type must still move");
        Assert.False(File.Exists(unwantedStemMatch), "a stem-matched file not on the companion list must be removed per unmatchedCompanionAction");
        Assert.False(File.Exists(Path.Combine(destDir, "movie.bak")), "an unrecognized file type must never be moved to the destination");
    }

    // ---- v2.1.3 code review finding #5: companion collision handling ----

    [Fact]
    public void MoveCompanionFiles_VideoWasRenamedOnCollision_CompanionFollowsTheSameRenamedStem()
    {
        // The exact scenario the code review called out: "Movie (2).mkv could be created while
        // Movie.en.srt is overwritten." A companion's destination filename must always be derived
        // from the VIDEO's own actual routed name, not its own original one - confirmed as the
        // required behavior, not just a nice-to-have.
        var sourceDir = Path.Combine(_tempDir, "Input", "Movie");
        Directory.CreateDirectory(sourceDir);
        var converted = Path.Combine(sourceDir, "movie.mkv");
        var subtitle = Path.Combine(sourceDir, "movie.en.srt");
        File.WriteAllText(converted, "x");
        File.WriteAllText(subtitle, "x");

        var destDir = Path.Combine(_tempDir, "Output", "Movie");
        Directory.CreateDirectory(destDir);

        // Simulates FileRouter having already resolved a video-level collision via Rename -
        // "movie.mkv" landed as "movie (2).mkv", not its own original name.
        var routedVideoPath = Path.Combine(destDir, "movie (2).mkv");

        _service.MoveCompanionFiles(converted, sourceDir, routedVideoPath, DeleteAfterConvertMode.Delete, CompanionExtensions, DeleteAfterConvertMode.Recycle, DestinationCollisionMode.Rename);

        Assert.True(File.Exists(Path.Combine(destDir, "movie (2).en.srt")), "companion must land under the video's own renamed stem");
        Assert.False(File.Exists(Path.Combine(destDir, "movie.en.srt")), "companion must not use its own original stem once the video was renamed");
        Assert.False(File.Exists(subtitle), "source companion must have been moved, not left behind");
    }

    [Fact]
    public void MoveCompanionFiles_CollisionModeSkip_LeavesTheCollidingCompanionInPlaceButStillHandlesOthers()
    {
        var sourceDir = Path.Combine(_tempDir, "Input", "Movie");
        Directory.CreateDirectory(sourceDir);
        var converted = Path.Combine(sourceDir, "movie.mkv");
        var subtitle = Path.Combine(sourceDir, "movie.en.srt");
        var nfo = Path.Combine(sourceDir, "movie.nfo");
        File.WriteAllText(converted, "x");
        File.WriteAllText(subtitle, "NEW SUBTITLE");
        File.WriteAllText(nfo, "x");

        var destDir = Path.Combine(_tempDir, "Output", "Movie");
        Directory.CreateDirectory(destDir);
        // An existing subtitle already sits at the destination - the fresh one must be skipped,
        // not silently overwritten, when the user has configured Skip.
        var existingDestSubtitle = Path.Combine(destDir, "movie.en.srt");
        File.WriteAllText(existingDestSubtitle, "OLD SUBTITLE");

        _service.MoveCompanionFiles(converted, sourceDir, RoutedPath(destDir, "movie.mkv"), DeleteAfterConvertMode.Delete, new[] { "srt", "nfo" }, DeleteAfterConvertMode.Recycle, DestinationCollisionMode.Skip);

        // The colliding companion: left exactly as configured, neither side touched.
        Assert.True(File.Exists(subtitle), "the new subtitle must stay in source when its destination collision is configured to Skip");
        Assert.Equal("NEW SUBTITLE", File.ReadAllText(subtitle));
        Assert.Equal("OLD SUBTITLE", File.ReadAllText(existingDestSubtitle));
        // A DIFFERENT companion with no collision at all must still move normally - one Skip must
        // not abort handling of the rest.
        Assert.True(File.Exists(Path.Combine(destDir, "movie.nfo")), "an unrelated, non-colliding companion must still move");
        Assert.False(File.Exists(nfo), "...and be gone from the source");
    }

    [Fact]
    public void MoveCompanionFiles_TwoCompanionsShareAnExtension_BothMoveWithoutCollidingWithEachOther()
    {
        // Companions are matched (and their destination name derived) from their FULL filename,
        // not just their extension - "movie.eng.srt" and "movie.eng.SDH.srt" are two independent
        // companions that happen to share the .srt extension. Each keeps its own distinguishing
        // middle segment when its destination name is built from the video's own stem, so neither
        // one collides with the other even though both are "movie.<something>.srt".
        var sourceDir = Path.Combine(_tempDir, "Input", "Movie");
        Directory.CreateDirectory(sourceDir);
        var converted = Path.Combine(sourceDir, "movie.mkv");
        var standardSubtitle = Path.Combine(sourceDir, "movie.eng.srt");
        var sdhSubtitle = Path.Combine(sourceDir, "movie.eng.SDH.srt");
        File.WriteAllText(converted, "x");
        File.WriteAllText(standardSubtitle, "standard");
        File.WriteAllText(sdhSubtitle, "sdh");

        var destDir = Path.Combine(_tempDir, "Output", "Movie");
        Directory.CreateDirectory(destDir);

        _service.MoveCompanionFiles(converted, sourceDir, RoutedPath(destDir, "movie.mkv"), DeleteAfterConvertMode.Delete, CompanionExtensions, DeleteAfterConvertMode.Recycle, DestinationCollisionMode.Overwrite);

        Assert.True(File.Exists(Path.Combine(destDir, "movie.eng.srt")));
        Assert.True(File.Exists(Path.Combine(destDir, "movie.eng.SDH.srt")));
        Assert.Equal("standard", File.ReadAllText(Path.Combine(destDir, "movie.eng.srt")));
        Assert.Equal("sdh", File.ReadAllText(Path.Combine(destDir, "movie.eng.SDH.srt")));
    }

    [Fact]
    public void MoveCompanionFiles_TwoCompanionsShareAnExtension_VideoRenamedOnCollision_BothFollowSameStem()
    {
        // Same scenario, but combined with the video itself being renamed on collision - both
        // companions must follow the SAME renamed stem, and still not collide with each other.
        var sourceDir = Path.Combine(_tempDir, "Input", "Movie");
        Directory.CreateDirectory(sourceDir);
        var converted = Path.Combine(sourceDir, "movie.mkv");
        var standardSubtitle = Path.Combine(sourceDir, "movie.eng.srt");
        var sdhSubtitle = Path.Combine(sourceDir, "movie.eng.SDH.srt");
        File.WriteAllText(converted, "x");
        File.WriteAllText(standardSubtitle, "standard");
        File.WriteAllText(sdhSubtitle, "sdh");

        var destDir = Path.Combine(_tempDir, "Output", "Movie");
        Directory.CreateDirectory(destDir);
        var routedVideoPath = Path.Combine(destDir, "movie (2).mkv"); // simulates a video-level Rename collision

        _service.MoveCompanionFiles(converted, sourceDir, routedVideoPath, DeleteAfterConvertMode.Delete, CompanionExtensions, DeleteAfterConvertMode.Recycle, DestinationCollisionMode.Rename);

        Assert.True(File.Exists(Path.Combine(destDir, "movie (2).eng.srt")));
        Assert.True(File.Exists(Path.Combine(destDir, "movie (2).eng.SDH.srt")));
    }

    [Fact]
    public void MoveCompanionFiles_CollisionModeOverwrite_MatchesPreExistingBehavior()
    {
        var sourceDir = Path.Combine(_tempDir, "Input", "Movie");
        Directory.CreateDirectory(sourceDir);
        var converted = Path.Combine(sourceDir, "movie.mkv");
        var subtitle = Path.Combine(sourceDir, "movie.en.srt");
        File.WriteAllText(converted, "x");
        File.WriteAllText(subtitle, "NEW SUBTITLE");

        var destDir = Path.Combine(_tempDir, "Output", "Movie");
        Directory.CreateDirectory(destDir);
        var existingDestSubtitle = Path.Combine(destDir, "movie.en.srt");
        File.WriteAllText(existingDestSubtitle, "OLD SUBTITLE");

        _service.MoveCompanionFiles(converted, sourceDir, RoutedPath(destDir, "movie.mkv"), DeleteAfterConvertMode.Delete, CompanionExtensions, DeleteAfterConvertMode.Recycle, DestinationCollisionMode.Overwrite);

        Assert.False(File.Exists(subtitle));
        Assert.Equal("NEW SUBTITLE", File.ReadAllText(existingDestSubtitle));
    }

    // ---- CleanUpEmptySourceFolder: deliberately a separate call from MoveCompanionFiles, made
    // only once it's safe to actually remove the now-empty source folder (after Sonarr/Radarr's
    // own rescan has had a chance to see it still present but empty - see the interface doc
    // comment). These tests call MoveCompanionFiles first when a companion needs to be out of the
    // way, exactly like ConversionOrchestrator does, then call CleanUpEmptySourceFolder as its own
    // separate step. ----

    [Fact]
    public void CleanUpEmptySourceFolder_OtherVideoStillInFolder_FolderIsNotDeleted()
    {
        var inputRoot = Path.Combine(_tempDir, "Input");
        var seasonDir = Path.Combine(inputRoot, "Show", "Season 01");
        Directory.CreateDirectory(seasonDir);
        var converted = Path.Combine(seasonDir, "episode1.mkv");
        var otherVideo = Path.Combine(seasonDir, "episode2.mkv");
        File.WriteAllText(converted, "x");
        File.WriteAllText(otherVideo, "x");
        File.Delete(converted); // simulates the file already having been moved out by FileRouter

        _service.CleanUpEmptySourceFolder(seasonDir, inputRoot, VidTypes, DeleteAfterConvertMode.Delete, DeleteAfterConvertMode.Recycle);

        Assert.True(File.Exists(otherVideo), "the other, still-untouched video must survive");
        Assert.True(Directory.Exists(seasonDir), "a folder still holding another video is not really empty");
    }

    [Theory]
    [InlineData(DeleteAfterConvertMode.Delete)]
    [InlineData(DeleteAfterConvertMode.Recycle)]
    public void CleanUpEmptySourceFolder_AVideoStillQueuedInASubfolder_IsNeverSweptAway(DeleteAfterConvertMode unmatchedAction)
    {
        var inputRoot = Path.Combine(_tempDir, "Input");
        var movieDir = Path.Combine(inputRoot, "Movie");
        var deepDir = Path.Combine(movieDir, "Extras", "Deleted Scenes");
        Directory.CreateDirectory(deepDir);
        var queued = Path.Combine(deepDir, "unprocessed.mkv");
        File.WriteAllText(queued, "x");                                           // still waiting (or skipped, or under the size filter)
        File.WriteAllText(Path.Combine(movieDir, "Extras", "notes.txt"), "x");

        _service.CleanUpEmptySourceFolder(movieDir, inputRoot, VidTypes, DeleteAfterConvertMode.Delete, unmatchedAction);

        Assert.True(File.Exists(queued), "an unprocessed video below the folder must survive the cleanup");
        Assert.True(Directory.Exists(movieDir), "the folder above it is not empty, so it stays");
    }

    [Fact]
    public void CleanUpEmptySourceFolder_AFolderWithNoVideoAnywhereBelow_IsStillDisposedOf()
    {
        var inputRoot = Path.Combine(_tempDir, "Input");
        var movieDir = Path.Combine(inputRoot, "Movie");
        Directory.CreateDirectory(Path.Combine(movieDir, "Subs", "English"));
        File.WriteAllText(Path.Combine(movieDir, "Subs", "English", "movie.srt"), "x");

        _service.CleanUpEmptySourceFolder(movieDir, inputRoot, VidTypes, DeleteAfterConvertMode.Delete, DeleteAfterConvertMode.Delete);

        Assert.False(Directory.Exists(movieDir), "leftovers with no video in them are cleaned up as before");
    }

    [Fact]
    public void CleanUpEmptySourceFolder_AFolderThatDeniesListing_IsKept_BecauseItsContentsAreUnknown()
    {
        if (!OperatingSystem.IsWindows()) return;   // the permission is set with a Windows ACL

        var inputRoot = Path.Combine(_tempDir, "Input");
        var movieDir = Path.Combine(inputRoot, "Movie");
        var locked = Path.Combine(movieDir, "Extras");
        Directory.CreateDirectory(locked);
        var queued = Path.Combine(locked, "unprocessed.mkv");
        File.WriteAllText(queued, "x");

        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var denyListing = new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny);
        var info = new DirectoryInfo(locked);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(denyListing);
        info.SetAccessControl(acl);
        try
        {
            // The deny only counts as set up when listing really is refused (an elevated account may not be stopped by it).
            var strict = new EnumerationOptions { IgnoreInaccessible = false };
            try { _ = Directory.EnumerateFileSystemEntries(locked, "*", strict).ToList(); return; }
            catch (UnauthorizedAccessException) { }

            _service.CleanUpEmptySourceFolder(movieDir, inputRoot, VidTypes, DeleteAfterConvertMode.Delete, DeleteAfterConvertMode.Delete);

            Assert.True(Directory.Exists(locked), "a folder that cannot be listed must not be treated as empty of video");
        }
        finally
        {
            acl = info.GetAccessControl();
            acl.RemoveAccessRule(denyListing);
            info.SetAccessControl(acl);
        }

        Assert.True(File.Exists(queued));
    }

    [Fact]
    public void CleanUpEmptySourceFolder_AJunctionInsideTheFolder_IsLeftAlone_AndNothingBehindItIsTouched()
    {
        if (!OperatingSystem.IsWindows()) return;   // mklink /J is Windows-only, like the scanner's own junction test

        var inputRoot = Path.Combine(_tempDir, "Input");
        var movieDir = Path.Combine(inputRoot, "Movie");
        Directory.CreateDirectory(movieDir);
        var outside = Directory.CreateTempSubdirectory("compressarr-cleanup-outside-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(outside, "elsewhere.mkv"), "x");
            var link = Path.Combine(movieDir, "link");
            var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{outside}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            })!;
            mklink.WaitForExit();
            if (!Directory.Exists(link)) return;    // a host that forbids junctions cannot run this check

            _service.CleanUpEmptySourceFolder(movieDir, inputRoot, VidTypes, DeleteAfterConvertMode.Delete, DeleteAfterConvertMode.Delete);

            Assert.True(File.Exists(Path.Combine(outside, "elsewhere.mkv")));
            Assert.True(Directory.Exists(link), "a link cannot be vouched for, so its folder is kept");
        }
        finally
        {
            var junction = Path.Combine(movieDir, "link");
            if (Directory.Exists(junction)) Directory.Delete(junction);   // removes the link only
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void CleanUpEmptySourceFolder_MaintainMode_LeavesFolderUntouched()
    {
        var inputRoot = Path.Combine(_tempDir, "Input");
        var sourceDir = Path.Combine(inputRoot, "Movie");
        Directory.CreateDirectory(sourceDir);

        _service.CleanUpEmptySourceFolder(sourceDir, inputRoot, VidTypes, DeleteAfterConvertMode.Maintain, DeleteAfterConvertMode.Recycle);

        Assert.True(Directory.Exists(sourceDir), "Maintain must never remove the source folder, even if empty");
    }

    [Fact]
    public void CleanUpEmptySourceFolder_DeleteMode_RemovesNowEmptySourceFolder()
    {
        var inputRoot = Path.Combine(_tempDir, "Input");
        var sourceDir = Path.Combine(inputRoot, "Movie");
        Directory.CreateDirectory(sourceDir);
        var converted = Path.Combine(sourceDir, "movie.mkv");
        var subtitle = Path.Combine(sourceDir, "movie.srt");
        File.WriteAllText(converted, "x");
        File.WriteAllText(subtitle, "x");

        var destDir = Path.Combine(_tempDir, "Output", "Movie");
        Directory.CreateDirectory(destDir);

        // Same two-step sequence ConversionOrchestrator follows: move companions out first, THEN
        // (as its own later step, after arr-unmonitor/rescan in the real caller) clean up the
        // folder.
        _service.MoveCompanionFiles(converted, sourceDir, RoutedPath(destDir, "movie.mkv"), DeleteAfterConvertMode.Delete, CompanionExtensions, DeleteAfterConvertMode.Recycle);
        File.Delete(converted); // simulates the video's own source delete, which already happened by this point in the real flow
        _service.CleanUpEmptySourceFolder(sourceDir, inputRoot, VidTypes, DeleteAfterConvertMode.Delete, DeleteAfterConvertMode.Recycle);

        Assert.True(File.Exists(Path.Combine(destDir, "movie.srt")));
        Assert.False(Directory.Exists(sourceDir), "now-empty source folder must be removed");
        Assert.True(Directory.Exists(inputRoot), "the lane's input root itself must never be removed");
    }

    [Fact]
    public void CleanUpEmptySourceFolder_DeleteMode_CascadesUpwardButStopsAtInputRoot()
    {
        var inputRoot = Path.Combine(_tempDir, "Input");
        var showDir = Path.Combine(inputRoot, "Show");
        var seasonDir = Path.Combine(showDir, "Season 01");
        Directory.CreateDirectory(seasonDir);

        _service.CleanUpEmptySourceFolder(seasonDir, inputRoot, VidTypes, DeleteAfterConvertMode.Recycle, DeleteAfterConvertMode.Recycle);

        Assert.False(Directory.Exists(seasonDir));
        Assert.False(Directory.Exists(showDir), "empty parent (Show\\) must cascade-remove once Season 01 was its only content");
        Assert.True(Directory.Exists(inputRoot), "cascade must stop at the input root");
    }

    [Fact]
    public void CleanUpEmptySourceFolder_DeleteMode_CascadeStopsAtFirstNonEmptyAncestor()
    {
        var inputRoot = Path.Combine(_tempDir, "Input");
        var showDir = Path.Combine(inputRoot, "Show");
        var seasonDir = Path.Combine(showDir, "Season 01");
        Directory.CreateDirectory(seasonDir);

        // Show\ has other unrelated content (e.g. Season 02), so the cascade must stop there.
        Directory.CreateDirectory(Path.Combine(showDir, "Season 02"));

        _service.CleanUpEmptySourceFolder(seasonDir, inputRoot, VidTypes, DeleteAfterConvertMode.Delete, DeleteAfterConvertMode.Recycle);

        Assert.False(Directory.Exists(seasonDir));
        Assert.True(Directory.Exists(showDir), "must not remove a parent that still has other content");
    }

    [Fact]
    public void CleanUpEmptySourceFolder_UnmatchedActionMaintain_LeavesUnrecognizedFileAndFolderInPlace()
    {
        var inputRoot = Path.Combine(_tempDir, "Input");
        var sourceDir = Path.Combine(inputRoot, "Movie");
        Directory.CreateDirectory(sourceDir);

        // Genuinely unrelated content the user placed here on purpose - not a companion of any
        // video at all, so MoveCompanionFiles' own stem-matched loop would never have touched it
        // either; only the folder-cleanup sweep ever sees it.
        var unrelated = Path.Combine(sourceDir, "notes.txt");
        File.WriteAllText(unrelated, "personal notes, not a companion file");

        _service.CleanUpEmptySourceFolder(sourceDir, inputRoot, VidTypes, DeleteAfterConvertMode.Delete, DeleteAfterConvertMode.Maintain);

        Assert.True(File.Exists(unrelated), "unmatchedCompanionAction=Maintain must leave unrecognized content untouched");
        Assert.True(Directory.Exists(sourceDir), "the folder must not be deleted while it still holds content left in place on purpose");
    }

    [Fact]
    public void CleanUpEmptySourceFolder_InputRootItself_IsNeverRemoved()
    {
        var inputRoot = Path.Combine(_tempDir, "Input");
        Directory.CreateDirectory(inputRoot);

        _service.CleanUpEmptySourceFolder(inputRoot, inputRoot, VidTypes, DeleteAfterConvertMode.Delete, DeleteAfterConvertMode.Recycle);

        Assert.True(Directory.Exists(inputRoot), "the lane's own watch folder must never be removed, even if momentarily empty");
    }
}
