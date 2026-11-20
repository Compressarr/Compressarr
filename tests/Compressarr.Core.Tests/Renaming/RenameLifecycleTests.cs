using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Queue;
using Compressarr.Core.Renaming;
using Compressarr.Core.Tests.Presets;

namespace Compressarr.Core.Tests.Renaming;

/// <summary>The rename as ONE unit (3.2 beta 2 review, B1-B3): the video and its subtitles move together or not at all; a file is reserved while it is renamed or encoded and looked at again after TMDB
/// has answered; and a plan is journalled before the first file moves, so a stop at any point is finished or undone on the next start instead of leaving a file under a new name with its queue
/// choices, its way back and its subtitles behind.</summary>
public class RenameLifecycleTests : AppDataTestBase
{
    private readonly string _dir;
    private static readonly string[] Video = { "mkv", "mp4" };

    public RenameLifecycleTests()
    {
        _dir = Path.Combine(AppData, "In");
        Directory.CreateDirectory(_dir);
    }

    private string Put(string name, string content = "x")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string P(string name) => Path.Combine(_dir, name);

    // ---- the set moves together (B1) ----

    [Fact]
    public void ASubtitleWhoseNewNameIsTaken_RefusesTheWholePlan_AndNothingMoves()
    {
        var video = Put("Old.mkv");
        Put("Old.en.srt", "the only copy of this subtitle");
        Put("New.en.srt", "somebody else's");

        var ex = Assert.Throws<IOException>(() => RenameApplier.Plan(video, "New.mkv", Video));

        Assert.Contains("New.en.srt", ex.Message);
        Assert.True(File.Exists(P("Old.mkv")));
        Assert.Equal("the only copy of this subtitle", File.ReadAllText(P("Old.en.srt")));
        Assert.False(File.Exists(P("New.mkv")));
    }

    [Fact]
    public void ThePlanCarriesTheVideoFirstAndEveryCompanionWithIt_ButNotAnotherVideoOrAnotherFilm()
    {
        var video = Put("Old.mkv");
        Put("Old.en.srt");
        Put("Old.nfo");
        Put("Old.mp4");                 // another video of the same name: not a companion
        Put("Older.en.srt");            // a different film that starts with the same letters: not a companion

        var moves = RenameApplier.Plan(video, "New.mkv", Video);

        Assert.Equal(new[] { "Old.mkv", "Old.en.srt", "Old.nfo" }, moves.Select(m => Path.GetFileName(m.From)).ToArray());
        Assert.Equal(new[] { "New.mkv", "New.en.srt", "New.nfo" }, moves.Select(m => Path.GetFileName(m.To)).ToArray());
    }

    [Fact]
    public void ALockedSubtitle_PutsTheWholeSetBack_AndSaysSo()
    {
        if (!OperatingSystem.IsWindows()) return;       // a rename of an open file is only refused on Windows

        var video = Put("Old.mkv");
        Put("Old.a.srt", "first");
        var lockedPath = Put("Old.b.srt", "second");
        using var held = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var moves = RenameApplier.Plan(video, "New.mkv", Video);
        var ex = Assert.Throws<RenameFailedException>(() => RenameApplier.Execute(moves));

        Assert.True(ex.RolledBack);
        Assert.True(File.Exists(P("Old.mkv")));
        Assert.Equal("first", File.ReadAllText(P("Old.a.srt")));
        Assert.False(File.Exists(P("New.mkv")));
        Assert.False(File.Exists(P("New.a.srt")));
    }

    [Fact]
    public void AnUndoNeverOverwritesAFileThatTookTheOldName()
    {
        var video = Put("Old.mkv");
        var moves = RenameApplier.Plan(video, "New.mkv", Video);
        RenameApplier.Execute(moves);
        Put("Old.mkv", "somebody put a different file here");

        var stranded = RenameApplier.Undo(moves);

        Assert.Single(stranded);
        Assert.Equal("somebody put a different file here", File.ReadAllText(P("Old.mkv")));
        Assert.True(File.Exists(P("New.mkv")));
    }

