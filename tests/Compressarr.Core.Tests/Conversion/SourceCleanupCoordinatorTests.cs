using Compressarr.Core.Arr;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Logging;
using Compressarr.Core.Routing;

namespace Compressarr.Core.Tests.Conversion;

internal sealed class CoordinatorTestLogger : IRunLogger
{
#pragma warning disable CS0067
    public event Action<string, LogSeverity>? LineWritten;
#pragma warning restore CS0067
    public List<(string Message, LogSeverity Severity)> Logs { get; } = new();
    public bool HasLoggedError => Logs.Any(l => l.Severity == LogSeverity.Error);

    public string Initialize(string logFilePath, string timestamp) => "";
    public void Log(string message, LogSeverity severity = LogSeverity.Info) => Logs.Add((message, severity));
    public void LogProblem(string key, string message) => Log(message, LogSeverity.Error);
    public void ClearProblem(string key) { }
    public bool HasLaneProblemsChanged(string laneId, IReadOnlyCollection<string> problemCodes) => true;
    public void FileStart(string laneDisplayName, int index, int total, string fileName, double sizeGb, string contentType, string preset) { }
    public void FileComplete(string fileName, double beginSizeGb, double endSizeGb, TimeSpan duration, bool success, string? detailLogFile) { }
}

internal sealed class CoordinatorTestArr : IArrUnmonitorService
{
    public ArrUnmonitorResult? Result { get; set; }
    public Exception? Throw { get; set; }
    public List<(string FileName, bool IsTv)> Calls { get; } = new();

    public Task<ArrUnmonitorResult> UnmonitorAsync(CompressarrConfig config, string fileName, bool isTv, CancellationToken cancellationToken = default)
    {
        Calls.Add((fileName, isTv));
        if (Throw is not null) throw Throw;
        return Task.FromResult(Result!);
    }
}

internal sealed class CoordinatorTestCompanions : ICompanionFileService
{
    public Exception? Throw { get; set; }
    public List<(string Directory, string InputRoot)> Cleanups { get; } = new();

    public void MoveCompanionFiles(string originalFileFullName, string originalFileDirectory, string routedVideoDestPath, DeleteAfterConvertMode deleteAfterConvert, IReadOnlyList<string> companionExtensions, DeleteAfterConvertMode unmatchedCompanionAction, DestinationCollisionMode collisionMode = DestinationCollisionMode.Overwrite, IReadOnlyList<string>? videoExtensions = null) { }

    public void CleanUpEmptySourceFolder(string originalFileDirectory, string inputRoot, IReadOnlyList<string> vidTypes, DeleteAfterConvertMode deleteAfterConvert, DeleteAfterConvertMode unmatchedCompanionAction)
    {
        Cleanups.Add((originalFileDirectory, inputRoot));
        if (Throw is not null) throw Throw;
    }
}

/// <summary>The "tell Sonarr/Radarr, confirm the rescan, only then remove the empty source folder" sequence
/// shared by the first attempt and every retry. These pin the contract callers rely on: a failure is never
/// thrown, never reads as "safe", and the log wording (which users see) is exactly what each caller used to
/// write for itself.</summary>
public class SourceCleanupCoordinatorTests
{
    private readonly CoordinatorTestArr _arr = new();
    private readonly CoordinatorTestCompanions _companions = new();
    private readonly CoordinatorTestLogger _logger = new();
    private readonly CompressarrConfig _config = new();

    private SourceCleanupCoordinator Coordinator() => new(_arr, _companions, _logger);

    [Theory]
    [InlineData(ArrRescanOutcome.Completed, true)]
    [InlineData(ArrRescanOutcome.NoMatch, true)]
    [InlineData(ArrRescanOutcome.NotEnabled, true)]
    [InlineData(ArrRescanOutcome.Failed, false)]
    [InlineData(ArrRescanOutcome.TimedOut, false)]
    public async Task ConfirmRescan_SaysSafeOnlyForAnOutcomeThatProvesTheRescanFinished(ArrRescanOutcome outcome, bool expectedSafe)
    {
        _arr.Result = new ArrUnmonitorResult("Radarr: done", outcome);

        var result = await Coordinator().ConfirmRescanAsync(_config, "Movie (2020).mkv", isTv: false, "", logDeferred: true, CancellationToken.None);

        Assert.Equal(expectedSafe, result.Safe);
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal("Radarr: done", result.Message);
        Assert.Null(result.FailureMessage);
        Assert.Equal(("Movie (2020).mkv", false), Assert.Single(_arr.Calls));
    }

