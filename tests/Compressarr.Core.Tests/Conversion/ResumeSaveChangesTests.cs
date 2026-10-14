using Compressarr.Core.Conversion;

namespace Compressarr.Core.Tests.Conversion;

/// <summary>A pass that reworks the whole in-memory list (adds tracked files, remaps renames, prunes) saves only what it changed, merged into the
/// file as it is now - so a queue edit committed after the pass's refresh survives, which a save of the whole older list would have wiped.</summary>
public class ResumeSaveChangesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("compressarr-savechanges-").FullName;
    private readonly JsonResumeStateStore _store = new();
    private string Path_ => Path.Combine(_dir, "resume.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ResumeEntry Entry(string name, int order, ResumeStatus status = ResumeStatus.Pending) =>
        new() { LaneId = "lane1", FullName = @"C:\In\" + name, Status = status, Order = order };

    private (List<ResumeEntry> Memory, List<ResumeEntry> Baseline) Start(params ResumeEntry[] onDisk)
    {
        _store.Save(onDisk.ToList(), Path_);
        var memory = _store.Load(Path_);
        return (memory, ResumeResults.Snapshot(memory));
    }

    private void EditOnDisk(string name, Action<ResumeEntry> edit) =>
        _store.Update(Path_, state => { edit(state.Single(e => e.FullName.EndsWith(name))); return true; });

    [Fact]
    public void AnEditMadeAfterTheRefresh_SurvivesWhenThePassAddsAFile()
    {
        var (memory, baseline) = Start(Entry("a.mkv", 0));
        EditOnDisk("a.mkv", e => e.Skipped = true);                    // committed after the pass refreshed
        memory.Add(Entry("b.mkv", 1));                                 // the pass tracks a new file

        ResumeResults.SaveChanges(_store, baseline, memory, Path_);

        var after = _store.Load(Path_);
        Assert.True(after.Single(e => e.FullName.EndsWith("a.mkv")).Skipped);
        Assert.Contains(after, e => e.FullName.EndsWith("b.mkv"));
    }

    [Fact]
    public void OnlyTheFieldsThePassChanged_AreCopied_SoAFreshEditToAnotherFieldOfTheSameEntryStays()
    {
        var (memory, baseline) = Start(Entry("a.mkv", 0));
        EditOnDisk("a.mkv", e => e.PresetOverride = "My preset");     // user edit after the refresh
        memory.Single().Order = 5;                                     // the pass changed a different field (an order backfill)

        ResumeResults.SaveChanges(_store, baseline, memory, Path_);

        var saved = _store.Load(Path_).Single();
        Assert.Equal(5, saved.Order);
        Assert.Equal("My preset", saved.PresetOverride);
    }

    [Fact]
    public void AFileTheMonitorPageTrackedMeanwhile_IsKept_WhenThePassPrunesAnother()
    {
        var (memory, baseline) = Start(Entry("a.mkv", 0), Entry("gone.mkv", 1));
        _store.Update(Path_, state => { state.Add(Entry("new-from-monitor.mkv", 2)); return true; });
        memory.RemoveAll(e => e.FullName.EndsWith("gone.mkv"));

        ResumeResults.SaveChanges(_store, baseline, memory, Path_);

        var names = _store.Load(Path_).Select(e => Path.GetFileName(e.FullName)).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "a.mkv", "new-from-monitor.mkv" }, names);
    }

    [Fact]
    public void ARemappedFile_TakesTheLatestEditsOfItsOldEntryToItsNewPath()
    {
        var (memory, baseline) = Start(Entry("old name.mkv", 3), Entry("other.mkv", 4));
        EditOnDisk("old name.mkv", e => { e.Skipped = true; e.PresetOverride = "Fast"; });   // edited after the refresh
        memory.Single(e => e.FullName.EndsWith("old name.mkv")).FullName = @"C:\In\New Name (2020).mkv";   // the pass's FileBot remap

        ResumeResults.SaveChanges(_store, baseline, memory, Path_,
            new Dictionary<string, string> { [@"C:\In\old name.mkv"] = @"C:\In\New Name (2020).mkv" });

        var after = _store.Load(Path_);
        Assert.DoesNotContain(after, e => e.FullName.EndsWith("old name.mkv"));
        var moved = after.Single(e => e.FullName.EndsWith("New Name (2020).mkv"));
        Assert.True(moved.Skipped);
        Assert.Equal("Fast", moved.PresetOverride);
        Assert.Equal(3, moved.Order);
    }

    [Fact]
    public void NothingChanged_MeansNothingIsWritten()
    {
        var (memory, baseline) = Start(Entry("a.mkv", 0));
        var stamp = File.GetLastWriteTimeUtc(Path_);
        Thread.Sleep(30);

        ResumeResults.SaveChanges(_store, baseline, memory, Path_);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path_));
    }

    [Fact]
    public void AnEmptiedQueue_IsAMissingFile_WhichReadsTheSame()
    {
        _store.Save(new List<ResumeEntry> { Entry("a.mkv", 0, ResumeStatus.Completed) }, Path_);

        _store.Update(Path_, state => { state.Clear(); return true; });

        Assert.False(File.Exists(Path_));
        Assert.Empty(_store.Load(Path_));
    }
}
