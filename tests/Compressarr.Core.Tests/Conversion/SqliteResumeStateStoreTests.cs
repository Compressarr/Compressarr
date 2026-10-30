using System.Text.Json;
using Compressarr.Core.Conversion;
using Microsoft.Data.Sqlite;

namespace Compressarr.Core.Tests.Conversion;

internal sealed class MovableClock : TimeProvider
{
    private DateTimeOffset _now;
    public MovableClock(DateTimeOffset now) => _now = now;
    public void Advance(TimeSpan by) => _now += by;
    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>The queue in SQLite: every field survives, a failed or crashed change leaves the rows alone, two writers never lose one another, the old
/// JSON file is imported once (and kept), and a damaged file or database is set aside instead of stopping the app.</summary>
public class SqliteResumeStateStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("compressarr-statedb-").FullName;
    private string ResumePath => Path.Combine(_dir, "compressarr.resume.json");
    private string DbPath => StateDatabase.PathFor(ResumePath);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ResumeEntry Entry(string name, int? order = 0, ResumeStatus status = ResumeStatus.Pending, string lane = "lane1") =>
        new() { LaneId = lane, FullName = @"C:\In\" + name, Status = status, Order = order };

    [Fact]
    public void TheAppsOwnFileName_MapsToTheStateDatabase_AnyOtherNameGetsItsOwn()
    {
        Assert.Equal(Path.Combine(_dir, "compressarr.state.db"), StateDatabase.PathFor(ResumePath));
        Assert.Equal(Path.Combine(_dir, "a.json.state.db"), StateDatabase.PathFor(Path.Combine(_dir, "a.json")));
    }

    [Fact]
    public void EveryPropertyOfAnEntry_IsStored()
    {
        var properties = typeof(ResumeEntry).GetProperties().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(properties, SqliteResumeStateStore.MappedProperties.OrderBy(n => n).ToArray());
    }

    [Fact]
    public void EveryFieldOfAnEntry_RoundTrips()
    {
        var kept = new DateTime(2026, 10, 24, 18, 30, 15, DateTimeKind.Utc).AddTicks(1234567);
        var entry = new ResumeEntry
        {
            LaneId = "lane-ü", FullName = @"C:\In\Movie (1999).mkv", Status = ResumeStatus.CompanionMoveFailed,
            EncodedFilePath = @"D:\Out\x.mkv", EncodedFileDesiredName = "Movie (1999).mkv", PendingCompanionSourceDirectory = @"C:\In",
            PendingCompanionVideoDestPath = @"E:\Movies\Movie (1999).mkv", LastRetryFailureMessage = "the share is offline", Order = 7, Skipped = true,
            PresetOverride = "My preset", Removed = true, HeldAfterFailure = true, KeptSourceSize = 12_345_678_901L, KeptSourceModifiedUtc = kept,
            CreatedByQueueEdit = true, FileBotUnmatched = true, DestinationLaneId = "lane2",
            EncodedFromSize = 9_876_543_210L, EncodedFromModifiedUtc = kept.AddHours(-5)
        };
        var store = new SqliteResumeStateStore();
        store.Save(new List<ResumeEntry> { entry, Entry("plain.mkv", order: null) }, ResumePath);

        var loaded = new SqliteResumeStateStore().Load(ResumePath);

        Assert.Equal(2, loaded.Count);
        var first = loaded[0];
        Assert.Equal(entry.LaneId, first.LaneId);
        Assert.Equal(entry.FullName, first.FullName);
        Assert.Equal(entry.Status, first.Status);
        Assert.Equal(entry.EncodedFilePath, first.EncodedFilePath);
        Assert.Equal(entry.EncodedFileDesiredName, first.EncodedFileDesiredName);
        Assert.Equal(entry.PendingCompanionSourceDirectory, first.PendingCompanionSourceDirectory);
        Assert.Equal(entry.PendingCompanionVideoDestPath, first.PendingCompanionVideoDestPath);
        Assert.Equal(entry.LastRetryFailureMessage, first.LastRetryFailureMessage);
        Assert.Equal(7, first.Order);
        Assert.True(first.Skipped);
        Assert.Equal("My preset", first.PresetOverride);
        Assert.True(first.Removed);
        Assert.True(first.HeldAfterFailure);
        Assert.Equal(12_345_678_901L, first.KeptSourceSize);
        Assert.Equal(kept, first.KeptSourceModifiedUtc);
        Assert.Equal(DateTimeKind.Utc, first.KeptSourceModifiedUtc!.Value.Kind);
        Assert.True(first.CreatedByQueueEdit);
        Assert.True(first.FileBotUnmatched);
        Assert.Equal("lane2", first.DestinationLaneId);
        Assert.Equal(9_876_543_210L, first.EncodedFromSize);
        Assert.Equal(kept.AddHours(-5), first.EncodedFromModifiedUtc);
        Assert.Equal(DateTimeKind.Utc, first.EncodedFromModifiedUtc!.Value.Kind);
        Assert.Null(loaded[1].EncodedFromSize);
        Assert.Null(loaded[1].Order);
        Assert.Null(loaded[1].KeptSourceModifiedUtc);
        Assert.False(loaded[1].Skipped);
    }

    [Fact]
    public void EntriesComeBackInTheOrderTheyWereAdded_AndAFileIsOneEntryWhateverItsCase()
    {
        var store = new SqliteResumeStateStore();
        store.Save(new List<ResumeEntry> { Entry("b.mkv", 1), Entry("a.mkv", 0) }, ResumePath);
        store.Update(ResumePath, state => { state.Add(Entry("c.mkv", 2)); state.Add(Entry("A.MKV", 9)); return true; });

        var loaded = store.Load(ResumePath);

        Assert.Equal(new[] { "b.mkv", "a.mkv", "c.mkv" }, loaded.Select(e => Path.GetFileName(e.FullName).ToLowerInvariant()));
        Assert.Equal(9, loaded.Single(e => e.FullName.EndsWith("a.mkv", StringComparison.OrdinalIgnoreCase)).Order);   // the second add of the same file updated the entry
    }

    [Fact]
    public void AnUpdateThatThrows_LeavesTheRowsAsTheyWere()
    {
        var store = new SqliteResumeStateStore();
        store.Save(new List<ResumeEntry> { Entry("a.mkv", 0), Entry("b.mkv", 1) }, ResumePath);

        Assert.Throws<InvalidOperationException>(() => store.Update<bool>(ResumePath, state =>
        {
            state.RemoveAt(0);
            state[0].Skipped = true;
            state.Add(Entry("c.mkv", 2));
            throw new InvalidOperationException("crash halfway through");
        }));

        var after = store.Load(ResumePath);
        Assert.Equal(2, after.Count);
        Assert.All(after, e => Assert.False(e.Skipped));
    }

    [Fact]
    public void AnEntryRenamedInTheCallback_KeepsItsRow()
    {
        var store = new SqliteResumeStateStore();
        store.Save(new List<ResumeEntry> { Entry("a.mkv", 0), Entry("b.mkv", 1) }, ResumePath);

        store.Update(ResumePath, state => { state[0].FullName = @"C:\In\renamed.mkv"; return true; });

        Assert.Equal(new[] { @"C:\In\renamed.mkv", @"C:\In\b.mkv" }, store.Load(ResumePath).Select(e => e.FullName));
    }

    [Fact]
    public void WritersOnTwoStoresAtOnce_NeverLoseAnUpdate()
    {
        var one = new SqliteResumeStateStore();
        var two = new SqliteResumeStateStore();
        one.Save(new List<ResumeEntry> { Entry("a.mkv", 0) }, ResumePath);

        var threads = Enumerable.Range(0, 8).Select(n => new Thread(() =>
        {
            var store = n % 2 == 0 ? one : two;
            for (var i = 0; i < 20; i++) store.Update(ResumePath, state => { state[0].Order++; return true; });
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.Equal(160, one.Load(ResumePath).Single().Order);
    }

    [Fact]
    public void TheOldJsonFile_IsImportedOnce_AndKeptAsImported()
    {
        new JsonResumeStateStore().Save(new List<ResumeEntry> { Entry("a.mkv", 0), Entry("b.mkv", 1, ResumeStatus.Error) }, ResumePath);
        var store = new SqliteResumeStateStore();

        var loaded = store.Load(ResumePath);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(ResumeStatus.Error, loaded[1].Status);
        Assert.False(File.Exists(ResumePath));
        Assert.True(File.Exists(ResumePath + ".imported"));
        Assert.Null(store.Notice);

        // An entry deleted afterwards stays deleted, and a stray JSON file dropped back is not imported a second time.
        store.Update(ResumePath, state => { state.RemoveAt(0); return true; });
        File.Copy(ResumePath + ".imported", ResumePath);
        Assert.Single(new SqliteResumeStateStore().Load(ResumePath));
    }

    [Fact]
    public void ADamagedJsonFile_IsSetAside_AndTheQueueStartsEmpty()
    {
        File.WriteAllText(ResumePath, "[ { \"LaneId\": \"lane1\", \"FullName\": ");   // cut off mid-write
        var store = new SqliteResumeStateStore();

        var notice = store.Prepare(ResumePath);

        Assert.Empty(store.Load(ResumePath));
        Assert.Contains("set aside", notice);
        Assert.False(File.Exists(ResumePath));
        Assert.True(File.Exists(ResumePath + ".damaged"));
    }

    [Fact]
    public void ADamagedDatabase_IsSetAside_AndTheQueueStartsEmpty()
    {
        File.WriteAllBytes(DbPath, Enumerable.Range(0, 4096).Select(i => (byte)(i * 31)).ToArray());
        var store = new SqliteResumeStateStore();

        var notice = store.Prepare(ResumePath);
        store.Update(ResumePath, state => { state.Add(Entry("new.mkv")); return true; });

        Assert.Contains("damaged", notice);
        Assert.True(File.Exists(DbPath + ".damaged"));
        Assert.Single(store.Load(ResumePath));
    }

    [Fact]
    public void ADatabaseFromANewerBuild_IsRefused_NotSetAside()
    {
        new SqliteResumeStateStore().Save(new List<ResumeEntry> { Entry("a.mkv") }, ResumePath);
        using (var connection = new SqliteConnection($"Data Source={DbPath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        var fresh = new SqliteResumeStateStore();
        // The path is already marked ready in this process, so look at it through another folder's copy of the file.
        var other = Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "other")).FullName, "compressarr.resume.json");
        File.Copy(DbPath, StateDatabase.PathFor(other));

        Assert.Throws<InvalidOperationException>(() => fresh.Load(other));
        Assert.True(File.Exists(StateDatabase.PathFor(other)));
        Assert.False(File.Exists(StateDatabase.PathFor(other) + ".damaged"));
    }

    [Fact]
    public void WhenTheDatabaseCannotBeCreated_TheJsonFileKeepsServing_Untouched()
    {
        var jsonStore = new JsonResumeStateStore();
        jsonStore.Save(new List<ResumeEntry> { Entry("a.mkv", 0) }, ResumePath);
        Directory.CreateDirectory(DbPath);   // a folder where the database file should go
        var store = new SqliteResumeStateStore();

        var notice = store.Prepare(ResumePath);
        var loaded = store.Load(ResumePath);
        store.Update(ResumePath, state => { state.Add(Entry("b.mkv", 1)); return true; });

        Assert.Contains("old queue file is used", notice);
        Assert.NotNull(store.ImportFailure);
        Assert.Single(loaded);
        Assert.Equal(2, jsonStore.Load(ResumePath).Count);
        Assert.False(File.Exists(ResumePath + ".imported"));
    }

    [Fact]
    public void ADatabaseThatCouldNotBeOpenedForAMoment_IsOpenedAgainAfterThePause_NotNeverUntilARestart()
    {
        // Something held the place of the database as the app started (here a folder, which is not a transient error but fails the same way). The JSON file serves
        // for the pause; once the cause is gone and the pause is over, the database is created, the queue imported, and the fallback is left.
        var jsonStore = new JsonResumeStateStore();
        jsonStore.Save(new List<ResumeEntry> { Entry("a.mkv", 0) }, ResumePath);
        Directory.CreateDirectory(DbPath);
        var clock = new MovableClock(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));
        var store = new SqliteResumeStateStore(clock);

        store.Prepare(ResumePath);
        Assert.NotNull(store.ImportFailure);

        Directory.Delete(DbPath);
        clock.Advance(SqliteResumeStateStore.RetryFailedAfter - TimeSpan.FromSeconds(1));
        Assert.Single(store.Load(ResumePath));
        Assert.NotNull(store.ImportFailure);               // still inside the pause: the fallback serves
        Assert.False(File.Exists(DbPath));

        clock.Advance(TimeSpan.FromSeconds(2));
        var loaded = store.Load(ResumePath);

        Assert.Null(store.ImportFailure);
        Assert.True(File.Exists(DbPath));
        Assert.Equal(@"C:\In\a.mkv", Assert.Single(loaded).FullName);
        Assert.True(File.Exists(ResumePath + ".imported"));
        store.Update(ResumePath, state => { state.Add(Entry("b.mkv", 1)); return true; });
        Assert.Equal(2, new SqliteResumeStateStore().Load(ResumePath).Count);   // from the database now
    }

    /// <summary>An installation whose queue already lives in the database, and whose database was unavailable for a while (here a folder in its place) - the app carried on
    /// with the JSON fallback. Returns the store, with the clock still inside the pause and the database back in place but not yet looked at again.</summary>
    private (SqliteResumeStateStore Store, MovableClock Clock) StoreThatFellBackOnJsonAfterTheImport(params ResumeEntry[] inTheDatabase)
    {
        var clock = new MovableClock(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero));
        var store = new SqliteResumeStateStore(clock);
        store.Save(inTheDatabase.ToList(), ResumePath);                  // the database exists and the import marker is set
        File.Move(DbPath, DbPath + ".away");
        Directory.CreateDirectory(DbPath);                              // now something else holds its place
        return (store, clock);
    }

    private void TheDatabaseComesBack(MovableClock clock)
    {
        Directory.Delete(DbPath);
        File.Move(DbPath + ".away", DbPath);
        clock.Advance(SqliteResumeStateStore.RetryFailedAfter + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void EditsMadeOnTheJsonFallback_AreMergedIntoTheDatabaseWhenItComesBack_NotAbandoned()
    {
        var (store, clock) = StoreThatFellBackOnJsonAfterTheImport(Entry("a.mkv", 0), Entry("b.mkv", 1));

        // The outage is noticed, and a queue file turns up beside the database anyway (written by a JSON store that did not know better, such as an older build's fallback):
        // a.mkv skipped (that creates its own entry) and a new file queued.
        store.Prepare(ResumePath);
        var json = new JsonResumeStateStore();
        json.Update(ResumePath, state => { state.Add(new ResumeEntry { LaneId = "lane1", FullName = @"C:\In\a.mkv", Status = ResumeStatus.Pending, Skipped = true, Order = 0 }); return true; });
        json.Update(ResumePath, state => { state.Add(Entry("new.mkv", 0)); return true; });
        Assert.NotNull(store.ImportFailure);
        Assert.True(File.Exists(ResumePath));

        TheDatabaseComesBack(clock);
        var loaded = store.Load(ResumePath);

        Assert.Null(store.ImportFailure);
        Assert.True(loaded.Single(e => e.FullName == @"C:\In\a.mkv").Skipped);                  // the edit survived
        Assert.False(loaded.Single(e => e.FullName == @"C:\In\b.mkv").Skipped);                 // rows the fallback never mentioned are untouched
        Assert.Contains(loaded, e => e.FullName == @"C:\In\new.mkv");                          // and so is the file queued meanwhile
        Assert.Equal(3, loaded.Count);
        Assert.Equal(loaded.Count, loaded.Select(e => e.Order).Distinct().Count());              // positions did not collide with the database's own
        Assert.False(File.Exists(ResumePath));                                                  // the fallback file is consumed ...
        Assert.True(File.Exists(ResumePath + ".recovered"));                                    // ... and kept
        Assert.Equal(3, new SqliteResumeStateStore().Load(ResumePath).Count);                   // a fresh process reads the same
    }

    [Fact]
    public void WhatTheFallbackRecordedAboutProcessing_WinsOverTheDatabasesOlderPending_ButAFreshScanDoesNotEraseAKeptSource()
    {
        var kept = new ResumeEntry { LaneId = "lane1", FullName = @"C:\In\kept.mkv", Status = ResumeStatus.Completed, Order = 0, KeptSourceSize = 123, KeptSourceModifiedUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) };
        var (store, clock) = StoreThatFellBackOnJsonAfterTheImport(Entry("moved.mkv", 1), kept);

        store.Prepare(ResumePath);
        new JsonResumeStateStore().Update(ResumePath, state =>
        {
            // A pass ran on the fallback: it encoded moved.mkv and its routing failed (the staged file is the only copy of the result) ...
            state.Add(new ResumeEntry { LaneId = "lane1", FullName = @"C:\In\moved.mkv", Status = ResumeStatus.MoveFailed, EncodedFilePath = @"D:\Out\moved.compressarr-1.mkv", EncodedFileDesiredName = "moved.mkv", EncodedFromSize = 55, Order = 0 });
            // ... and it scanned kept.mkv as a plain new file (the fallback did not know it was converted already).
            state.Add(new ResumeEntry { LaneId = "lane1", FullName = @"C:\In\kept.mkv", Status = ResumeStatus.Pending, Order = 1 });
            return true;
        });

        TheDatabaseComesBack(clock);
        var loaded = store.Load(ResumePath);

        var moved = loaded.Single(e => e.FullName == @"C:\In\moved.mkv");
        Assert.Equal(ResumeStatus.MoveFailed, moved.Status);
        Assert.Equal(@"D:\Out\moved.compressarr-1.mkv", moved.EncodedFilePath);
        Assert.Equal("moved.mkv", moved.EncodedFileDesiredName);
        Assert.Equal(55, moved.EncodedFromSize);
        var keptAfter = loaded.Single(e => e.FullName == @"C:\In\kept.mkv");
        Assert.Equal(ResumeStatus.Completed, keptAfter.Status);                                  // not turned back into work by the fallback's fresh scan
        Assert.Equal(123, keptAfter.KeptSourceSize);
    }

    [Fact]
    public void WhileTheDatabaseThatHoldsTheQueueIsUnavailable_NothingIsAcceptedOrRun_AndTheQueueIsIntactAfterwards()
    {
        var skipped = new ResumeEntry { LaneId = "lane1", FullName = @"C:\In\skipped.mkv", Status = ResumeStatus.Pending, Skipped = true, PresetOverride = "mine", DestinationLaneId = "lane2", Order = 9 };
        var (store, clock) = StoreThatFellBackOnJsonAfterTheImport(skipped);

        var reason = store.UnavailableReason(ResumePath);

        Assert.NotNull(reason);                                                   // the engine is told to pause: the fallback knows nothing of the skip
        Assert.Empty(store.Load(ResumePath));                                     // a reader sees nothing rather than something wrong
        Assert.Throws<InvalidOperationException>(() => store.Update(ResumePath, state => { state.Add(Entry("x.mkv")); return true; }));   // a change is refused, not accepted into the fallback
        Assert.Throws<InvalidOperationException>(() => store.Save(new List<ResumeEntry> { Entry("x.mkv") }, ResumePath));
        Assert.False(File.Exists(ResumePath));                                    // nothing was written to a JSON fallback

        TheDatabaseComesBack(clock);

        Assert.Null(store.UnavailableReason(ResumePath));
        var back = Assert.Single(store.Load(ResumePath));                         // exactly what was there: Skip, profile, destination and position intact
        Assert.True(back.Skipped);
        Assert.Equal("mine", back.PresetOverride);
        Assert.Equal("lane2", back.DestinationLaneId);
        Assert.Equal(9, back.Order);
    }

    [Fact]
    public void ADatabaseThatWasNeverCreated_IsNotAnOutage_TheJsonFileStaysTheQueue()
    {
        new JsonResumeStateStore().Save(new List<ResumeEntry> { Entry("a.mkv", 0) }, ResumePath);
        Directory.CreateDirectory(DbPath);                                        // the database cannot be created here
        var store = new SqliteResumeStateStore(new MovableClock(new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero)));

        Assert.Null(store.UnavailableReason(ResumePath));                         // nothing was ever in a database: the old file is still the queue
        store.Update(ResumePath, state => { state.Add(Entry("b.mkv", 1)); return true; });
        Assert.Equal(2, store.Load(ResumePath).Count);
    }

    [Fact]
    public void AFallbackFileThatCannotBeRead_IsSetAside_AndTheDatabaseIsUsedAsItIs()
    {
        var (store, clock) = StoreThatFellBackOnJsonAfterTheImport(Entry("a.mkv", 0));
        store.Prepare(ResumePath);
        File.WriteAllText(ResumePath, "{ not json");
        TheDatabaseComesBack(clock);

        var loaded = store.Load(ResumePath);

        Assert.Single(loaded);
        Assert.True(File.Exists(ResumePath + ".damaged"));
        Assert.Contains("written while the database was unavailable", store.Notice);
    }

    [Fact]
    public void ARestoredOlderBackup_ImportsItsJsonFile_AfterTheDatabaseIsRemoved()
    {
        var store = new SqliteResumeStateStore();
        store.Save(new List<ResumeEntry> { Entry("current.mkv") }, ResumePath);
        File.WriteAllText(ResumePath, JsonSerializer.Serialize(new[] { Entry("from-backup.mkv") }, new JsonSerializerOptions { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
        File.Delete(DbPath);   // what a restore of a JSON-only backup does

        var loaded = store.Load(ResumePath);

        Assert.Equal(@"C:\In\from-backup.mkv", loaded.Single().FullName);
    }

    [Fact]
    public void ASnapshot_IsAConsistentCopyThatOpensAsAQueue()
    {
        var store = new SqliteResumeStateStore();
        store.Save(new List<ResumeEntry> { Entry("a.mkv", 0), Entry("b.mkv", 1) }, ResumePath);
        var copy = Path.Combine(_dir, "snapshot.db");

        Assert.True(StateDatabase.Snapshot(DbPath, copy));
        StateDatabase.ValidateAndMigrate(copy);
        var otherResume = Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "restored")).FullName, "compressarr.resume.json");
        File.Copy(copy, StateDatabase.PathFor(otherResume));

        Assert.Equal(2, new SqliteResumeStateStore().Load(otherResume).Count);
        Assert.False(StateDatabase.Snapshot(Path.Combine(_dir, "none.db"), Path.Combine(_dir, "x.db")));
    }

    [Fact]
    public void DeleteIfComplete_KeepsOnlyTheConvertedSourcesStillOnDisk_AndNothingWhileWorkIsOutstanding()
    {
        var store = new SqliteResumeStateStore();
        var done = Entry("done.mkv", 0, ResumeStatus.Completed);
        store.Save(new List<ResumeEntry> { done, Entry("todo.mkv", 1) }, ResumePath);

        store.DeleteIfComplete(store.Load(ResumePath), ResumePath);
        Assert.Equal(2, store.Load(ResumePath).Count);

        store.Update(ResumePath, state => { state.RemoveAll(e => e.Status == ResumeStatus.Pending); return true; });
        store.DeleteIfComplete(store.Load(ResumePath), ResumePath);
        Assert.Empty(store.Load(ResumePath));   // the converted file's source is not on disk, so it is not worth keeping
    }
}