    // ---- the journal ----

    private SqliteRenameJournal NewJournal(out SqliteResumeStateStore store)
    {
        store = new SqliteResumeStateStore();
        return new SqliteRenameJournal(store);
    }

    private static RenameIntent Intent(string input, IReadOnlyList<RenameMove> moves, string source = RenameRecord.ManualSource, bool unmatched = false) =>
        RenameIntent.From(input, moves, 949, false, null, null, source, unmatched);

    [Fact]
    public void TheJournalKeepsAPlanUntilItIsEnded_AndRefusesASecondForTheSameFile()
    {
        var journal = NewJournal(out _);
        var video = Put("Old.mkv");
        Put("Old.en.srt");
        var moves = RenameApplier.Plan(video, "New.mkv", Video);

        var id = journal.Begin(Intent(_dir, moves, unmatched: true));

        Assert.NotNull(id);
        Assert.Null(journal.Begin(Intent(_dir, moves)));                     // one open rename per file
        var open = Assert.Single(journal.Pending(_dir));
        Assert.Equal(video, open.Intent.OldPath);
        Assert.Equal(P("New.mkv"), open.Intent.NewPath);
        Assert.Equal(P("New.en.srt"), Assert.Single(open.Intent.Companions).To);
        Assert.True(open.Intent.Unmatched);
        Assert.Equal(949, open.Intent.TmdbId);
        Assert.Empty(journal.Pending(Path.Combine(AppData, "Other lane")));

        journal.End(id!.Value);

        Assert.Empty(journal.Pending(_dir));
    }

    // ---- a stop at every point of a rename is finished or undone (B3) ----

    private (RenameReconciler Reconciler, SqliteRenameJournal Journal, SqliteRenameLog Log) Reconciler()
    {
        var journal = NewJournal(out var store);
        var log = new SqliteRenameLog(store);
        return (new RenameReconciler(journal, log), journal, log);
    }

    [Fact]
    public void AStopBeforeTheFirstMove_LeavesNothingToDo_AndTheRecordIsDropped()
    {
        var (reconciler, journal, _) = Reconciler();
        var video = Put("Old.mkv");
        Put("Old.en.srt");
        journal.Begin(Intent(_dir, RenameApplier.Plan(video, "New.mkv", Video)));

        var result = reconciler.Reconcile(_dir, new RenamingTestLogger());

        Assert.Empty(result.Completed);
        Assert.Empty(result.Unresolved);
        Assert.Single(result.Close);
        Assert.True(File.Exists(P("Old.mkv")));
        Assert.True(File.Exists(P("Old.en.srt")));
    }

    [Fact]
    public void AStopAfterTheVideoMoved_CarriesTheSubtitlesAcross_AndNotesTheRenameForRestore()
    {
        var (reconciler, journal, log) = Reconciler();
        var video = Put("Old.mkv");
        Put("Old.en.srt", "subtitle");
        Put("Old.nfo");
        var moves = RenameApplier.Plan(video, "New.mkv", Video);
        journal.Begin(Intent(_dir, moves));
        File.Move(moves[0].From, moves[0].To);                              // the stop: only the video moved

        var result = reconciler.Reconcile(_dir, new RenamingTestLogger());

        Assert.Equal(P("New.mkv"), result.Completed[video]);
        Assert.Empty(result.Unresolved);
        Assert.Equal("subtitle", File.ReadAllText(P("New.en.srt")));
        Assert.True(File.Exists(P("New.nfo")));
        Assert.False(File.Exists(P("Old.en.srt")));
        var record = log.Find(P("New.mkv"));
        Assert.NotNull(record);
        Assert.Equal(video, record!.OriginalPath);                           // Restore original name still knows the first name
        Assert.Equal(949, record.TmdbId);
        reconciler.Close(result.Close);
        Assert.Empty(journal.Pending(_dir));
    }

