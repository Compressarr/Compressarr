using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Routing;

namespace Compressarr.Core.Tests.Conversion;

/// <summary>Replacing an original that sits at the output's own path (a lane whose Input is its library): the original is parked first and only disposed of once the output is in place, under
/// Delete and under Recycle alike; the exact parked path is reported before anything is moved.</summary>
public class PublishOverSourceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("compressarr-publish-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class BinTrash : ITrashService
    {
        private readonly string _bin;
        public bool FailDeletes { get; set; }
        public int Calls { get; private set; }
        public BinTrash(string bin) { _bin = bin; Directory.CreateDirectory(bin); }
        public void DeleteFile(string path, DeleteAfterConvertMode mode)
        {
            Calls++;
            if (FailDeletes) throw new IOException("locked");
            if (mode == DeleteAfterConvertMode.Recycle) File.Move(path, Path.Combine(_bin, Path.GetFileName(path)));
            else File.Delete(path);
        }
        public void DeleteFolder(string path, DeleteAfterConvertMode mode) { }
    }

    /// <summary>The production contract: a removal that fails is reported to the log and the call RETURNS NORMALLY - the file is still there.</summary>
    private sealed class SwallowingTrash : ITrashService
    {
        public int Calls { get; private set; }
        public void DeleteFile(string path, DeleteAfterConvertMode mode) => Calls++;
        public void DeleteFolder(string path, DeleteAfterConvertMode mode) { }
    }

    private sealed class FailingRecycleTrash : TrashServiceBase
    {
        public FailingRecycleTrash(ITrashWarningSink warnings) : base(warnings) { }
        protected override void MoveFileToTrash(string path) => throw new IOException("recycle failed");
        protected override void MoveFolderToTrash(string path) => throw new IOException("recycle failed");
    }

    private sealed class CollectingSink : ITrashWarningSink
    {
        public List<string> Messages { get; } = new();
        public void Warn(string message) => Messages.Add(message);
    }

    /// <summary>Locks the file the moment the production service is asked to remove it, and keeps it locked: the removal (and the fallback after a failed recycle) then fails inside the service.</summary>
    private sealed class LockingTrash : ITrashService, IDisposable
    {
        private readonly ITrashService _inner;
        private FileStream? _lock;
        public LockingTrash(ITrashService inner) { _inner = inner; }
        public void DeleteFile(string path, DeleteAfterConvertMode mode)
        {
            _lock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            _inner.DeleteFile(path, mode);
        }
        public void DeleteFolder(string path, DeleteAfterConvertMode mode) => _inner.DeleteFolder(path, mode);
        public void Dispose() => _lock?.Dispose();
    }

    private (string Source, string Staged, BinTrash Trash, List<string?> Notes) Arrange()
    {
        var source = Path.Combine(_dir, "Library", "Movie (2020)", "Movie (2020).mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "original bytes");
        var staged = Path.Combine(_dir, "Output", "Movie (2020).compressarr-abc.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
        File.WriteAllText(staged, "the encode");
        return (source, staged, new BinTrash(Path.Combine(_dir, "bin")), new List<string?>());
    }

    private static string[] ParkedFolders(string source) => Directory.GetDirectories(Path.GetDirectoryName(source)!, FileOutcomeHelpers.ParkedOriginalFolderPrefix + "*");

    [Theory]
    [InlineData(DeleteAfterConvertMode.Delete)]
    [InlineData(DeleteAfterConvertMode.Recycle)]
    public void PublishesTheOutput_ThenDisposesOfTheParkedOriginal_AndReportsThePathFirst(DeleteAfterConvertMode mode)
    {
        var (source, staged, trash, notes) = Arrange();
        var existedWhenReported = false;

        var leftover = FileOutcomeHelpers.PublishOverSource(trash, source, mode, staged, source, parked =>
        {
            notes.Add(parked);
            if (parked is not null) existedWhenReported = File.Exists(source) && !File.Exists(parked);   // reported BEFORE anything was moved
        });

        Assert.Null(leftover);
        Assert.True(existedWhenReported);
        Assert.Equal(2, notes.Count);                                                  // the path, then null once resolved
        Assert.StartsWith(Path.Combine(Path.GetDirectoryName(source)!, FileOutcomeHelpers.ParkedOriginalFolderPrefix), notes[0]);
        Assert.Equal(Path.GetFileName(source), Path.GetFileName(notes[0]));            // the original keeps its own name inside the parked folder
        Assert.Null(notes[1]);
        Assert.Equal("the encode", File.ReadAllText(source));
        Assert.False(File.Exists(staged));
        Assert.Empty(ParkedFolders(source));
        if (mode == DeleteAfterConvertMode.Recycle) Assert.Equal("original bytes", File.ReadAllText(Path.Combine(_dir, "bin", "Movie (2020).mkv")));   // recoverable under its own name
    }

    [Theory]
    [InlineData(DeleteAfterConvertMode.Delete)]
    [InlineData(DeleteAfterConvertMode.Recycle)]
    public void AFailedPublish_PutsTheOriginalBack_NeverTouchesTheBin_AndKeepsTheStagedOutput(DeleteAfterConvertMode mode)
    {
        if (!OperatingSystem.IsWindows()) return;   // relies on a file lock blocking the rename
        var (source, staged, trash, notes) = Arrange();

        using (new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.ThrowsAny<IOException>(() => FileOutcomeHelpers.PublishOverSource(trash, source, mode, staged, source, notes.Add));

        Assert.Equal("original bytes", File.ReadAllText(source));       // exactly where it was
        Assert.Equal("the encode", File.ReadAllText(staged));           // staged output untouched
        Assert.Equal(0, trash.Calls);                                   // nothing was recycled or deleted - not even as a fallback - before the output was in place
        Assert.Empty(ParkedFolders(source));
        Assert.Null(notes[^1]);                                         // and the record was released
    }

    [Theory]
    [InlineData(DeleteAfterConvertMode.Delete)]
    [InlineData(DeleteAfterConvertMode.Recycle)]
    public void AnOriginalThatCannotBeDisposedOfAfterPublishing_StaysParkedAndOnRecord(DeleteAfterConvertMode mode)
    {
        var (source, staged, trash, notes) = Arrange();
        trash.FailDeletes = true;

        var leftover = FileOutcomeHelpers.PublishOverSource(trash, source, mode, staged, source, notes.Add);

        Assert.Equal("the encode", File.ReadAllText(source));            // the output is in place
        Assert.NotNull(leftover);
        Assert.Equal("original bytes", File.ReadAllText(leftover!));     // and the original is still there, parked
        Assert.Single(notes);                                            // only the path was reported: the record stays until it is resolved
        Assert.Equal(leftover, notes[0]);
    }

    [Theory]
    [InlineData(DeleteAfterConvertMode.Delete)]
    [InlineData(DeleteAfterConvertMode.Recycle)]
    public void AServiceThatLogsAFailedRemovalAndReturnsNormally_DoesNotClearTheRecord(DeleteAfterConvertMode mode)
    {
        var (source, staged, _, notes) = Arrange();
        var trash = new SwallowingTrash();

        var leftover = FileOutcomeHelpers.PublishOverSource(trash, source, mode, staged, source, notes.Add);

        Assert.Equal(1, trash.Calls);
        Assert.Equal("the encode", File.ReadAllText(source));
        Assert.NotNull(leftover);                                        // the call returned, but the file did not go
        Assert.Equal("original bytes", File.ReadAllText(leftover!));
        Assert.Single(notes);                                            // the record was NOT cleared
        Assert.Single(ParkedFolders(source));
    }

    [Theory]
    [InlineData(DeleteAfterConvertMode.Delete)]
    [InlineData(DeleteAfterConvertMode.Recycle)]
    public void TheProductionTrashService_WithALockedOriginal_KeepsTheRecord(DeleteAfterConvertMode mode)
    {
        if (!OperatingSystem.IsWindows()) return;   // relies on a file lock blocking the delete
        var (source, staged, _, notes) = Arrange();
        var sink = new CollectingSink();
        using var trash = new LockingTrash(new FailingRecycleTrash(sink));   // Recycle fails, then the fallback Delete fails on the lock: logged, and it returns normally

        var leftover = FileOutcomeHelpers.PublishOverSource(trash, source, mode, staged, source, notes.Add);

        Assert.NotEmpty(sink.Messages);                                  // the service reported the failure to its log, and nothing else
        Assert.Equal("the encode", File.ReadAllText(source));
        Assert.NotNull(leftover);
        Assert.True(File.Exists(leftover!));                             // still on disk, beside the output
        Assert.Single(notes);                                            // and still on record
    }
}
