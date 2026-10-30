using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Queue;

namespace Compressarr.Core.Tests.Queue;

file sealed class PassThroughPathExpander : IPathExpander
{
    public string Expand(string value) => value;
    public bool PathExists(string value) => Directory.Exists(value) || File.Exists(value);
}

/// <summary>Returns whatever the test says the folder scan found, in that order - so a test can make
/// the live scan order disagree with arrival order, which is the whole thing the queue rules exist to
/// handle. ThrowFor makes one folder's scan fail.</summary>
file sealed class ScriptedScanner : IVideoFileScanner
{
    public Dictionary<string, List<string>> FilesByFolder { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ThrowFor { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<FileInfo> FindVideoFiles(string inputPath, IReadOnlyList<string> vidTypes, long minSizeBytes, int limit)
    {
        if (ThrowFor.Contains(inputPath)) throw new IOException("scan failed");
        return FilesByFolder.TryGetValue(inputPath, out var files)
            ? files.Select(f => new FileInfo(f)).ToList()
            : new List<FileInfo>();
    }
}

public sealed class QueueRulesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "compressarr-queuerules-" + Guid.NewGuid().ToString("N"));

    public QueueRulesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Touch(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private static ResumeEntry Pending(string lane, string path, int? order = null) =>
        new() { LaneId = lane, FullName = path, Status = ResumeStatus.Pending, Order = order };

    private static CompressarrConfig ConfigWith(params (string Id, string Input)[] lanes)
    {
        var config = new CompressarrConfig();
        config.Lanes.Clear();
        foreach (var (id, input) in lanes)
        {
            config.Lanes.Add(new LaneConfig { Id = id, DisplayName = id, Enabled = true, Input = input });
        }
        return config;
    }

    private static Dictionary<string, int> LaneIndex(params string[] laneIds) =>
        laneIds.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);

    // ---- SelectNext -------------------------------------------------------------------------

    [Fact]
    public void SelectNext_LowestExplicitOrderWinsRegardlessOfLane()
    {
        var a = Touch(Folder("a"), "a.mkv");
        var b = Touch(Folder("b"), "b.mkv");
        var state = new List<ResumeEntry> { Pending("laneA", a, order: 5), Pending("laneB", b, order: 2) };

        var next = QueueRules.SelectNext(state, LaneIndex("laneA", "laneB"), (_, _) => 0);

        Assert.Equal(b, next!.FullName);
    }

    [Fact]
    public void SelectNext_EqualOrderFallsBackToLanePositionThenNaturalIndex()
    {
        var a1 = Touch(Folder("a"), "a1.mkv");
        var a2 = Touch(Folder("a"), "a2.mkv");
        var b1 = Touch(Folder("b"), "b1.mkv");
        // No explicit order for anyone: lane A (first in config) goes before lane B, and within a
        // lane the earlier scan position goes first.
        var state = new List<ResumeEntry> { Pending("laneB", b1), Pending("laneA", a2), Pending("laneA", a1) };
        var natural = new Dictionary<string, int> { [a1] = 0, [a2] = 1, [b1] = 0 };

        var lanes = LaneIndex("laneA", "laneB");
        Assert.Equal(a1, QueueRules.SelectNext(state, lanes, (_, f) => natural[f])!.FullName);

        state.RemoveAll(e => e.FullName == a1);
        Assert.Equal(a2, QueueRules.SelectNext(state, lanes, (_, f) => natural[f])!.FullName);
    }

    [Fact]
    public void SelectNext_NeverPicksSkippedRemovedNonPendingUnpreparedOrMissingFiles()
    {
        var folder = Folder("a");
        var skipped = Touch(folder, "skipped.mkv");
        var removed = Touch(folder, "removed.mkv");
        var errored = Touch(folder, "errored.mkv");
        var otherLane = Touch(Folder("z"), "other.mkv");
        var missing = Path.Combine(folder, "gone.mkv");
        var good = Touch(folder, "good.mkv");

        var state = new List<ResumeEntry>
        {
            new() { LaneId = "laneA", FullName = skipped, Status = ResumeStatus.Pending, Skipped = true, Order = 0 },
            new() { LaneId = "laneA", FullName = removed, Status = ResumeStatus.Pending, Removed = true, Skipped = true, Order = 1 },
            new() { LaneId = "laneA", FullName = errored, Status = ResumeStatus.Error, Order = 2 },
            Pending("notPrepared", otherLane, order: 3),
            Pending("laneA", missing, order: 4),
            Pending("laneA", good, order: 9)
        };

        var next = QueueRules.SelectNext(state, LaneIndex("laneA"), (_, _) => 0);

        Assert.Equal(good, next!.FullName);
    }

    [Fact]
    public void SelectNext_ReturnsNullWhenNothingIsEligible()
    {
        Assert.Null(QueueRules.SelectNext(new List<ResumeEntry>(), LaneIndex("laneA"), (_, _) => 0));
    }

    // ---- TrackScannedFiles ------------------------------------------------------------------

    [Fact]
    public void TrackScannedFiles_NewFileIsAppendedAfterEveryExistingOrder_EvenWithABacklog()
    {
        var folder = Folder("a");
        var waiting = Touch(folder, "z-waiting.mkv");
        var arrived = Touch(folder, "a-arrived.mkv"); // sorts before the waiting file on a scan
        var state = new List<ResumeEntry> { Pending("laneA", waiting, order: 7) };

        var added = QueueRules.TrackScannedFiles(state, "laneA", new[] { new FileInfo(arrived), new FileInfo(waiting) }, new HashSet<string>(), laneHadPending: true);

        Assert.True(added);
        var entry = state.Single(e => e.FullName == arrived);
        Assert.Equal(8, entry.Order);
        Assert.Equal(7, state.Single(e => e.FullName == waiting).Order);
    }

    [Fact]
    public void TrackScannedFiles_NewFilesGetAscendingOrdersInScanOrder()
    {
        var folder = Folder("a");
        var one = Touch(folder, "1.mkv");
        var two = Touch(folder, "2.mkv");
        var state = new List<ResumeEntry>();

        QueueRules.TrackScannedFiles(state, "laneA", new[] { new FileInfo(one), new FileInfo(two) }, new HashSet<string>(), laneHadPending: false);

        Assert.Equal(0, state.Single(e => e.FullName == one).Order);
        Assert.Equal(1, state.Single(e => e.FullName == two).Order);
    }

    [Fact]
    public void TrackScannedFiles_NewFileIsOnlyMarkedQueueEditCreatedWhenTheLaneAlreadyHadPendingWork()
    {
        var folder = Folder("a");
        var file = Touch(folder, "f.mkv");

        var fresh = new List<ResumeEntry>();
        QueueRules.TrackScannedFiles(fresh, "laneA", new[] { new FileInfo(file) }, new HashSet<string>(), laneHadPending: false);
        Assert.False(fresh.Single().CreatedByQueueEdit);

        var resumed = new List<ResumeEntry>();
        QueueRules.TrackScannedFiles(resumed, "laneA", new[] { new FileInfo(file) }, new HashSet<string>(), laneHadPending: true);
        Assert.True(resumed.Single().CreatedByQueueEdit);
    }

    [Fact]
    public void TrackScannedFiles_RecordsFileBotUnmatchedFiles()
    {
        var folder = Folder("a");
        var file = Touch(folder, "f.mkv");
        var state = new List<ResumeEntry>();

        QueueRules.TrackScannedFiles(state, "laneA", new[] { new FileInfo(file) }, new HashSet<string> { file }, laneHadPending: false);

        Assert.True(state.Single().FileBotUnmatched);
    }

    [Fact]
    public void TrackScannedFiles_ReAddedCompletedFileIsRequeuedAtTheEnd_WhenNothingElseIsPending()
    {
        var folder = Folder("a");
        var readded = Touch(folder, "readded.mkv");
        var other = Touch(folder, "other.mkv");
        var state = new List<ResumeEntry>
        {
            new() { LaneId = "laneA", FullName = readded, Status = ResumeStatus.Completed, Order = 0 },
            new() { LaneId = "laneA", FullName = other, Status = ResumeStatus.Completed, Order = 5 }
        };

        var added = QueueRules.TrackScannedFiles(state, "laneA", new[] { new FileInfo(readded) }, new HashSet<string>(), laneHadPending: false);

        Assert.False(added); // reused the entry, no second one
        Assert.Equal(2, state.Count);
        var entry = state.Single(e => e.FullName == readded);
        Assert.Equal(ResumeStatus.Pending, entry.Status);
        Assert.Equal(6, entry.Order); // restamped to the end, not its old position 0
    }

    [Fact]
    public void TrackScannedFiles_FileHeldAfterAnEncodeFailureIsNotRetriedByARescan()
    {
        var folder = Folder("a");
        var failed = Touch(folder, "failed.mkv");
        var aborted = Touch(folder, "aborted.mkv");
        var state = new List<ResumeEntry>
        {
            new() { LaneId = "laneA", FullName = failed, Status = ResumeStatus.Error, HeldAfterFailure = true },
            new() { LaneId = "laneA", FullName = aborted, Status = ResumeStatus.Error } // an abort or missing preset is still retried
        };

        QueueRules.TrackScannedFiles(state, "laneA", new[] { new FileInfo(failed), new FileInfo(aborted) }, new HashSet<string>(), laneHadPending: false);

        Assert.Equal(ResumeStatus.Error, state.Single(e => e.FullName == failed).Status);
        Assert.Equal(ResumeStatus.Pending, state.Single(e => e.FullName == aborted).Status);
    }

    [Fact]
    public void TrackScannedFiles_ReAddedFileIsLeftAloneWhileTheLaneHasABacklog()
    {
        var folder = Folder("a");
        var readded = Touch(folder, "readded.mkv");
        var state = new List<ResumeEntry>
        {
            new() { LaneId = "laneA", FullName = readded, Status = ResumeStatus.Completed, Order = 0 }
        };

        QueueRules.TrackScannedFiles(state, "laneA", new[] { new FileInfo(readded) }, new HashSet<string>(), laneHadPending: true);

        Assert.Equal(ResumeStatus.Completed, state.Single().Status);
        Assert.Equal(0, state.Single().Order);
    }

    [Theory]
    [InlineData(ResumeStatus.MoveFailed)]
    [InlineData(ResumeStatus.CompanionMoveFailed)]
    [InlineData(ResumeStatus.CleanupPending)]
    public void TrackScannedFiles_NeverFlipsAnEntryBeingRetriedBackToPending(ResumeStatus status)
    {
        var folder = Folder("a");
        var file = Touch(folder, "f.mkv");
        var state = new List<ResumeEntry> { new() { LaneId = "laneA", FullName = file, Status = status, Order = 3 } };

        QueueRules.TrackScannedFiles(state, "laneA", new[] { new FileInfo(file) }, new HashSet<string>(), laneHadPending: false);

        Assert.Equal(status, state.Single().Status);
        Assert.Equal(3, state.Single().Order);
    }

    [Fact]
    public void TrackScannedFiles_ExistingPendingEntryKeepsItsPosition()
    {
        var folder = Folder("a");
        var file = Touch(folder, "f.mkv");
        var state = new List<ResumeEntry> { Pending("laneA", file, order: 2), Pending("laneA", Touch(folder, "g.mkv"), order: 9) };

        var added = QueueRules.TrackScannedFiles(state, "laneA", new[] { new FileInfo(file) }, new HashSet<string>(), laneHadPending: false);

        Assert.False(added);
        Assert.Equal(2, state.Single(e => e.FullName == file).Order);
    }

    // ---- WaitingEntriesInOrder --------------------------------------------------------------

    [Fact]
    public void WaitingEntriesInOrder_IsOrderedAndExcludesSkippedRemovedOtherLanesAndNonPending()
    {
        var state = new List<ResumeEntry>
        {
            Pending("laneA", "c", order: 3),
            Pending("laneA", "a", order: 1),
            new() { LaneId = "laneA", FullName = "skip", Status = ResumeStatus.Pending, Skipped = true, Order = 0 },
            new() { LaneId = "laneA", FullName = "gone", Status = ResumeStatus.Pending, Removed = true, Order = 0 },
            new() { LaneId = "laneA", FullName = "done", Status = ResumeStatus.Completed, Order = 0 },
            Pending("laneB", "other", order: 0),
            Pending("laneA", "unordered")
        };

        var names = QueueRules.WaitingEntriesInOrder(state, "laneA").Select(e => e.FullName).ToList();

        Assert.Equal(new[] { "a", "c", "unordered" }, names);
    }

    // ---- MergeUserEdits ---------------------------------------------------------------------

    [Fact]
    public void MergeUserEdits_CopiesOnlyTheFourUserOwnedFields()
    {
        var state = new List<ResumeEntry>
        {
            new() { LaneId = "laneA", FullName = "f", Status = ResumeStatus.Completed, EncodedFilePath = "enc.mkv", Order = 1 }
        };
        var onDisk = new[]
        {
            new ResumeEntry { LaneId = "laneA", FullName = "f", Status = ResumeStatus.Pending, EncodedFilePath = null, Order = 9, Skipped = true, Removed = true, PresetOverride = "Fast" }
        };

        QueueRules.MergeUserEdits(state, onDisk);

        var merged = state.Single();
        Assert.Equal(9, merged.Order);
        Assert.True(merged.Skipped);
        Assert.True(merged.Removed);
        Assert.Equal("Fast", merged.PresetOverride);
        // The engine's own state machine is never overwritten from disk.
        Assert.Equal(ResumeStatus.Completed, merged.Status);
        Assert.Equal("enc.mkv", merged.EncodedFilePath);
    }

    [Fact]
    public void MergeUserEdits_CarriesALaneAssignmentAcross_AndItsClearing()
    {
        var state = new List<ResumeEntry> { Pending("laneA", "f", order: 1) };

        QueueRules.MergeUserEdits(state, new[] { new ResumeEntry { LaneId = "laneA", FullName = "f", Status = ResumeStatus.Pending, DestinationLaneId = "laneB" } });
        Assert.Equal("laneB", state.Single().DestinationLaneId);

        QueueRules.MergeUserEdits(state, new[] { new ResumeEntry { LaneId = "laneA", FullName = "f", Status = ResumeStatus.Pending, DestinationLaneId = null } });
        Assert.Null(state.Single().DestinationLaneId);
    }

    [Fact]
    public void MergeUserEdits_AdoptsAnEntryTheEngineHasNotSeenYet()
    {
        var state = new List<ResumeEntry>();
        var fromDisk = Pending("laneA", "new", order: 4);

        QueueRules.MergeUserEdits(state, new[] { fromDisk });

        Assert.Same(fromDisk, state.Single());
    }

    [Fact]
    public void MergeUserEdits_MatchesOnLaneAndPath()
    {
        var state = new List<ResumeEntry> { Pending("laneA", "same", order: 1), Pending("laneB", "same", order: 2) };

        QueueRules.MergeUserEdits(state, new[] { Pending("laneB", "same", order: 8) });

        Assert.Equal(1, state.Single(e => e.LaneId == "laneA").Order);
        Assert.Equal(8, state.Single(e => e.LaneId == "laneB").Order);
        Assert.Equal(2, state.Count);
    }

    // ---- NaturalIndexMap / ApplyExplicitOrder ------------------------------------------------

    [Fact]
    public void NaturalIndexMap_IsScanPositionAndCaseInsensitive()
    {
        var map = QueueRules.NaturalIndexMap(new[] { new FileInfo(Path.Combine(_root, "B.mkv")), new FileInfo(Path.Combine(_root, "a.mkv")) });

        Assert.Equal(0, map[Path.Combine(_root, "b.MKV")]);
        Assert.Equal(1, map[Path.Combine(_root, "A.mkv")]);
    }

    [Fact]
    public void ApplyExplicitOrder_NumbersTheWholeListAcrossLanes_AndSkipsUnusableItems()
    {
        var a = Touch(Folder("a"), "a.mkv");
        var b = Touch(Folder("b"), "b.mkv");
        var errored = Touch(Folder("a"), "errored.mkv");
        var config = ConfigWith(("laneA", Folder("a")), ("laneB", Folder("b")));
        var state = new List<ResumeEntry> { new() { LaneId = "laneA", FullName = errored, Status = ResumeStatus.Error } };

        QueueRules.ApplyExplicitOrder(config, state, new[]
        {
            ("laneB", b),
            ("noSuchLane", a),
            ("laneA", errored),
            ("laneA", Path.Combine(_root, "missing.mkv")),
            ("laneA", a)
        }, new PassThroughPathExpander());

        Assert.Equal(0, state.Single(e => e.FullName == b).Order);
        Assert.Equal(4, state.Single(e => e.FullName == a).Order); // positions are consumed even by skipped items
        Assert.Null(state.Single(e => e.FullName == errored).Order); // an Error entry is never touched
        Assert.Equal(3, state.Count);
    }

    // ---- LockInVisibleOrder / BuildUpNext ---------------------------------------------------

    [Fact]
    public void LockInVisibleOrder_TracksUntrackedFilesAtTheEnd_SoALaterArrivalCannotJumpAhead()
    {
        var folder = Folder("a");
        var waiting = Touch(folder, "z-waiting.mkv");
        var arrived = Touch(folder, "a-arrived.mkv");
        var scanner = new ScriptedScanner();
        scanner.FilesByFolder[folder] = new List<string> { arrived, waiting }; // arrived enumerates first
        var config = ConfigWith(("laneA", folder));
        var state = new List<ResumeEntry> { Pending("laneA", waiting, order: 0) };

        QueueRules.LockInVisibleOrder(config, state, new PassThroughPathExpander(), scanner);

        var upNext = QueueRules.BuildUpNext(config, state, new PassThroughPathExpander(), scanner, new QueueRunContext(null, new Dictionary<string, bool>()), out var sawUntracked);
        Assert.False(sawUntracked);
        Assert.Equal(new[] { "z-waiting.mkv", "a-arrived.mkv" }, upNext.Select(i => i.FileName).ToArray());
    }

    [Fact]
    public void BuildUpNext_FlagsUntrackedFilesAndShowsTheInProgressFileNowhere()
    {
        var folder = Folder("a");
        var running = Touch(folder, "running.mkv");
        var untracked = Touch(folder, "untracked.mkv");
        var scanner = new ScriptedScanner();
        scanner.FilesByFolder[folder] = new List<string> { running, untracked };
        var config = ConfigWith(("laneA", folder));
        var state = new List<ResumeEntry> { Pending("laneA", running, order: 0) };

        var upNext = QueueRules.BuildUpNext(config, state, new PassThroughPathExpander(), scanner, new QueueRunContext(running, new Dictionary<string, bool>()), out var sawUntracked);

        Assert.True(sawUntracked);
        Assert.Equal(new[] { "untracked.mkv" }, upNext.Select(i => i.FileName).ToArray());
    }

    // ---- RemapRenamedFiles ------------------------------------------------------------------

    private static Dictionary<string, string> Renames(string from, string to) => new(StringComparer.OrdinalIgnoreCase) { [from] = to };

    [Fact]
    public void RemapRenamedFiles_MovesTheEntryToTheNewPath_KeepingEveryUserSetting()
    {
        var folder = Folder("a");
        var oldPath = Path.Combine(folder, "old.mkv"); // FileBot moved it, so it no longer exists
        var newPath = Touch(folder, "New Name.mkv");
        var other = Touch(folder, "other.mkv");
        var entry = new ResumeEntry { LaneId = "laneA", FullName = oldPath, Status = ResumeStatus.Pending, Order = 0, Skipped = true, PresetOverride = "Fast", CreatedByQueueEdit = true };
        var state = new List<ResumeEntry> { entry, Pending("laneA", other, order: 1) };

        var changed = QueueRules.RemapRenamedFiles(state, "laneA", Renames(oldPath, newPath));

        Assert.True(changed);
        Assert.Same(entry, state[0]); // same entry, just re-pointed
        Assert.Equal(newPath, entry.FullName);
        Assert.Equal(0, entry.Order);
        Assert.True(entry.Skipped);
        Assert.Equal("Fast", entry.PresetOverride);
        Assert.Equal(2, state.Count);
    }

    [Fact]
    public void RemapRenamedFiles_WhenTheNewPathWasAlreadyTrackedAsANewArrival_ItInheritsAndTheOldEntryGoes()
    {
        // The Monitor page's poll can track the renamed file while FileBot is still running.
        var folder = Folder("a");
        var oldPath = Path.Combine(folder, "old.mkv");
        var newPath = Touch(folder, "New Name.mkv");
        var state = new List<ResumeEntry>
        {
            new() { LaneId = "laneA", FullName = oldPath, Status = ResumeStatus.Pending, Order = 1, PresetOverride = "Fast", Removed = true, Skipped = true },
            new() { LaneId = "laneA", FullName = newPath, Status = ResumeStatus.Pending, Order = 9, CreatedByQueueEdit = true }
        };

        var changed = QueueRules.RemapRenamedFiles(state, "laneA", Renames(oldPath, newPath));

        Assert.True(changed);
        var survivor = Assert.Single(state);
        Assert.Equal(newPath, survivor.FullName);
        Assert.Equal(1, survivor.Order);
        Assert.Equal("Fast", survivor.PresetOverride);
        Assert.True(survivor.Skipped);
        Assert.True(survivor.Removed);
    }

    [Fact]
    public void RemapRenamedFiles_WhenTheNewPathIsTrackedUnderAnotherStatus_NothingIsRemapped()
    {
        var folder = Folder("a");
        var oldPath = Path.Combine(folder, "old.mkv");
        var newPath = Touch(folder, "New Name.mkv");
        var state = new List<ResumeEntry>
        {
            Pending("laneA", oldPath, order: 1),
            new() { LaneId = "laneA", FullName = newPath, Status = ResumeStatus.Completed, Order = 4 }
        };

        var changed = QueueRules.RemapRenamedFiles(state, "laneA", Renames(oldPath, newPath));

        Assert.False(changed);
        Assert.Equal(2, state.Count);
        Assert.Equal(oldPath, state[0].FullName);
        Assert.Equal(ResumeStatus.Completed, state[1].Status);
    }

    [Fact]
    public void RemapRenamedFiles_IgnoresARenameThatDidNotHappen()
    {
        var folder = Folder("a");
        var stillThere = Touch(folder, "old.mkv");
        var neverCreated = Path.Combine(folder, "new.mkv");
        var state = new List<ResumeEntry> { Pending("laneA", stillThere, order: 3) };

        Assert.False(QueueRules.RemapRenamedFiles(state, "laneA", Renames(stillThere, neverCreated)));
        Assert.Equal(stillThere, state.Single().FullName);

        // ...nor one where the old file is gone but the new one never showed up.
        var gone = Path.Combine(folder, "gone.mkv");
        state = new List<ResumeEntry> { Pending("laneA", gone, order: 3) };
        Assert.False(QueueRules.RemapRenamedFiles(state, "laneA", Renames(gone, neverCreated)));
        Assert.Equal(gone, state.Single().FullName);
    }

    [Fact]
    public void RemapRenamedFiles_OnlyTouchesWaitingEntriesInTheSameLane()
    {
        var folder = Folder("a");
        var oldPath = Path.Combine(folder, "old.mkv");
        var newPath = Touch(folder, "New Name.mkv");
        var otherLane = Pending("laneB", oldPath, order: 1);
        var errored = new ResumeEntry { LaneId = "laneA", FullName = oldPath, Status = ResumeStatus.Error, Order = 2 };
        var state = new List<ResumeEntry> { otherLane, errored };

        var changed = QueueRules.RemapRenamedFiles(state, "laneA", Renames(oldPath, newPath));

        Assert.False(changed);
        Assert.Equal(oldPath, otherLane.FullName);
        Assert.Equal(oldPath, errored.FullName);
    }

    [Fact]
    public void RemapRenamedFiles_MatchesPathsIgnoringCase()
    {
        var folder = Folder("a");
        var oldPath = Path.Combine(folder, "Old.mkv");
        var newPath = Touch(folder, "New Name.mkv");
        var state = new List<ResumeEntry> { Pending("laneA", oldPath.ToUpperInvariant(), order: 2) };

        Assert.True(QueueRules.RemapRenamedFiles(state, "laneA", Renames(oldPath, newPath)));
        Assert.Equal(newPath, state.Single().FullName);
    }

    // ---- BackfillMissingOrder ---------------------------------------------------------------

    [Fact]
    public void BackfillMissingOrder_IsANoOpOnceEveryPendingEntryHasAnOrder()
    {
        var state = new List<ResumeEntry> { Pending("laneA", "x", order: 0), new() { LaneId = "laneA", FullName = "y", Status = ResumeStatus.Completed } };

        var changed = QueueRules.BackfillMissingOrder(ConfigWith(("laneA", _root)), state, new PassThroughPathExpander(), new ScriptedScanner(), _ => { });

        Assert.False(changed);
        Assert.Null(state[1].Order);
    }

    [Fact]
    public void BackfillMissingOrder_FreezesLaneThenScanOrder_AfterTheHighestExistingOrder()
    {
        var folderA = Folder("a");
        var folderB = Folder("b");
        var a1 = Touch(folderA, "a1.mkv");
        var a2 = Touch(folderA, "a2.mkv");
        var b1 = Touch(folderB, "b1.mkv");
        var scanner = new ScriptedScanner();
        scanner.FilesByFolder[folderA] = new List<string> { a1, a2 };
        scanner.FilesByFolder[folderB] = new List<string> { b1 };
        var config = ConfigWith(("laneA", folderA), ("laneB", folderB));
        var stamped = Pending("laneB", Touch(folderB, "stamped.mkv"), order: 10);
        var state = new List<ResumeEntry> { stamped, Pending("laneB", b1), Pending("laneA", a2), Pending("laneA", a1) };

        var changed = QueueRules.BackfillMissingOrder(config, state, new PassThroughPathExpander(), scanner, _ => { });

        Assert.True(changed);
        Assert.Equal(10, stamped.Order);
        Assert.Equal(11, state.Single(e => e.FullName == a1).Order);
        Assert.Equal(12, state.Single(e => e.FullName == a2).Order);
        Assert.Equal(13, state.Single(e => e.FullName == b1).Order);
    }

    [Fact]
    public void BackfillMissingOrder_ReportsAFailedLaneScan_AndStillGivesEveryEntryAnOrder()
    {
        var folderA = Folder("a");
        var a1 = Touch(folderA, "a1.mkv");
        var scanner = new ScriptedScanner();
        scanner.ThrowFor.Add(folderA);
        var config = ConfigWith(("laneA", folderA));
        var state = new List<ResumeEntry> { Pending("laneA", a1), Pending("deletedLane", "orphan") };
        var messages = new List<string>();

        var changed = QueueRules.BackfillMissingOrder(config, state, new PassThroughPathExpander(), scanner, messages.Add);

        Assert.True(changed);
        Assert.Single(messages);
        Assert.All(state, e => Assert.NotNull(e.Order));
        Assert.Equal(2, state.Select(e => e.Order).Distinct().Count());
    }
}