    [Fact]
    public async Task ConfirmRescan_LogsArrsOwnMessage_ThenTheDeferralLine_WhenNotConfirmed()
    {
        _arr.Result = new ArrUnmonitorResult("Sonarr: rescan started", ArrRescanOutcome.TimedOut);

        await Coordinator().ConfirmRescanAsync(_config, "Show - S01E01.mkv", isTv: true, "", logDeferred: true, CancellationToken.None);

        Assert.Equal(2, _logger.Logs.Count);
        Assert.Equal("  Sonarr: rescan started", _logger.Logs[0].Message);
        Assert.Equal("  Source folder cleanup deferred - rescan was not positively confirmed complete (TimedOut).", _logger.Logs[1].Message);
        Assert.All(_logger.Logs, l => Assert.Equal(LogSeverity.Info, l.Severity));
    }

    [Fact]
    public async Task ConfirmRescan_WithLogDeferredOff_StaysQuietAboutTheDeferral()
    {
        _arr.Result = new ArrUnmonitorResult(null, ArrRescanOutcome.TimedOut);

        var result = await Coordinator().ConfirmRescanAsync(_config, "a.mkv", false, " on cleanup retry", logDeferred: false, CancellationToken.None);

        Assert.False(result.Safe);
        Assert.Empty(_logger.Logs); // no message from *arr and no deferral line: that caller logs its own, deduplicated one
    }

    [Fact]
    public async Task ConfirmRescan_WhenArrThrows_IsLoggedAsAnError_ReportedNotSafe_AndNeverRethrown()
    {
        _arr.Throw = new InvalidOperationException("Sonarr API unreachable");

        var result = await Coordinator().ConfirmRescanAsync(_config, "a.mkv", true, " on move retry", logDeferred: true, CancellationToken.None);

        Assert.False(result.Safe);
        Assert.Null(result.Outcome);
        Assert.Equal("Sonarr API unreachable", result.FailureMessage);
        var line = Assert.Single(_logger.Logs);
        Assert.Equal("  Arr unmonitor skipped on move retry: Sonarr API unreachable", line.Message);
        Assert.Equal(LogSeverity.Error, line.Severity);
    }

    [Fact]
    public async Task ConfirmRescan_FirstAttemptWording_HasNoContextSuffix()
    {
        _arr.Throw = new InvalidOperationException("boom");

        await Coordinator().ConfirmRescanAsync(_config, "a.mkv", true, "", logDeferred: true, CancellationToken.None);

        Assert.Equal("  Arr unmonitor skipped: boom", Assert.Single(_logger.Logs).Message);
    }

    [Fact]
    public void TryCleanUp_OnSuccess_ReturnsNull_AndCleansWithTheCallersOwnInputRoot()
    {
        var error = Coordinator().TryCleanUpSourceFolder(_config, @"D:\Input\Show", @"D:\Input", " on move retry");

        Assert.Null(error);
        Assert.Equal((@"D:\Input\Show", @"D:\Input"), Assert.Single(_companions.Cleanups));
        Assert.Empty(_logger.Logs);
    }

