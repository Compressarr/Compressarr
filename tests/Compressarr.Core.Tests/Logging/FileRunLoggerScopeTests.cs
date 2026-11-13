using Compressarr.Core.Logging;

namespace Compressarr.Core.Tests.Logging;

public class FileRunLoggerScopeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("compressarr-logger-scope-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private (FileRunLogger Logger, string File, List<string> Live) Start()
    {
        var logger = new FileRunLogger();
        var file = logger.Initialize(_dir, "20260101_000000");
        var live = new List<string>();
        logger.LineWritten += (message, _) => live.Add(message);
        return (logger, file, live);
    }

    [Fact]
    public void ADiscardedScope_WritesNothingToTheFile_ButTheLiveLogStillSeesIt()
    {
        var (logger, file, live) = Start();
        logger.Log("before");

        logger.BeginFileScope();
        logger.Log("Name   : secret clip.mkv");
        logger.EndFileScope(discard: true);
        logger.Log("after");

        var text = File.ReadAllText(file);
        Assert.Contains("before", text);
        Assert.Contains("after", text);
        Assert.DoesNotContain("secret clip", text);
        Assert.Contains("Name   : secret clip.mkv", live);
    }

    [Fact]
    public void AKeptScope_WritesItsLinesInOrder()
    {
        var (logger, file, _) = Start();

        logger.BeginFileScope();
        logger.Log("one");
        logger.Log("two");
        logger.EndFileScope(discard: false);

        Assert.Equal(new[] { "one", "two" }, File.ReadAllLines(file));
    }

    [Fact]
    public void ADiscardedScope_StillKeepsItsLines_WhenOneOfThemIsAnError()
    {
        var (logger, file, _) = Start();

        logger.BeginFileScope();
        logger.Log("fine");
        logger.Log("  FAILED : secret clip.mkv", LogSeverity.Error);
        logger.EndFileScope(discard: true);

        Assert.Contains("FAILED : secret clip.mkv", File.ReadAllText(file));
        Assert.True(logger.HasLoggedError);
    }

    [Fact]
    public void AHeldError_DoesNotCountAsLoggedUntilItIsKept()
    {
        var (logger, _, _) = Start();

        logger.BeginFileScope();
        logger.Log("an error", LogSeverity.Error);
        var duringScope = logger.HasLoggedError;
        logger.EndFileScope(discard: false);

        Assert.False(duringScope);
        Assert.True(logger.HasLoggedError);
    }

    [Fact]
    public void EndingAScopeThatWasNeverStarted_DoesNothing()
    {
        var (logger, file, _) = Start();
        logger.Log("line");

        logger.EndFileScope(discard: true);

        Assert.Equal(new[] { "line" }, File.ReadAllLines(file));
    }
}