    [Fact]
    public void AStopBetweenTwoSubtitles_FinishesTheOnesLeft()
    {
        var (reconciler, journal, _) = Reconciler();
        var video = Put("Old.mkv");
        Put("Old.a.srt", "a");
        Put("Old.b.srt", "b");
        var moves = RenameApplier.Plan(video, "New.mkv", Video);
        journal.Begin(Intent(_dir, moves));
        File.Move(moves[0].From, moves[0].To);
        File.Move(moves[1].From, moves[1].To);                              // the video and the first subtitle moved

        var result = reconciler.Reconcile(_dir, new RenamingTestLogger());

        Assert.Equal(P("New.mkv"), result.Completed[video]);
        Assert.Equal("a", File.ReadAllText(P("New.a.srt")));
        Assert.Equal("b", File.ReadAllText(P("New.b.srt")));
        Assert.False(File.Exists(P("Old.b.srt")));
    }

    [Fact]
    public void AStopAfterTheRenameLogWasWritten_DoesNotWriteItAgain_SoTheFirstNameIsKept()
    {
        var (reconciler, journal, log) = Reconciler();
        var video = Put("Old.mkv");
        var moves = RenameApplier.Plan(video, "New.mkv", Video);
        journal.Begin(Intent(_dir, moves));
        File.Move(moves[0].From, moves[0].To);
        // The file had been renamed before: its very first name is on record, and the rename log was written just before the stop.
        log.Record(P("First.mkv"), video, null, false, null, null, RenameRecord.AutomaticSource);
        log.Record(video, P("New.mkv"), 949, false, null, null, RenameRecord.ManualSource);

        reconciler.Reconcile(_dir, new RenamingTestLogger());

        Assert.Equal(P("First.mkv"), log.Find(P("New.mkv"))!.OriginalPath);
    }

    [Fact]
    public void AStopWhereSomethingNowStandsInTheWay_TouchesNothing_HoldsTheFile_KeepsTheRecordOpen_AndTheQueueStillFollowsTheVideo()
    {
        var (reconciler, journal, _) = Reconciler();
        var video = Put("Old.mkv");
        Put("Old.en.srt", "the only copy");
        var moves = RenameApplier.Plan(video, "New.mkv", Video);
        journal.Begin(Intent(_dir, moves));
        File.Move(moves[0].From, moves[0].To);
        Put("New.en.srt", "put there afterwards by somebody");

        var logger = new RenamingTestLogger();
        var result = reconciler.Reconcile(_dir, logger);

        Assert.Equal(P("New.mkv"), result.Completed[video]);              // the queue entry follows the video that moved ...
        Assert.Empty(result.Close);                                       // ... but the record stays open until its subtitle can follow too
        Assert.Contains(P("New.mkv"), result.Unresolved);
        Assert.Contains(video, result.Unresolved);
        Assert.Equal("the only copy", File.ReadAllText(P("Old.en.srt")));
        Assert.Equal("put there afterwards by somebody", File.ReadAllText(P("New.en.srt")));
        Assert.Single(journal.Pending(_dir));
        Assert.Contains(logger.Logs, l => l.Severity == Compressarr.Core.Logging.LogSeverity.Error && l.Message.Contains("Rename it by hand"));
    }

    [Fact]
    public void AStopWithALockedSubtitleStillToMove_HoldsTheFile_AndTriesAgainNextTime()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (reconciler, journal, _) = Reconciler();
        var video = Put("Old.mkv");
        var subtitle = Put("Old.en.srt", "subtitle");
        var moves = RenameApplier.Plan(video, "New.mkv", Video);
        journal.Begin(Intent(_dir, moves));
        File.Move(moves[0].From, moves[0].To);

        RenameReconciliation locked;
        using (new FileStream(subtitle, FileMode.Open, FileAccess.Read, FileShare.None)) locked = reconciler.Reconcile(_dir, new RenamingTestLogger());

        Assert.Contains(P("New.mkv"), locked.Unresolved);
        Assert.Single(journal.Pending(_dir));
        Assert.Equal("subtitle", File.ReadAllText(subtitle));