    [Fact]
    public void TryCleanUp_AWantedCompanionOfTheVideoIsStillThere_LeavesTheFolderAlone_AndSaysSo()
    {
        var folder = Directory.CreateTempSubdirectory("compressarr-coordinator-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "Movie (2020).en.srt"), "left by a Skip");
            _config.Processing.DeleteAfterConvert = DeleteAfterConvertMode.Delete;

            var error = Coordinator().TryCleanUpSourceFolder(_config, folder, Path.GetDirectoryName(folder)!, "", Path.Combine(folder, "Movie (2020).mkv"));

            Assert.Null(error);                                   // not a failure, and nothing to retry
            Assert.Empty(_companions.Cleanups);                   // the sweep never ran
            Assert.Contains("Source folder kept", Assert.Single(_logger.Logs).Message);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TryCleanUp_WithSkip_ACompanionOfAnyVideoInTheFolderKeepsIt_NotJustTheCurrentOnes()
    {
        var folder = Directory.CreateTempSubdirectory("compressarr-coordinator-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "A.en.srt"), "left by a Skip for A, which finished earlier");
            _config.Processing.DeleteAfterConvert = DeleteAfterConvertMode.Delete;
            _config.Processing.OnDestinationCollision = DestinationCollisionMode.Skip;

            var error = Coordinator().TryCleanUpSourceFolder(_config, folder, Path.GetDirectoryName(folder)!, "", Path.Combine(folder, "B.mkv"));   // B finishes now

            Assert.Null(error);
            Assert.Empty(_companions.Cleanups);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("A-source")]
    [InlineData("A-source/extras")]
    [InlineData("a/b/c")]
    public void TryCleanUp_WithSkip_ACompanionInAVideoFreeFolderBelow_KeepsEverythingAbove(string childPath)
    {
        // Input/batch/<child>/A.en.srt was left by a Skip when A finished; B, directly in batch, finishes later and its cleanup must not recurse over it.
        var folder = Directory.CreateTempSubdirectory("compressarr-coordinator-").FullName;
        try
        {
            var child = Path.Combine(folder, childPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(child);
            File.WriteAllText(Path.Combine(child, "A.en.srt"), "left by a Skip for A");
            _config.Processing.DeleteAfterConvert = DeleteAfterConvertMode.Delete;
            _config.Processing.OnDestinationCollision = DestinationCollisionMode.Skip;

            var error = Coordinator().TryCleanUpSourceFolder(_config, folder, Path.GetDirectoryName(folder)!, "", Path.Combine(folder, "B.mkv"));

            Assert.Null(error);
            Assert.Empty(_companions.Cleanups);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TryCleanUp_WithSkip_AFolderBelowWithNoCompanionAtAnyDepth_DoesNotStopTheSweep()
    {
        var folder = Directory.CreateTempSubdirectory("compressarr-coordinator-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "old", "deeper"));
            File.WriteAllText(Path.Combine(folder, "old", "deeper", "readme.txt"), "not a wanted companion");
            _config.Processing.DeleteAfterConvert = DeleteAfterConvertMode.Delete;
            _config.Processing.OnDestinationCollision = DestinationCollisionMode.Skip;

            Coordinator().TryCleanUpSourceFolder(_config, folder, Path.GetDirectoryName(folder)!, "", Path.Combine(folder, "B.mkv"));

            Assert.Single(_companions.Cleanups);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TryCleanUp_WithoutSkip_ACompanionInAFolderBelowIsStillAnOrphan()
    {
        var folder = Directory.CreateTempSubdirectory("compressarr-coordinator-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "A-source"));
            File.WriteAllText(Path.Combine(folder, "A-source", "A.en.srt"), "an orphan");
            _config.Processing.DeleteAfterConvert = DeleteAfterConvertMode.Delete;
            _config.Processing.OnDestinationCollision = DestinationCollisionMode.Overwrite;

            Coordinator().TryCleanUpSourceFolder(_config, folder, Path.GetDirectoryName(folder)!, "", Path.Combine(folder, "B.mkv"));

            Assert.Single(_companions.Cleanups);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TryCleanUp_WithoutSkip_ALeftoverOfAnotherStemIsAnOrphanAndTheSweepRuns()
    {
        var folder = Directory.CreateTempSubdirectory("compressarr-coordinator-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "A.en.srt"), "an orphan");
            _config.Processing.DeleteAfterConvert = DeleteAfterConvertMode.Delete;
            _config.Processing.OnDestinationCollision = DestinationCollisionMode.Overwrite;

            Coordinator().TryCleanUpSourceFolder(_config, folder, Path.GetDirectoryName(folder)!, "", Path.Combine(folder, "B.mkv"));

            Assert.Single(_companions.Cleanups);                    // unchanged: orphans are what the unmatched-leftover setting is for
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TryCleanUp_WithMaintain_CompanionsAlwaysRemainAndThatIsNotNews()
    {
        var folder = Directory.CreateTempSubdirectory("compressarr-coordinator-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(folder, "Movie (2020).en.srt"), "copied, not moved");
            _config.Processing.DeleteAfterConvert = DeleteAfterConvertMode.Maintain;

            Coordinator().TryCleanUpSourceFolder(_config, folder, Path.GetDirectoryName(folder)!, "", Path.Combine(folder, "Movie (2020).mkv"));

            Assert.Single(_companions.Cleanups);                  // handed on as before (it does nothing for Maintain) ...
            Assert.Empty(_logger.Logs);                           // ... with no "folder kept" line on every pass
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TryCleanUp_OnFailure_ReturnsTheMessage_AndLogsItWithTheContext()
    {
        _companions.Throw = new IOException("file is locked");

        var error = Coordinator().TryCleanUpSourceFolder(_config, @"D:\Input\Show", @"D:\Input", " on companion retry");

        Assert.Equal("file is locked", error);
        var line = Assert.Single(_logger.Logs);
        Assert.Equal("  Source folder cleanup skipped on companion retry: file is locked", line.Message);
        Assert.Equal(LogSeverity.Error, line.Severity);
    }

    [Fact]
    public void TryCleanUp_WithANullContext_LeavesLoggingToTheCaller()
    {
        _companions.Throw = new IOException("file is locked");

        var error = Coordinator().TryCleanUpSourceFolder(_config, @"D:\Input\Show", @"D:\Input", null);

        Assert.Equal("file is locked", error);
        Assert.Empty(_logger.Logs);
    }
}
