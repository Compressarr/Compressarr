using Compressarr.Core.Conversion;
using Compressarr.Core.Queue;

namespace Compressarr.Core.Tests.Queue;

file sealed class CountingScanner : IVideoFileScanner
{
    public int Calls { get; private set; }
    public IReadOnlyList<FileInfo> FindVideoFiles(string inputPath, IReadOnlyList<string> vidTypes, long minSizeBytes, int limit)
    {
        Calls++;
        return new[] { new FileInfo(Path.Combine(inputPath, $"scan{Calls}.mkv")) };
    }
}

file sealed class StepClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => _now += by;
    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>The queue display polls every second or two; each poll used to walk every lane's Input folder. The listing is reused for a few seconds, and dropped the moment the
/// file being encoded changes.</summary>
public class CachedVideoFileScannerTests
{
    private static readonly string[] Types = { "mkv" };

    [Fact]
    public void PollsInsideTheWindow_ShareOneScan_AndALaterPollScansAgain()
    {
        var inner = new CountingScanner();
        var clock = new StepClock();
        var scanner = new CachedVideoFileScanner(inner, TimeSpan.FromSeconds(3), clock);

        var first = scanner.FindVideoFiles(@"C:\In", Types, 0, 0);
        clock.Advance(TimeSpan.FromSeconds(1.5));
        var second = scanner.FindVideoFiles(@"C:\In", Types, 0, 0);
        Assert.Equal(1, inner.Calls);
        Assert.Same(first, second);

        clock.Advance(TimeSpan.FromSeconds(2));
        scanner.FindVideoFiles(@"C:\In", Types, 0, 0);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public void ADifferentFolderOrFilter_IsAnotherEntry()
    {
        var inner = new CountingScanner();
        var scanner = new CachedVideoFileScanner(inner, TimeSpan.FromSeconds(3), new StepClock());

        scanner.FindVideoFiles(@"C:\In", Types, 0, 0);
        scanner.FindVideoFiles(@"D:\Other", Types, 0, 0);
        scanner.FindVideoFiles(@"C:\In", Types, 1000, 0);
        scanner.FindVideoFiles(@"C:\In", Types, 0, 5);

        Assert.Equal(4, inner.Calls);
    }

    [Fact]
    public void WhenTheFileBeingEncodedChanges_EverythingRememberedIsDropped()
    {
        var inner = new CountingScanner();
        var scanner = new CachedVideoFileScanner(inner, TimeSpan.FromSeconds(3), new StepClock());

        scanner.ObserveCurrentFile(@"C:\In\a.mkv");
        scanner.FindVideoFiles(@"C:\In", Types, 0, 0);
        scanner.ObserveCurrentFile(@"C:\In\a.mkv");          // the same file still encoding: kept
        scanner.FindVideoFiles(@"C:\In", Types, 0, 0);
        Assert.Equal(1, inner.Calls);

        scanner.ObserveCurrentFile(@"C:\In\b.mkv");          // a.mkv finished (its source is gone), b.mkv started
        scanner.FindVideoFiles(@"C:\In", Types, 0, 0);
        Assert.Equal(2, inner.Calls);

        scanner.ObserveCurrentFile(null);                    // the run ended
        scanner.FindVideoFiles(@"C:\In", Types, 0, 0);
        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public void AZeroWindow_ScansEveryTime()
    {
        var inner = new CountingScanner();
        var scanner = new CachedVideoFileScanner(inner, TimeSpan.Zero, new StepClock());

        scanner.FindVideoFiles(@"C:\In", Types, 0, 0);
        scanner.FindVideoFiles(@"C:\In", Types, 0, 0);

        Assert.Equal(2, inner.Calls);
    }
}