        var again = reconciler.Reconcile(_dir, new RenamingTestLogger());      // the lock is gone

        Assert.Equal(P("New.mkv"), again.Completed[video]);
        Assert.Equal("subtitle", File.ReadAllText(P("New.en.srt")));
    }

    [Fact]
    public void ARecordWhoseFilesAreNeitherWhereItSaysNorWhereItWentIsDropped_WithoutMovingAnything()
    {
        var (reconciler, journal, _) = Reconciler();
        var video = Put("Old.mkv");
        journal.Begin(Intent(_dir, RenameApplier.Plan(video, "New.mkv", Video)));
        File.Delete(video);                                                  // somebody removed it

        var result = reconciler.Reconcile(_dir, new RenamingTestLogger());

        Assert.Empty(result.Completed);
        Assert.Single(result.Close);
        Assert.Empty(Directory.EnumerateFiles(_dir));
    }

    [Fact]
    public void ClosingWhatThePassAppliedLeavesTheOtherRecordsOpen()
    {
        var (reconciler, journal, _) = Reconciler();
        var a = Put("A.mkv");
        var b = Put("B.mkv");
        journal.Begin(Intent(_dir, RenameApplier.Plan(a, "A2.mkv", Video)));
        journal.Begin(Intent(_dir, RenameApplier.Plan(b, "B2.mkv", Video)));

        reconciler.CloseApplied(_dir, new Dictionary<string, string> { [a] = P("A2.mkv") });

        Assert.Equal(b, Assert.Single(journal.Pending(_dir)).Intent.OldPath);
    }

    // ---- reservations ----

    [Fact]
    public async Task AFileIsHeldByOneAtATime_AndAWaiterGetsItWhenItIsReleased()
    {
        var reservations = new FileReservations();
        var path = Put("Old.mkv");

        var first = reservations.TryReserve(path);
        Assert.NotNull(first);
        Assert.Null(reservations.TryReserve(path.ToUpperInvariant()));       // by path, whatever the case

        var waiting = reservations.ReserveAsync(path, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);

        first!.Dispose();
        first.Dispose();                                                     // releasing twice is harmless
        var (lease, waited) = await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(waited);
        lease.Dispose();
        Assert.NotNull(reservations.TryReserve(path));
    }

    [Fact]
    public async Task AWaiterStopsWaitingWhenItIsCancelled()
    {
        var reservations = new FileReservations();
        var path = Put("Old.mkv");
        using var held = reservations.TryReserve(path);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reservations.ReserveAsync(path, cts.Token));
    }

    // ---- the automatic renamer goes through the same steps ----

    private static FakeTmdbClient HeatOnly()
    {
        var tmdb = new FakeTmdbClient();
        tmdb.Films["Heat"] = new() { new TmdbCandidate(949, false, "Heat", "Heat", 1995, Array.Empty<string>(), 1, null, null) };
        return tmdb;
    }

    private TmdbRenamer Automatic(out SqliteRenameJournal journal, out FileReservations reservations, out RenameReconciler reconciler)
    {
        journal = NewJournal(out var store);
        var log = new SqliteRenameLog(store);
        reservations = new FileReservations();
        reconciler = new RenameReconciler(journal, log);
        return new TmdbRenamer(new RenamingFolderScanner(), HeatOnly(), log, journal, reservations);
    }

    private Task<Compressarr.Core.FileBot.FileBotRunResult> RunAutomatic(TmdbRenamer renamer, RenamingTestLogger logger) =>
        renamer.RunAsync(new TmdbRenameSettings { Enabled = true }, "key", _dir, Video, logger, CancellationToken.None);

    [Fact]
    public async Task TheAutomaticRenamer_JournalsEachRename_AndTheRecordStaysOpenUntilTheQueueHasCaughtUp()
    {
        var renamer = Automatic(out var journal, out _, out var reconciler);
        var video = Put("Heat.1995.720p.mkv");
        Put("Heat.1995.720p.en.srt", "subtitle");

        var result = await RunAutomatic(renamer, new RenamingTestLogger());

        Assert.Equal(P("Heat (1995).mkv"), result.Renames[video]);
        Assert.Equal("subtitle", File.ReadAllText(P("Heat (1995).en.srt")));
        var open = Assert.Single(journal.Pending(_dir));                       // a stop here would be finished on the next start
        Assert.Equal(video, open.Intent.OldPath);

        reconciler.CloseApplied(_dir, result.Renames);                         // what the orchestrator does once it has saved the queue

        Assert.Empty(journal.Pending(_dir));
    }

    [Fact]
    public async Task ThePreviewSaysSo_WhenASubtitleCannotFollowItsVideo()
    {
        var renamer = Automatic(out _, out _, out _);
        Put("Heat.1995.720p.mkv");
        Put("Heat.1995.720p.en.srt");
        Put("Heat (1995).en.srt");

        var plan = await renamer.PreviewAsync(new TmdbRenameSettings { Enabled = true }, "key", _dir, Video, 40, CancellationToken.None);

        var item = Assert.Single(plan);
        Assert.Equal(RenameKind.TargetExists, item.Kind);                       // not "Rename": apply would refuse, and a preview must not promise what apply will not do
        Assert.Contains("Heat (1995).en.srt", item.Reason);
    }

    [Fact]
    public async Task TheAutomaticRenamer_LeavesAFileSomethingElseHolds()
    {
        var renamer = Automatic(out _, out var reservations, out _);
        var video = Put("Heat.1995.720p.mkv");
        var logger = new RenamingTestLogger();

        Compressarr.Core.FileBot.FileBotRunResult held;
        using (reservations.TryReserve(video)) held = await RunAutomatic(renamer, logger);

        Assert.Empty(held.Renames);
        Assert.True(File.Exists(video));
        Assert.Contains(logger.Logs, l => l.Message.Contains("worked on elsewhere"));
    }

    [Fact]
    public async Task TheAutomaticRenamer_PutsTheSetBackWhenASubtitleIsLocked_AndTriesAgainNextPass()
    {
        if (!OperatingSystem.IsWindows()) return;

        var renamer = Automatic(out var journal, out _, out _);
        var video = Put("Heat.1995.720p.mkv");
        var subtitle = Put("Heat.1995.720p.en.srt", "subtitle");
        var logger = new RenamingTestLogger();

        Compressarr.Core.FileBot.FileBotRunResult locked;
        using (new FileStream(subtitle, FileMode.Open, FileAccess.Read, FileShare.None)) locked = await RunAutomatic(renamer, logger);

        Assert.Empty(locked.Renames);
        Assert.True(File.Exists(video));
        Assert.False(File.Exists(P("Heat (1995).mkv")));
        Assert.Empty(journal.Pending(_dir));                                   // nothing was left half done, so nothing is left to finish
        Assert.Contains(logger.Logs, l => l.Message.Contains("Nothing was changed"));

        var again = await RunAutomatic(renamer, new RenamingTestLogger());     // the lock is gone

        Assert.Equal(P("Heat (1995).mkv"), again.Renames[video]);
        Assert.Equal("subtitle", File.ReadAllText(P("Heat (1995).en.srt")));
    }

    [Fact]
    public async Task TheAutomaticRenamer_SkipsAFileThatWasMatchedByHandWhileItsPlanWasWaiting()
    {
        var journal = NewJournal(out var store);
        var log = new SqliteRenameLog(store);
        var video = Put("Heat.1995.720p.mkv");
        var tmdb = HeatOnly();
        // While TMDB is being asked (the plan is made first, the files renamed afterwards) a person marks this file as theirs.
        tmdb.OnSearch = () => log.Record(video, video, null, false, null, null, RenameRecord.RestoredSource);
        var renamer = new TmdbRenamer(new RenamingFolderScanner(), tmdb, log, journal, new FileReservations());

        var result = await RunAutomatic(renamer, new RenamingTestLogger());

        Assert.Empty(result.Renames);
        Assert.True(File.Exists(video));
    }
}
