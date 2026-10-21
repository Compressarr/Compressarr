using Compressarr.Core.Arr;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.FileBot;
using Compressarr.Core.Logging;
using Compressarr.Core.Orchestration;
using Compressarr.Core.Presets;
using Compressarr.Core.Routing;

namespace Compressarr.Core.Tests.Conversion;

file sealed class PassThroughPathExpander : IPathExpander
{
    public string Expand(string value) => value;
    public bool PathExists(string value) => Directory.Exists(value) || File.Exists(value);
}

file sealed class RealFolderScanner : IVideoFileScanner
{
    public IReadOnlyList<FileInfo> FindVideoFiles(string inputPath, IReadOnlyList<string> vidTypes, long minSizeBytes, int limit) =>
        Directory.GetFiles(inputPath).Select(f => new FileInfo(f)).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
}

// Unlike RealFolderScanner above (deliberately flat, used by most tests in this file),
// recurses into subfolders - matching the real VideoFileScanner's own behavior. Needed for a
// "one folder per movie" library layout (e.g. Input\Caddyshack (1980)\Caddyshack (1980).mkv),
// which is exactly the shape the orphaned-source-folder bug this file also tests for showed up
// in live.
file sealed class RecursiveFolderScanner : IVideoFileScanner
{
    public IReadOnlyList<FileInfo> FindVideoFiles(string inputPath, IReadOnlyList<string> vidTypes, long minSizeBytes, int limit) =>
        Directory.GetFiles(inputPath, "*", SearchOption.AllDirectories).Select(f => new FileInfo(f)).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
}

file sealed class NoOpFileBotRunner : IFileBotRunner
{
    public FileBotRunResult Run(FileBotSettings settings, string inputPath, IReadOnlyList<string> vidTypes, IRunLogger logger) => FileBotRunResult.Empty;
}

file sealed class FixedUnmatchedFileBotRunner : IFileBotRunner
{
    private readonly HashSet<string> _unmatched;
    public FixedUnmatchedFileBotRunner(HashSet<string> unmatched) => _unmatched = unmatched;
    public FileBotRunResult Run(FileBotSettings settings, string inputPath, IReadOnlyList<string> vidTypes, IRunLogger logger) => new(_unmatched, new Dictionary<string, string>());
}

/// <summary>Stands in for FileBot renaming files: really moves each file on disk (unless told to
/// only announce it) and reports the old -> new pairs the way the real runner parses them.</summary>
file sealed class RenamingFileBotRunner : IFileBotRunner
{
    private readonly Dictionary<string, string> _renames;
    private readonly bool _actuallyMove;
    public RenamingFileBotRunner(Dictionary<string, string> renames, bool actuallyMove = true)
    {
        _renames = renames;
        _actuallyMove = actuallyMove;
    }

    public FileBotRunResult Run(FileBotSettings settings, string inputPath, IReadOnlyList<string> vidTypes, IRunLogger logger)
    {
        if (_actuallyMove)
        {
            foreach (var (from, to) in _renames) File.Move(from, to);
        }
        return new FileBotRunResult(new HashSet<string>(), _renames);
    }
}

file sealed class RecordingFileBotRunner : IFileBotRunner
{
    public string? ReceivedCliPath { get; private set; }
    public FileBotRunResult Run(FileBotSettings settings, string inputPath, IReadOnlyList<string> vidTypes, IRunLogger logger)
    {
        ReceivedCliPath = settings.CliPath;
        return FileBotRunResult.Empty;
    }
}

/// <summary>Expands one specific token, unlike PassThroughPathExpander (identity) - needed to
/// prove a caller actually calls Expand rather than passing a raw, still-tokenized value
/// through untouched (both look identical under an identity expander).</summary>
file sealed class TokenPathExpander : IPathExpander
{
    public string Expand(string value) => value.Replace("%TestToken%", @"C:\Expanded\FileBot.exe");
    public bool PathExists(string value) => Directory.Exists(value) || File.Exists(value);
}

file sealed class FixedExtensionPresetService : IEncoderPresetService
{
    public IReadOnlyList<string> GetPresetNames() => Array.Empty<string>();
    public bool PresetExists(string presetName) => true;
    public string GetOutputExtension(string presetName, out string? warning) { warning = null; return ".mkv"; }
    public string PreparePresetSource() => "presets.json";
}

/// <summary>Simulates HandBrakeCLI by writing a real (tiny) output file, since the orchestrator
/// moves/measures it for real afterward.</summary>
file sealed class FakeProcessRunner : IEncoderRunner
{
    public int CallCount { get; private set; }

    public Task<EncodeResult> RunAsync(EncodeRequest request, Action<EncodeProgress>? onProgress, CancellationToken cancellationToken)
    {
        CallCount++;
        File.WriteAllText(request.OutputPath, "fake encoded output");
        // Matches real HandBrakeProcessRunner - it always writes a detail log, success or not, so
        // KeepSuccessfulHandBrakeLogs-driven deletion has a real file to actually exercise.
        Directory.CreateDirectory(Path.GetDirectoryName(request.DetailLogFile)!);
        File.WriteAllText(request.DetailLogFile, "fake HandBrake detail output");
        return Task.FromResult(new EncodeResult(Success: true, DetailLogFile: request.DetailLogFile));
    }
}

/// <summary>Fires a callback the first time RunAsync is called, before writing the fake encoded
/// output - simulates a concurrent web request (e.g. POST /api/run/queue/reorder) landing while
/// HandBrakeCLI is still "encoding" the first file, the exact live scenario that exposed the
/// resume-state lost-update bug (a user's reorder/skip/preset-override change got silently wiped
/// the moment the in-flight file finished).</summary>
file sealed class ConcurrentEditProcessRunner : IEncoderRunner
{
    private readonly Action _onFirstRun;
    private bool _fired;

    public ConcurrentEditProcessRunner(Action onFirstRun) => _onFirstRun = onFirstRun;

    public Task<EncodeResult> RunAsync(EncodeRequest request, Action<EncodeProgress>? onProgress, CancellationToken cancellationToken)
    {
        if (!_fired)
        {
            _fired = true;
            _onFirstRun();
        }
        File.WriteAllText(request.OutputPath, "fake encoded output");
        return Task.FromResult(new EncodeResult(Success: true, DetailLogFile: request.DetailLogFile));
    }
}

/// <summary>Simulates the exact real failure captured live against a genuinely full disk: a
/// truncated temp output file, a failed EncodeResult, and the real HandBrakeCLI log content
/// observed (mux error naming "No space left on device", "Finished work at" printed anyway,
/// "Encode failed").</summary>
file sealed class FailingProcessRunner : IEncoderRunner
{
    public Task<EncodeResult> RunAsync(EncodeRequest request, Action<EncodeProgress>? onProgress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(request.DetailLogFile)!);
        File.WriteAllText(request.DetailLogFile, "Encode failed (error 1).\n");
        return Task.FromResult(new EncodeResult(Success: false, DetailLogFile: request.DetailLogFile));
    }
}

file sealed class DiskFullProcessRunner : IEncoderRunner
{
    public Task<EncodeResult> RunAsync(EncodeRequest request, Action<EncodeProgress>? onProgress, CancellationToken cancellationToken)
    {
        File.WriteAllText(request.OutputPath, "truncated, disk filled up mid-write");
        File.WriteAllText(request.DetailLogFile,
            "ERROR: avformatMux: track 0, av_interleaved_write_frame failed with error 'No space left on device'\n" +
            "Finished work at Sat Jan  1 00:00:00 2026\n" +
            "libhb: work result = 4\n" +
            "Encode failed (error 4).\n");
        return Task.FromResult(new EncodeResult(Success: false, DetailLogFile: request.DetailLogFile));
    }
}

/// <summary>Returns configAfterSwitch starting from its (1-based) switchOnCall'th Load() call and
/// every call after - ConversionOrchestrator.ProcessLaneAsync calls Load() exactly once per file,
/// immediately after that file's HandBrakeCLI pass finishes, so this ties the "settings changed"
/// moment precisely to a specific file's reload point rather than guessing at timing.</summary>
file sealed class SwitchingConfigStore : IConfigStore
{
    private readonly CompressarrConfig _configBeforeSwitch;
    private readonly CompressarrConfig _configAfterSwitch;
    private readonly int _switchOnCall;
    private int _callCount;

    public SwitchingConfigStore(CompressarrConfig configBeforeSwitch, CompressarrConfig configAfterSwitch, int switchOnCall)
    {
        _configBeforeSwitch = configBeforeSwitch;
        _configAfterSwitch = configAfterSwitch;
        _switchOnCall = switchOnCall;
    }

    public CompressarrConfig Load(string path)
    {
        _callCount++;
        return _callCount >= _switchOnCall ? _configAfterSwitch : _configBeforeSwitch;
    }

    public void Save(CompressarrConfig config, string path) { }
    public T Update<T>(string path, Func<CompressarrConfig, T> mutate) => mutate(_configBeforeSwitch);
}

/// <summary>Simulates a move failure whose exception isn't disk-full and isn't shaped like a
/// missing/unreachable path (e.g. a permission error) - used to prove those don't get mislabeled
/// with the path-unavailable reason.</summary>
file sealed class ThrowingFileRouter : IFileRouter
{
    private readonly Exception _exception;
    public ThrowingFileRouter(Exception exception) => _exception = exception;
    public string? RouteFile(string sourcePath, string desiredFileName, bool isTv, string tvShowBasePath, string movieBasePath, bool moveFiles, DestinationCollisionMode collisionMode = DestinationCollisionMode.Overwrite, string? showEdition = null) => throw _exception;
}

file sealed record TrashCall(string Path, DeleteAfterConvertMode Mode);

file sealed class RecordingTrashService : ITrashService
{
    public List<TrashCall> Calls { get; } = new();
    public void DeleteFile(string path, DeleteAfterConvertMode mode) => Calls.Add(new TrashCall(path, mode));
    public void DeleteFolder(string path, DeleteAfterConvertMode mode) { }
}

/// <summary>Actually deletes, unlike RecordingTrashService - needed for the move-retry tests, which
/// need the source file genuinely gone from Input after a DeleteAfterConvert pass so a second
/// ProcessLaneAsync call's fresh scan doesn't rediscover it and flip the just-completed resume
/// entry back to Pending (a rescan reusing an existing entry for the same still-present path is a
/// separate, pre-existing behavior of Maintain-style setups, not something these tests are about).</summary>
file sealed class DeletingTrashService : ITrashService
{
    public void DeleteFile(string path, DeleteAfterConvertMode mode) { try { File.Delete(path); } catch { } }
    public void DeleteFolder(string path, DeleteAfterConvertMode mode) { try { Directory.Delete(path, recursive: true); } catch { } }
}

file sealed class NoOpRunLogger : IRunLogger
{
    public event Action<string, LogSeverity>? LineWritten { add { } remove { } }
    public bool HasLoggedError => false;
    public string Initialize(string logFilePath, string timestamp) => Path.GetTempFileName();
    public void Log(string message, LogSeverity severity = LogSeverity.Info) { }
    public void LogProblem(string key, string message) { }
    public void ClearProblem(string key) { }
    public bool HasLaneProblemsChanged(string laneId, IReadOnlyCollection<string> problemCodes) => true;
    public void FileStart(string laneDisplayName, int index, int total, string fileName, double sizeGb, string contentType, string preset) { }
    public void FileComplete(string fileName, double beginSizeGb, double endSizeGb, TimeSpan duration, bool success, string? detailLogFile) { }
}

file sealed class RecordingRunLogger : IRunLogger
{
    public event Action<string, LogSeverity>? LineWritten { add { } remove { } }
    public List<(string Message, LogSeverity Severity)> Logs { get; } = new();
    public bool HasLoggedError => Logs.Any(l => l.Severity == LogSeverity.Error);
    private readonly Dictionary<string, string> _lastProblemMessages = new();
    private readonly Dictionary<string, string> _lastReportedLaneProblems = new();
    public string Initialize(string logFilePath, string timestamp) { Logs.Clear(); return Path.GetTempFileName(); }
    public void Log(string message, LogSeverity severity = LogSeverity.Info) => Logs.Add((message, severity));
    public void LogProblem(string key, string message)
    {
        var changed = !_lastProblemMessages.TryGetValue(key, out var last) || last != message;
        Log(changed ? message : $"{message} (still unresolved, same as last check)", changed ? LogSeverity.Error : LogSeverity.Info);
        _lastProblemMessages[key] = message;
    }
    public void ClearProblem(string key) => _lastProblemMessages.Remove(key);
    public bool HasLaneProblemsChanged(string laneId, IReadOnlyCollection<string> problemCodes)
    {
        var current = string.Join(",", problemCodes.OrderBy(c => c, StringComparer.Ordinal));
        var changed = !_lastReportedLaneProblems.TryGetValue(laneId, out var last) || last != current;
        _lastReportedLaneProblems[laneId] = current;
        return changed;
    }
    public void FileStart(string laneDisplayName, int index, int total, string fileName, double sizeGb, string contentType, string preset) { }
    public void FileComplete(string fileName, double beginSizeGb, double endSizeGb, TimeSpan duration, bool success, string? detailLogFile) { }
}

file sealed class NoOpProgressReporter : IRunProgressReporter
{
    public void RunStarted(string timestamp) { }
    public void LaneStarted(string laneId, string laneDisplayName, bool isResumed) { }
    public void FileBotStarted(string laneId) { }
    public void FileBotCompleted(string laneId) { }
    public void FileStarted(string laneId, int index, int total, string fileName, string fullName, string? presetName, double sizeGb) { }
    public void FileProgress(string laneId, double percent, double? fps, string? eta) { }
    public void FileCompleted(string laneId, string fileName, bool success) { }
    public void FileThroughputSample(string? presetName, double gb, TimeSpan duration) { }
    public void RunCompleted(int totalFiles) { }
}

file sealed class NoOpArrUnmonitorService : IArrUnmonitorService
{
    public Task<ArrUnmonitorResult> UnmonitorAsync(CompressarrConfig config, string fileName, bool isTv, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ArrUnmonitorResult(null, ArrRescanOutcome.NotEnabled));
}

/// <summary>Fires a callback from inside UnmonitorAsync - simulates a concurrent web queue edit
/// landing during the *arr rescan-confirmation step specifically, AFTER ConversionOrchestrator's
/// own one-time RefreshResumeState call (right after HandBrake finishes) but BEFORE its final save.
/// This is the exact gap a code review found live: RefreshResumeState only runs once per file, but
/// routing, companion-file movement, and this arr step all still run afterward - an edit to a
/// DIFFERENT file landing here was previously silently wiped the moment the in-flight file's own
/// blind Save(resumeState, ...) ran, unlike the already-fixed "edit lands mid-encode" case
/// ConcurrentEditProcessRunner covers above.</summary>
file sealed class ConcurrentEditArrUnmonitorService : IArrUnmonitorService
{
    private readonly Action _onUnmonitor;
    public ConcurrentEditArrUnmonitorService(Action onUnmonitor) => _onUnmonitor = onUnmonitor;

    public Task<ArrUnmonitorResult> UnmonitorAsync(CompressarrConfig config, string fileName, bool isTv, CancellationToken cancellationToken = default)
    {
        _onUnmonitor();
        return Task.FromResult(new ArrUnmonitorResult(null, ArrRescanOutcome.NotEnabled));
    }
}

file sealed class ThrowingArrUnmonitorService : IArrUnmonitorService
{
    public Task<ArrUnmonitorResult> UnmonitorAsync(CompressarrConfig config, string fileName, bool isTv, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Sonarr API unreachable");
}

file sealed class RecordingArrUnmonitorService : IArrUnmonitorService
{
    public int CallCount { get; private set; }

    public Task<ArrUnmonitorResult> UnmonitorAsync(CompressarrConfig config, string fileName, bool isTv, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(new ArrUnmonitorResult("Unmonitored", ArrRescanOutcome.Completed));
    }
}

/// <summary>Returns whatever ArrRescanOutcome the test asks for, without a throw - lets a test
/// exercise the SafeToCleanUp gating in ConversionOrchestrator directly (a Failed/TimedOut/
/// Cancelled outcome is a normal, non-throwing return, not an exception - see
/// IArrUnmonitorService.UnmonitorAsync's own doc comment for why).</summary>
file sealed class FixedOutcomeArrUnmonitorService : IArrUnmonitorService
{
    private readonly ArrRescanOutcome _outcome;
    public FixedOutcomeArrUnmonitorService(ArrRescanOutcome outcome) => _outcome = outcome;

    public Task<ArrUnmonitorResult> UnmonitorAsync(CompressarrConfig config, string fileName, bool isTv, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ArrUnmonitorResult("Radarr: unmonitored the matching movie and rescanned the library.", _outcome));
}

/// <summary>Returns a different outcome each call, in order - lets a test drive a multi-pass
/// scenario (e.g. TimedOut on the first pass's rescan, Completed on a later retry's fresh one)
/// against the SAME ConversionOrchestrator instance, matching how a real Sonarr/Radarr rescan that
/// eventually catches up across polls would behave. Repeats the last outcome once the list is
/// exhausted, so a test doesn't need to provide one entry per call it makes.</summary>
file sealed class SequencedOutcomeArrUnmonitorService : IArrUnmonitorService
{
    private readonly Queue<ArrRescanOutcome> _outcomes;
    private ArrRescanOutcome _last;
    public SequencedOutcomeArrUnmonitorService(params ArrRescanOutcome[] outcomes) => _outcomes = new Queue<ArrRescanOutcome>(outcomes);

    public Task<ArrUnmonitorResult> UnmonitorAsync(CompressarrConfig config, string fileName, bool isTv, CancellationToken cancellationToken = default)
    {
        _last = _outcomes.Count > 0 ? _outcomes.Dequeue() : _last;
        return Task.FromResult(new ArrUnmonitorResult("Radarr: unmonitored the matching movie and rescanned the library.", _last));
    }
}

file sealed class ThrowingCompanionFileService : ICompanionFileService
{
    public void MoveCompanionFiles(string originalFileFullName, string originalFileDirectory, string routedVideoDestPath, DeleteAfterConvertMode deleteAfterConvert, IReadOnlyList<string> companionExtensions, DeleteAfterConvertMode unmatchedCompanionAction, DestinationCollisionMode collisionMode = DestinationCollisionMode.Overwrite) =>
        throw new IOException("Access to the path is denied.");

    public void CleanUpEmptySourceFolder(string originalFileDirectory, string inputRoot, IReadOnlyList<string> vidTypes, DeleteAfterConvertMode deleteAfterConvert, DeleteAfterConvertMode unmatchedCompanionAction) =>
        throw new IOException("Access to the path is denied.");
}

file sealed class NoOpCompanionFileService : ICompanionFileService
{
    public void MoveCompanionFiles(string originalFileFullName, string originalFileDirectory, string routedVideoDestPath, DeleteAfterConvertMode deleteAfterConvert, IReadOnlyList<string> companionExtensions, DeleteAfterConvertMode unmatchedCompanionAction, DestinationCollisionMode collisionMode = DestinationCollisionMode.Overwrite) { }
    public void CleanUpEmptySourceFolder(string originalFileDirectory, string inputRoot, IReadOnlyList<string> vidTypes, DeleteAfterConvertMode deleteAfterConvert, DeleteAfterConvertMode unmatchedCompanionAction) { }
}

/// <summary>Throws on MoveCompanionFiles (simulating a real I/O failure moving a companion), but
/// records rather than throws on CleanUpEmptySourceFolder - lets a test assert directly on whether
/// the orchestrator called it at all after a companion-move failure, which ThrowingCompanionFileService
/// (throws on both) can't distinguish, since that call is already wrapped in its own try/catch either
/// way.</summary>
file sealed class ThrowOnMoveCompanionFileService : ICompanionFileService
{
    public bool CleanUpCalled { get; private set; }

    public void MoveCompanionFiles(string originalFileFullName, string originalFileDirectory, string routedVideoDestPath, DeleteAfterConvertMode deleteAfterConvert, IReadOnlyList<string> companionExtensions, DeleteAfterConvertMode unmatchedCompanionAction, DestinationCollisionMode collisionMode = DestinationCollisionMode.Overwrite) =>
        throw new IOException("Access to the path is denied.");

    public void CleanUpEmptySourceFolder(string originalFileDirectory, string inputRoot, IReadOnlyList<string> vidTypes, DeleteAfterConvertMode deleteAfterConvert, DeleteAfterConvertMode unmatchedCompanionAction) =>
        CleanUpCalled = true;
}

/// <summary>Inverse of ThrowOnMoveCompanionFileService - succeeds on MoveCompanionFiles, throws on
/// CleanUpEmptySourceFolder (simulating a locked file, permission error, antivirus interference,
/// etc during the actual folder removal, AFTER the *arr rescan has already been positively
/// confirmed complete).</summary>
file sealed class ThrowOnCleanupCompanionFileService : ICompanionFileService
{
    public void MoveCompanionFiles(string originalFileFullName, string originalFileDirectory, string routedVideoDestPath, DeleteAfterConvertMode deleteAfterConvert, IReadOnlyList<string> companionExtensions, DeleteAfterConvertMode unmatchedCompanionAction, DestinationCollisionMode collisionMode = DestinationCollisionMode.Overwrite) { }

    public void CleanUpEmptySourceFolder(string originalFileDirectory, string inputRoot, IReadOnlyList<string> vidTypes, DeleteAfterConvertMode deleteAfterConvert, DeleteAfterConvertMode unmatchedCompanionAction) =>
        throw new IOException("Access to the path is denied.");
}

/// <summary>Reads like the real store, but right after the first Load it commits an edit - the web request that lands just after a pass refreshed
/// its list and just before it saves. Deterministic: no timing involved.</summary>
file sealed class EditAfterFirstLoadStore : IResumeStateStore
{
    private readonly JsonResumeStateStore _inner = new();
    private readonly Action<JsonResumeStateStore, string> _edit;
    private bool _done;
    public EditAfterFirstLoadStore(Action<JsonResumeStateStore, string> edit) => _edit = edit;

    public List<ResumeEntry> Load(string path)
    {
        var snapshot = _inner.Load(path);
        if (!_done) { _done = true; _edit(_inner, path); }
        return snapshot;
    }

    public void Save(List<ResumeEntry> state, string path) => _inner.Save(state, path);
    public void DeleteIfComplete(List<ResumeEntry> state, string path) => _inner.DeleteIfComplete(state, path);
    public T Update<T>(string path, Func<List<ResumeEntry>, T> mutate) => _inner.Update(path, mutate);
}

file sealed class NoOpResumeStateStore : IResumeStateStore
{
    public List<ResumeEntry> Load(string path) => new();
    public void Save(List<ResumeEntry> state, string path) { }
    public void DeleteIfComplete(List<ResumeEntry> state, string path) { }
    public T Update<T>(string path, Func<List<ResumeEntry>, T> mutate) => mutate(new List<ResumeEntry>());
}

/// <summary>Records every EncodeRequest it is given (so a test can check which preset and Output
/// folder were used) and writes a tiny output file like the other fake runners.</summary>
file sealed class RequestRecordingRunner : IEncoderRunner
{
    public List<EncodeRequest> Requests { get; } = new();

    public Task<EncodeResult> RunAsync(EncodeRequest request, Action<EncodeProgress>? onProgress, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        File.WriteAllText(request.OutputPath, "fake encoded output");
        Directory.CreateDirectory(Path.GetDirectoryName(request.DetailLogFile)!);
        File.WriteAllText(request.DetailLogFile, "fake HandBrake detail output");
        return Task.FromResult(new EncodeResult(Success: true, DetailLogFile: request.DetailLogFile));
    }
}

/// <summary>Covers the mid-run config-reload fix: settings used to be read once per run/loop
/// lifetime, so a change made while a lane was still processing files had no effect until
/// restarted. ConversionOrchestrator now reloads config immediately after each file's
/// HandBrakeCLI pass finishes, before that file's own post-processing.</summary>
public class ConversionOrchestratorTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("compressarr-conversion-orchestrator-tests-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static CompressarrConfig BuildConfig(DeleteAfterConvertMode mode) => new()
    {
        Processing = new ProcessingSettings { DeleteAfterConvert = mode, MoveFiles = false, ClearTitleMetadata = false }
    };

    /// <summary>Reproduces the old single-lane ProcessLaneAsync's full-drain driving loop against
    /// the new PrepareLane + ProcessOneFileAsync split - lets every existing test below keep
    /// asserting "process this whole lane, get back every ConversionResult" without needing to
    /// know about RunOrchestrator's own global cross-lane picking loop, which is what actually
    /// drives ProcessOneFileAsync in production now. Deliberately mirrors the OLD per-lane-only
    /// picking query (Order within this lane, no cross-lane tie-break) exactly, since these tests
    /// are about ConversionOrchestrator's own per-file behavior, not RunOrchestrator's interleaving
    /// (see RunOrchestratorTests.cs for that).</summary>
    private static async Task<List<ConversionResult>> RunLaneAsync(
        IConversionOrchestrator orchestrator,
        LaneConfig lane,
        CompressarrConfig config,
        string logFilePath,
        string timestamp,
        List<ResumeEntry> resumeState,
        string resumeFilePath,
        CancellationToken cancellationToken,
        CancellationToken stopToken = default)
    {
        var results = new List<ConversionResult>();
        var context = await orchestrator.PrepareLaneAsync(lane, config, resumeState, resumeFilePath);
        if (context is null) return results;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stopToken.ThrowIfCancellationRequested();

            orchestrator.RefreshResumeState(resumeState, resumeFilePath);

            var entry = resumeState
                .Where(e => e.LaneId == lane.Id && e.Status == ResumeStatus.Pending && !e.Skipped && !e.Removed)
                .OrderBy(e => e.Order ?? int.MaxValue)
                .FirstOrDefault(e => File.Exists(e.FullName));

            if (entry is null) break;

            var result = await orchestrator.ProcessOneFileAsync(context, entry, logFilePath, timestamp, resumeState, resumeFilePath, cancellationToken);
            results.Add(result);
        }

        return results;
    }

    [Fact]
    public async Task ProcessLaneAsync_ConfigChangedMidRun_SecondFileHonorsNewValue_FirstFileDoesNot()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        // Alphabetical order matters - RealFolderScanner sorts by name, so file1 is processed
        // before file2.
        var file1Path = Path.Combine(inputDir, "file1.mkv");
        var file2Path = Path.Combine(inputDir, "file2.mkv");
        File.WriteAllText(file1Path, "source 1");
        File.WriteAllText(file2Path, "source 2");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset"
        };

        var initialConfig = BuildConfig(DeleteAfterConvertMode.Maintain);
        initialConfig.Lanes.Add(lane);
        var configAfterSwitch = BuildConfig(DeleteAfterConvertMode.Delete);
        configAfterSwitch.Lanes.Add(lane);

        // Call #1 = file1's reload (still Maintain), call #2 = file2's reload (now Delete).
        var configStore = new SwitchingConfigStore(initialConfig, configAfterSwitch, switchOnCall: 2);
        var trash = new RecordingTrashService();

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(),
            new RealFolderScanner(),
            new NoOpFileBotRunner(),
            new FixedExtensionPresetService(),
            new MetadataService(),
            new FakeProcessRunner(),
            new FileRouter(),
            new NoOpCompanionFileService(),
            new NoOpArrUnmonitorService(),
            trash,
            new NoOpRunLogger(),
            new NoOpResumeStateStore(),
            new NoOpProgressReporter(),
            configStore);

        var results = await RunLaneAsync(orchestrator,
            lane, initialConfig, _tempDir, "20260101_000000", new List<ResumeEntry>(), Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Success));

        // file1's post-processing ran while DeleteAfterConvert was still Maintain (the switch
        // only happens once file1's own HandBrakeCLI pass finishes, inside the reload point) -
        // Maintain means _trash.DeleteFile is never called for it at all.
        Assert.DoesNotContain(trash.Calls, c => c.Path == file1Path);

        // file2's post-processing ran after the reload picked up the switched-to Delete mode.
        var file2Call = Assert.Single(trash.Calls);
        Assert.Equal(file2Path, file2Call.Path);
        Assert.Equal(DeleteAfterConvertMode.Delete, file2Call.Mode);
    }

    [Fact]
    public async Task ProcessOneFileAsync_SuccessfulEncode_KeepSuccessfulHandBrakeLogsOff_DeletesTheDetailLog()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(inputDir, "file1.mkv"), "source");

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = outputDir, MoviePreset = "Any Preset" };
        var config = BuildConfig(DeleteAfterConvertMode.Maintain);
        config.Logging.KeepSuccessfulHandBrakeLogs = false; // default
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new RecordingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var results = await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", new List<ResumeEntry>(), Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.True(result.Success);
        Assert.NotNull(result.DetailLogFile);
        Assert.False(File.Exists(result.DetailLogFile), "a successful encode's own detail log should be removed once processing finishes, with the setting off");
    }

    [Fact]
    public async Task ProcessOneFileAsync_SuccessfulEncode_KeepSuccessfulHandBrakeLogsOn_KeepsTheDetailLog()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(inputDir, "file1.mkv"), "source");

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = outputDir, MoviePreset = "Any Preset" };
        var config = BuildConfig(DeleteAfterConvertMode.Maintain);
        config.Logging.KeepSuccessfulHandBrakeLogs = true;
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new RecordingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var results = await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", new List<ResumeEntry>(), Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.True(result.Success);
        Assert.NotNull(result.DetailLogFile);
        Assert.True(File.Exists(result.DetailLogFile), "a successful encode's own detail log should survive when the setting is on");
    }

    [Fact]
    public async Task ProcessOneFileAsync_FailedEncode_KeepSuccessfulHandBrakeLogsOff_StillKeepsTheDetailLog()
    {
        // The setting only affects a SUCCESSFUL encode's log - a failed one is always kept
        // regardless, since it's linked from the report (ReportErrorCode.EncodeFailed).
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(inputDir, "file1.mkv"), "source");

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = outputDir, MoviePreset = "Any Preset" };
        var config = BuildConfig(DeleteAfterConvertMode.Maintain);
        config.Logging.KeepSuccessfulHandBrakeLogs = false;
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FailingProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new RecordingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var results = await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", new List<ResumeEntry>(), Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.False(result.Success);
        Assert.NotNull(result.DetailLogFile);
        Assert.True(File.Exists(result.DetailLogFile), "a failed encode's detail log must always be kept, regardless of the setting");
    }

    [Fact]
    public async Task FailedEncode_IsHeldInTheQueue_AndNotRetriedOnTheNextPass()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(inputDir, "file1.mkv"), "source");

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = outputDir, MoviePreset = "Any Preset" };
        var config = BuildConfig(DeleteAfterConvertMode.Maintain);
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);
        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FailingProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new RecordingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);
        var state = new List<ResumeEntry>();
        var resumePath = Path.Combine(_tempDir, "resume.json");

        var first = await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", state, resumePath, CancellationToken.None);
        var second = await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000100", state, resumePath, CancellationToken.None);

        Assert.False(Assert.Single(first).Success);
        Assert.Empty(second); // nothing re-attempted, so no new failure to report
        var entry = Assert.Single(state);
        Assert.Equal(ResumeStatus.Error, entry.Status);
        Assert.True(entry.HeldAfterFailure);
    }

    private ConversionOrchestrator OrchestratorWithRealStore(IEncoderRunner runner, LaneConfig lane, CompressarrConfig config, IResumeStateStore store, IArrUnmonitorService? arr = null) =>
        new(new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            runner, new FileRouter(), new NoOpCompanionFileService(), arr ?? new NoOpArrUnmonitorService(),
            new RecordingTrashService(), new NoOpRunLogger(), store, new NoOpProgressReporter(), new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue));

    [Fact]
    public async Task FailedEncode_HoldSurvivesAReloadFromDisk_SoTheNextPassDoesNotRetryIt()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(inputDir, "file1.mkv"), "source");
        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = outputDir, MoviePreset = "Any Preset" };
        var config = BuildConfig(DeleteAfterConvertMode.Maintain);
        config.Lanes.Add(lane);
        var store = new JsonResumeStateStore();
        var resumePath = Path.Combine(_tempDir, "resume.json");
        var orchestrator = OrchestratorWithRealStore(new FailingProcessRunner(), lane, config, store);

        var first = await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", new List<ResumeEntry>(), resumePath, CancellationToken.None);
        var onDisk = Assert.Single(store.Load(resumePath));
        var second = await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000100", store.Load(resumePath), resumePath, CancellationToken.None);   // as after a restart

        Assert.False(Assert.Single(first).Success);
        Assert.Equal(ResumeStatus.Error, onDisk.Status);
        Assert.True(onDisk.HeldAfterFailure, "the hold must be in the file, not only in memory");
        Assert.Empty(second);
    }

    [Fact]
    public async Task MaintainedSource_FingerprintIsSavedWithTheResult_SoAReloadDoesNotConvertItAgain()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);
        var source = Path.Combine(inputDir, "file1.mkv");
        File.WriteAllText(source, "source");
        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = outputDir, MoviePreset = "Any Preset" };
        var config = BuildConfig(DeleteAfterConvertMode.Maintain);
        config.Lanes.Add(lane);
        var store = new JsonResumeStateStore();
        var resumePath = Path.Combine(_tempDir, "resume.json");
        var orchestrator = OrchestratorWithRealStore(new FakeProcessRunner(), lane, config, store);

        var first = await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", new List<ResumeEntry>(), resumePath, CancellationToken.None);
        var onDisk = Assert.Single(store.Load(resumePath));      // read straight after the file's own save, before any end-of-pass repair
        var second = await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000100", store.Load(resumePath), resumePath, CancellationToken.None);

        Assert.True(Assert.Single(first).Success);
        Assert.Equal(ResumeStatus.Completed, onDisk.Status);
        Assert.Equal(new FileInfo(source).Length, onDisk.KeptSourceSize);
        Assert.NotNull(onDisk.KeptSourceModifiedUtc);
        Assert.Empty(second);                                    // the unchanged source is not converted again
    }

    [Fact]
    public async Task LanePreparation_KeepsAQueueEditMadeAfterItsRefresh_WhenItTracksANewFile()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);
        var a = Path.Combine(inputDir, "a.mkv");
        var b = Path.Combine(inputDir, "b.mkv");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");                                    // not tracked yet: the pass will add it
        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = outputDir, MoviePreset = "Any Preset" };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.Lanes.Add(lane);
        var resumePath = Path.Combine(_tempDir, "resume.json");
        new JsonResumeStateStore().Save(new List<ResumeEntry> { new() { LaneId = "lane1", FullName = a, Status = ResumeStatus.Pending, Order = 0 } }, resumePath);

        // The skip lands right after the pass's refresh read the file.
        var store = new EditAfterFirstLoadStore((inner, path) => inner.Update(path, state => { state.Single(e => e.FullName == a).Skipped = true; return true; }));
        var orchestrator = OrchestratorWithRealStore(new FakeProcessRunner(), lane, config, store);

        await orchestrator.PrepareLaneAsync(lane, config, new JsonResumeStateStore().Load(resumePath), resumePath);

        var after = new JsonResumeStateStore().Load(resumePath);
        Assert.True(after.Single(e => e.FullName == a).Skipped, "the skip committed after the refresh must survive the pass's save");
        Assert.Contains(after, e => e.FullName == b);                  // and the new file was tracked
    }

    [Fact]
    public async Task RetryRecovery_KeepsAQueueEditMadeWhileTheRetryWaitedOnTheArrRescan()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);
        var pending = Path.Combine(inputDir, "episode2.mkv");
        File.WriteAllText(pending, "2");
        var staged = Path.Combine(outputDir, "Movie A (1999).compressarr-abc12345.mkv");
        File.WriteAllText(staged, "encoded");
        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = outputDir, MoviePreset = "Any Preset" };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.Lanes.Add(lane);
        var store = new JsonResumeStateStore();
        var resumePath = Path.Combine(_tempDir, "resume.json");
        store.Save(new List<ResumeEntry>
        {
            new() { LaneId = "lane1", FullName = Path.Combine(inputDir, "Movie A (1999).mkv"), Status = ResumeStatus.MoveFailed, EncodedFilePath = staged, EncodedFileDesiredName = "Movie A (1999).mkv", Order = 0 },
            new() { LaneId = "lane1", FullName = pending, Status = ResumeStatus.Pending, Order = 1 }
        }, resumePath);

        // A skip made on the Monitor page while the retry is waiting on Sonarr/Radarr's rescan.
        var arr = new ConcurrentEditArrUnmonitorService(() => store.Update(resumePath, state =>
        {
            state.Single(e => e.FullName == pending).Skipped = true;
            return true;
        }));
        var orchestrator = OrchestratorWithRealStore(new FakeProcessRunner(), lane, config, store, arr);

        await orchestrator.PrepareLaneAsync(lane, config, store.Load(resumePath), resumePath);

        var after = store.Load(resumePath);
        Assert.True(after.Single(e => e.FullName == pending).Skipped, "the skip made during the retry must survive its save");
        Assert.Equal(ResumeStatus.Completed, after.Single(e => e.FullName.Contains("Movie A")).Status);
    }

    [Fact]
    public async Task ProcessLaneAsync_RespectsOrder_NotScanOrder()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        // Alphabetically "a-file" scans before "z-file" - Order should override that.
        var aPath = Path.Combine(inputDir, "a-file.mkv");
        var zPath = Path.Combine(inputDir, "z-file.mkv");
        File.WriteAllText(aPath, "a");
        File.WriteAllText(zPath, "z");

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = outputDir, MoviePreset = "Any Preset" };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new RecordingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry>
        {
            new() { LaneId = "lane1", FullName = zPath, Status = ResumeStatus.Pending, Order = 0 },
            new() { LaneId = "lane1", FullName = aPath, Status = ResumeStatus.Pending, Order = 1 }
        };

        var results = await RunLaneAsync(orchestrator,lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal("z-file.mkv", results[0].FileName);
        Assert.Equal("a-file.mkv", results[1].FileName);
    }

    [Fact]
    public async Task ProcessLaneAsync_SkippedEntry_IsExcludedButStaysTracked()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var keepPath = Path.Combine(inputDir, "process-me.mkv");
        var skipPath = Path.Combine(inputDir, "skip-me.mkv");
        File.WriteAllText(keepPath, "keep");
        File.WriteAllText(skipPath, "skip");

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = outputDir, MoviePreset = "Any Preset" };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new RecordingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry>
        {
            new() { LaneId = "lane1", FullName = keepPath, Status = ResumeStatus.Pending },
            new() { LaneId = "lane1", FullName = skipPath, Status = ResumeStatus.Pending, Skipped = true }
        };

        var results = await RunLaneAsync(orchestrator,lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("process-me.mkv", result.FileName);

        var skippedEntry = resumeState.Single(e => e.FullName == skipPath);
        Assert.Equal(ResumeStatus.Pending, skippedEntry.Status);
        Assert.True(skippedEntry.Skipped);
    }

    [Fact]
    public async Task ProcessLaneAsync_RemovedEntry_IsExcludedButStaysTracked()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var keepPath = Path.Combine(inputDir, "process-me.mkv");
        var removedPath = Path.Combine(inputDir, "removed-me.mkv");
        File.WriteAllText(keepPath, "keep");
        File.WriteAllText(removedPath, "removed");

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = outputDir, MoviePreset = "Any Preset" };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new RecordingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        // Removed implies Skipped, same as the "Remove from queue" endpoint sets both - a Removed
        // entry with Skipped left false would be a state the UI never actually produces, and isn't
        // what this test is verifying.
        var resumeState = new List<ResumeEntry>
        {
            new() { LaneId = "lane1", FullName = keepPath, Status = ResumeStatus.Pending },
            new() { LaneId = "lane1", FullName = removedPath, Status = ResumeStatus.Pending, Skipped = true, Removed = true }
        };

        var results = await RunLaneAsync(orchestrator,lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("process-me.mkv", result.FileName);

        var removedEntry = resumeState.Single(e => e.FullName == removedPath);
        Assert.Equal(ResumeStatus.Pending, removedEntry.Status);
        Assert.True(removedEntry.Removed);
    }

    // Code-review finding (v2.1.4 review, #4 MEDIUM): RestInPlace() - used whenever a converted
    // file rests directly in Output (MoveFiles off, or a routing-level collision Skip) - always
    // did File.Move(..., overwrite: true) unconditionally, regardless of OnDestinationCollision.
    // A real routed destination already respects Overwrite/Skip/Rename; a file resting in Output
    // did not, so two files that happened to land on the same desired name there could silently
    // clobber each other even with Rename configured. Fixed by routing RestInPlace through the
    // same FileRouter.ResolveCollision every other collision check already uses.
    [Fact]
    public async Task ProcessLaneAsync_RestingInOutput_RenameCollision_DoesNotOverwriteExistingFile()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourcePath = Path.Combine(inputDir, "process-me.mkv");
        File.WriteAllText(sourcePath, "source");

        // A file already sits at the exact name this conversion would otherwise land on.
        var preExistingPath = Path.Combine(outputDir, "process-me.mkv");
        File.WriteAllText(preExistingPath, "PRE-EXISTING CONTENT - must survive");

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = outputDir, MoviePreset = "Any Preset" };
        var config = new CompressarrConfig
        {
            // MoveFiles off - the file rests directly in Output, the exact scenario RestInPlace
            // used to leave unprotected regardless of this setting.
            Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false, OnDestinationCollision = DestinationCollisionMode.Rename }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new RecordingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var results = await RunLaneAsync(orchestrator,
            lane, config, _tempDir, "20260101_000000", new List<ResumeEntry>(), Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.True(result.Success);

        Assert.Equal("PRE-EXISTING CONTENT - must survive", File.ReadAllText(preExistingPath));

        var renamedPath = Path.Combine(outputDir, "process-me (2).mkv");
        Assert.True(File.Exists(renamedPath), "the new encode must land under a renamed path instead of silently overwriting the pre-existing file");
        Assert.Equal("fake encoded output", File.ReadAllText(renamedPath));
    }

    [Fact]
    public async Task ProcessLaneAsync_PresetOverride_WinsOverLaneDefault()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourcePath = Path.Combine(inputDir, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = outputDir, MoviePreset = "Lane Default Preset" };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new RecordingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry>
        {
            new() { LaneId = "lane1", FullName = sourcePath, Status = ResumeStatus.Pending, PresetOverride = "Custom Per-File Preset" }
        };

        var results = await RunLaneAsync(orchestrator,lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("Custom Per-File Preset", result.PresetName);
    }

    [Fact]
    public async Task ProcessLaneAsync_NoOverride_UsesLaneDefault()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourcePath = Path.Combine(inputDir, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = outputDir, MoviePreset = "Lane Default Preset" };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new RecordingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var results = await RunLaneAsync(orchestrator,
            lane, config, _tempDir, "20260101_000000", new List<ResumeEntry>(), Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("Lane Default Preset", result.PresetName);
    }

    [Fact]
    public async Task ProcessLaneAsync_StopTokenCancelledMidEncode_FinishesCurrentFile_NeverStartsNext()
    {
        // Confirmed live: "Stop Monitoring" was silently letting the ENTIRE remaining queue finish
        // (every file, potentially across every lane) before honoring the stop at all - the loop's
        // own cancellation token was never actually passed into RunOnceAsync/ProcessLaneAsync. The
        // fix threads it through as a distinct stopToken, checked only between files (never handed
        // to the process runner, unlike the hard-kill Abort token) - this proves the file already
        // encoding when Stop is pressed finishes completely, and the next one never starts.
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var firstPath = Path.Combine(inputDir, "a-first.mkv");
        var secondPath = Path.Combine(inputDir, "b-second.mkv");
        File.WriteAllText(firstPath, "first");
        File.WriteAllText(secondPath, "second");

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = outputDir, MoviePreset = "Any Preset" };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var stopCts = new CancellationTokenSource();
        // Fires while the FIRST file's encode is still in flight - simulates the user clicking
        // Stop Monitoring mid-encode.
        var processRunner = new ConcurrentEditProcessRunner(() => stopCts.Cancel());

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            processRunner, new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new RecordingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry>
        {
            new() { LaneId = "lane1", FullName = firstPath, Status = ResumeStatus.Pending },
            new() { LaneId = "lane1", FullName = secondPath, Status = ResumeStatus.Pending }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RunLaneAsync(orchestrator,lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None, stopCts.Token));

        var firstEntry = resumeState.Single(e => e.FullName == firstPath);
        var secondEntry = resumeState.Single(e => e.FullName == secondPath);
        Assert.Equal(ResumeStatus.Completed, firstEntry.Status);
        Assert.Equal(ResumeStatus.Pending, secondEntry.Status);
    }

    [Fact]
    public async Task ProcessLaneAsync_ConcurrentQueueEditDuringEncode_IsHonoredNotLost()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var path1 = Path.Combine(inputDir, "episode1.mkv");
        var path2 = Path.Combine(inputDir, "episode2.mkv");
        var path3 = Path.Combine(inputDir, "episode3.mkv");
        File.WriteAllText(path1, "1");
        File.WriteAllText(path2, "2");
        File.WriteAllText(path3, "3");

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = outputDir, MoviePreset = "Any Preset" };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        // A REAL file-backed store, not NoOp - this test needs Load/Update to genuinely round-trip
        // through disk, the same channel a real web request and a real in-flight pass actually
        // share (there's no way to reproduce a lost update against an in-memory-only fake).
        var resumeFilePath = Path.Combine(_tempDir, "resume.json");
        var realResumeStore = new JsonResumeStateStore();

        var resumeState = new List<ResumeEntry>
        {
            new() { LaneId = "lane1", FullName = path1, Status = ResumeStatus.Pending, Order = 0 },
            new() { LaneId = "lane1", FullName = path2, Status = ResumeStatus.Pending, Order = 1 },
            new() { LaneId = "lane1", FullName = path3, Status = ResumeStatus.Pending, Order = 2 }
        };
        realResumeStore.Save(resumeState, resumeFilePath);

        // Simulates POST /api/run/queue/reorder landing while episode1 is still "encoding" -
        // moves episode3 to the front of what's left, exactly the reported live scenario (user
        // drags a file to the top of the queue mid-pass).
        var processRunner = new ConcurrentEditProcessRunner(() =>
        {
            realResumeStore.Update(resumeFilePath, state =>
            {
                state.Single(e => e.FullName == path3).Order = 0;
                state.Single(e => e.FullName == path2).Order = 1;
                return true;
            });
        });

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            processRunner, new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new RecordingTrashService(), new NoOpRunLogger(), realResumeStore, new NoOpProgressReporter(), configStore);

        var results = await RunLaneAsync(orchestrator,lane, config, _tempDir, "20260101_000000", resumeState, resumeFilePath, CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.Equal("episode1.mkv", results[0].FileName); // already in flight when the edit landed
        Assert.Equal("episode3.mkv", results[1].FileName); // the concurrent reorder's effect
        Assert.Equal("episode2.mkv", results[2].FileName);

        // The concurrent edit must have survived ConversionOrchestrator's own subsequent saves,
        // not been silently overwritten by them.
        var finalState = realResumeStore.Load(resumeFilePath);
        Assert.All(finalState, e => Assert.Equal(ResumeStatus.Completed, e.Status));
    }

    [Fact]
    public async Task ProcessLaneAsync_ConcurrentQueueEditAfterRefresh_DuringPostProcessing_IsHonoredNotLost()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var path1 = Path.Combine(inputDir, "episode1.mkv");
        var path2 = Path.Combine(inputDir, "episode2.mkv");
        File.WriteAllText(path1, "1");
        File.WriteAllText(path2, "2");

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = outputDir, MoviePreset = "Any Preset" };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var resumeFilePath = Path.Combine(_tempDir, "resume.json");
        var realResumeStore = new JsonResumeStateStore();

        var resumeState = new List<ResumeEntry>
        {
            new() { LaneId = "lane1", FullName = path1, Status = ResumeStatus.Pending, Order = 0 },
            new() { LaneId = "lane1", FullName = path2, Status = ResumeStatus.Pending, Order = 1 }
        };
        realResumeStore.Save(resumeState, resumeFilePath);

        // Simulates a concurrent web edit landing during episode1's *arr rescan step - AFTER
        // ConversionOrchestrator's one-time RefreshResumeState already ran for episode1, exactly
        // the gap the code review found: routing/companion-move/arr-rescan all still run between
        // that one refresh and the final save, so an edit to a DIFFERENT file landing anywhere in
        // that window was previously silently lost.
        var arrUnmonitor = new ConcurrentEditArrUnmonitorService(() =>
        {
            realResumeStore.Update(resumeFilePath, state =>
            {
                state.Single(e => e.FullName == path2).Skipped = true;
                return true;
            });
        });

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), arrUnmonitor,
            new RecordingTrashService(), new NoOpRunLogger(), realResumeStore, new NoOpProgressReporter(), configStore);

        var results = await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", resumeState, resumeFilePath, CancellationToken.None);

        // episode2's Skipped=true (set while episode1 was still processing) must have survived
        // episode1's own final save - previously it was silently wiped, so episode2 would have
        // been incorrectly picked and processed too.
        Assert.Single(results);
        Assert.Equal("episode1.mkv", results[0].FileName);

        var finalState = realResumeStore.Load(resumeFilePath);
        Assert.True(finalState.Single(e => e.FullName == path2).Skipped);
        Assert.Equal(ResumeStatus.Pending, finalState.Single(e => e.FullName == path2).Status);
    }

    [Fact]
    public async Task ProcessLaneAsync_MoveDestinationUnreachable_ReportsErrorButKeepsTheFile()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourcePath = Path.Combine(inputDir, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            // Simulates an offline/unreachable network drive.
            MovieBasePath = @"Z:\Unavailable\Movies"
        };

        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Maintain, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(),
            new RealFolderScanner(),
            new NoOpFileBotRunner(),
            new FixedExtensionPresetService(),
            new MetadataService(),
            new FakeProcessRunner(),
            new FileRouter(),
            new NoOpCompanionFileService(),
            new NoOpArrUnmonitorService(),
            new RecordingTrashService(),
            new NoOpRunLogger(),
            new NoOpResumeStateStore(),
            new NoOpProgressReporter(),
            configStore);

        var results = await RunLaneAsync(orchestrator,
            lane, config, _tempDir, "20260101_000000", new List<ResumeEntry>(), Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var result = Assert.Single(results);
        // The encode itself succeeded - only the move failed - but this must be reported as an
        // error, not silently as "OK", or a reader of the report/History would never know the
        // file didn't end up where it was supposed to.
        Assert.False(result.Success);
        // The report shows this specific reason in place of a bare "ERROR" - not disk-full, since
        // the unreachable path here is a nonexistent drive letter, not a full one.
        Assert.False(result.DiskFull);
        Assert.Equal("Base folder path unavailable, move skipped", result.FailureReason);
        // The file is not lost - it's exactly where HandBrake wrote it, in the lane's Output
        // folder, under its own collision-safe staging name (finding #4's fix) rather than the
        // deterministic "Caddyshack (1980).mkv" - routing never got far enough to know it could
        // safely claim that name.
        Assert.NotNull(result.NewFileName);
        Assert.StartsWith(outputDir, result.NewFileName);
        Assert.True(File.Exists(result.NewFileName));
        Assert.Contains(".compressarr-", result.NewFileName);
    }

    [Fact]
    public async Task ProcessLaneAsync_MoveFails_MarksResumeEntryMoveFailed_NotCompleted()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourcePath = Path.Combine(inputDir, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = @"Z:\Unavailable\Movies"
        };

        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Maintain, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new RecordingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry> { new() { LaneId = "lane1", FullName = sourcePath, Status = ResumeStatus.Pending } };

        await RunLaneAsync(orchestrator,lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var entry = Assert.Single(resumeState);
        // Previously this incorrectly ended up Completed even though the file never got filed -
        // this pins the fix: a real move failure must leave the entry retryable, not lying that
        // the file is fully done.
        Assert.Equal(ResumeStatus.MoveFailed, entry.Status);
        // The encoded file is kept under its own collision-safe staging name (finding #4's fix),
        // not the deterministic "Caddyshack (1980).mkv" - and the clean name is preserved
        // separately so a later successful retry can still classify/route it correctly.
        Assert.NotNull(entry.EncodedFilePath);
        Assert.StartsWith(outputDir, entry.EncodedFilePath);
        Assert.Contains(".compressarr-", entry.EncodedFilePath);
        Assert.True(File.Exists(entry.EncodedFilePath));
        Assert.Equal("Caddyshack (1980).mkv", entry.EncodedFileDesiredName);
    }

    [Fact]
    public async Task ProcessLaneAsync_MoveFails_DoesNotDeleteSourceOrUnmonitorArr()
    {
        // Pins the fix for the two release-level bugs the v2.1.3 code review flagged: the original
        // source used to be deleted (and Sonarr/Radarr told to stop monitoring) unconditionally,
        // BEFORE routing even ran - so a routing failure (destination offline, etc.) still lost
        // the original and stranded the file while *arr had already stopped tracking it. Both must
        // now stay untouched until the video has genuinely reached its final resting place.
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourcePath = Path.Combine(inputDir, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = @"Z:\Unavailable\Movies" // unreachable - routing will fail
        };

        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Delete, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);
        var arrUnmonitor = new RecordingArrUnmonitorService();

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), arrUnmonitor,
            new DeletingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry> { new() { LaneId = "lane1", FullName = sourcePath, Status = ResumeStatus.Pending } };

        await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        Assert.Equal(ResumeStatus.MoveFailed, Assert.Single(resumeState).Status);
        Assert.True(File.Exists(sourcePath));
        Assert.Equal(0, arrUnmonitor.CallCount);
    }

    [Fact]
    public async Task ProcessLaneAsync_MoveSucceeds_DeletesSourceAndUnmonitorsArr()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        var movieBaseDir = Path.Combine(_tempDir, "Movies");
        Directory.CreateDirectory(movieBaseDir);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourcePath = Path.Combine(inputDir, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = movieBaseDir
        };

        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Delete, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);
        var arrUnmonitor = new RecordingArrUnmonitorService();

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), arrUnmonitor,
            new DeletingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry> { new() { LaneId = "lane1", FullName = sourcePath, Status = ResumeStatus.Pending } };

        await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        Assert.Equal(ResumeStatus.Completed, Assert.Single(resumeState).Status);
        Assert.False(File.Exists(sourcePath));
        Assert.Equal(1, arrUnmonitor.CallCount);
    }

    [Fact]
    public async Task ProcessLaneAsync_MoveFailedEntry_RetriedAndSucceedsOnNextPass()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        var movieBaseDir = Path.Combine(_tempDir, "Movies");
        Directory.CreateDirectory(movieBaseDir);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourcePath = Path.Combine(inputDir, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = @"Z:\Unavailable\Movies" // first pass: unreachable
        };

        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Delete, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new DeletingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry>();
        await RunLaneAsync(orchestrator,lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var entryAfterFirstPass = Assert.Single(resumeState);
        Assert.Equal(ResumeStatus.MoveFailed, entryAfterFirstPass.Status);
        // Source cleanup only happens once the file has genuinely reached its final resting
        // place - a real move failure must leave the original alone, not delete it out from
        // under a routing failure it couldn't have predicted.
        Assert.True(File.Exists(sourcePath));

        // Second pass: the base path is reachable now - only the move should be retried, no
        // re-encode (nothing left in Input to encode anyway).
        lane.MovieBasePath = movieBaseDir;
        var secondPassResults = await RunLaneAsync(orchestrator,lane, config, _tempDir, "20260101_000100", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        Assert.Empty(secondPassResults); // the retry itself doesn't add a new ConversionResult
        var entryAfterRetry = Assert.Single(resumeState);
        Assert.Equal(ResumeStatus.Completed, entryAfterRetry.Status);
        Assert.Null(entryAfterRetry.EncodedFilePath);

        var finalPath = Path.Combine(movieBaseDir, "Caddyshack (1980)", "Caddyshack (1980).mkv");
        Assert.True(File.Exists(finalPath));
        // Only now that the retry actually succeeded does the original get cleaned up.
        Assert.False(File.Exists(sourcePath));
    }

    private LaneConfig EditionLane(string tvBase) => new()
    {
        Id = "lane1", DisplayName = "Test Lane", Enabled = true,
        Input = Path.Combine(_tempDir, "Input"), Output = Path.Combine(_tempDir, "Output"),
        TvPreset = "Any Preset", TvShowBasePath = tvBase
    };

    private ConversionOrchestrator EditionOrchestrator(CompressarrConfig config) => new(
        new PassThroughPathExpander(), new RecursiveFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
        new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
        new DeletingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(),
        new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue));

    private string EditionSource()
    {
        var source = Path.Combine(_tempDir, "Input", "Babylon 5 (1994) {edition-Bluray Remaster}", "Season 01", "Babylon 5 (1994) - S01E01 - Midnight.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(Path.Combine(_tempDir, "Output"));
        File.WriteAllText(source, "source");
        return source;
    }

    [Fact]
    public async Task ProcessLaneAsync_ATvSourceShowFolderWithAPlexEdition_FilesTheEpisodeInTheEditionsShowFolder()
    {
        // Input\Babylon 5 (1994) {edition-Bluray Remaster}\Season 01\<episode> must come out as <TV>\Babylon 5 (1994) {edition-Bluray Remaster}\Season 01\<episode>:
        // the edition tag on the show folder is how Plex keeps two versions of a show apart.
        var tvBase = Path.Combine(_tempDir, "TV");
        Directory.CreateDirectory(tvBase);
        var source = EditionSource();
        var lane = EditionLane(tvBase);
        var config = new CompressarrConfig { Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Delete, MoveFiles = true, ClearTitleMetadata = false } };
        config.Lanes.Add(lane);

        await RunLaneAsync(EditionOrchestrator(config), lane, config, _tempDir, "20260101_000000", new List<ResumeEntry>(), Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(tvBase, "Babylon 5 (1994) {edition-Bluray Remaster}", "Season 01", "Babylon 5 (1994) - S01E01 - Midnight.mkv")));
        Assert.False(Directory.Exists(Path.Combine(tvBase, "Babylon 5 (1994)")));      // and no plain-show folder was made
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task ProcessLaneAsync_ARetriedMove_StillFilesTheEpisodeInTheEditionsShowFolder()
    {
        // First pass: the TV library is unreachable, so the move fails. Second pass: it is back and only the move is retried - from the recorded source path.
        var tvBase = Path.Combine(_tempDir, "TV");
        var source = EditionSource();
        var lane = EditionLane(@"Z:\Unavailable\TV");
        var config = new CompressarrConfig { Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Delete, MoveFiles = true, ClearTitleMetadata = false } };
        config.Lanes.Add(lane);
        var orchestrator = EditionOrchestrator(config);
        var resumeState = new List<ResumeEntry>();

        await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);
        Assert.Equal(ResumeStatus.MoveFailed, Assert.Single(resumeState).Status);
        Assert.True(File.Exists(source));

        Directory.CreateDirectory(tvBase);
        lane.TvShowBasePath = tvBase;
        await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000100", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        Assert.Equal(ResumeStatus.Completed, Assert.Single(resumeState).Status);
        Assert.True(File.Exists(Path.Combine(tvBase, "Babylon 5 (1994) {edition-Bluray Remaster}", "Season 01", "Babylon 5 (1994) - S01E01 - Midnight.mkv")));
        Assert.False(Directory.Exists(Path.Combine(tvBase, "Babylon 5 (1994)")));
    }

    [Fact]
    public async Task ProcessLaneAsync_MoveFailedEntry_RetriedAndSucceeds_RemovesOrphanedSourceFolder()
    {
        // Real bug found live: the retry path passed entry.EncodedFilePath (the already-converted
        // file sitting in the processing/staging folder) to MoveCompanionFiles as if it were the
        // original source file, so companion-cleanup/empty-folder-cascade-delete ran against the
        // wrong directory entirely and the true original source folder was left behind forever,
        // even after a successful retried move. Needs the REAL CompanionFileService (not the
        // NoOp fake the other MoveFailed tests use) to actually exercise that cleanup logic.
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        var movieBaseDir = Path.Combine(_tempDir, "Movies");
        Directory.CreateDirectory(movieBaseDir);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        // Nested one level deep, like a real "one folder per movie" library layout - the orphan
        // the user hit live was exactly this shape (Reacher\Season 04\<file>.mkv).
        var sourceSubfolder = Path.Combine(inputDir, "Caddyshack (1980)");
        Directory.CreateDirectory(sourceSubfolder);
        var sourcePath = Path.Combine(sourceSubfolder, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = @"Z:\Unavailable\Movies" // first pass: unreachable
        };

        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Delete, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RecursiveFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new CompanionFileService(new DeletingTrashService()), new NoOpArrUnmonitorService(),
            new DeletingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry>();
        await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        Assert.Equal(ResumeStatus.MoveFailed, Assert.Single(resumeState).Status);
        // A real move failure must leave the original alone - the source (and its folder) stay
        // untouched until routing genuinely succeeds.
        Assert.True(File.Exists(sourcePath));
        Assert.True(Directory.Exists(sourceSubfolder));

        // Second pass: the base path is reachable now - only the move should be retried.
        lane.MovieBasePath = movieBaseDir;
        await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000100", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        Assert.Equal(ResumeStatus.Completed, Assert.Single(resumeState).Status);
        Assert.True(File.Exists(Path.Combine(movieBaseDir, "Caddyshack (1980)", "Caddyshack (1980).mkv")));
        // Only now that the retry actually succeeded does the source (and its now-empty folder)
        // get cleaned up.
        Assert.False(File.Exists(sourcePath));
        Assert.False(Directory.Exists(sourceSubfolder));
    }

    [Fact]
    public async Task ProcessLaneAsync_MoveFailedEntry_StillFailing_StaysMoveFailed()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourcePath = Path.Combine(inputDir, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = @"Z:\Unavailable\Movies"
        };

        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Delete, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new DeletingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry>();
        await RunLaneAsync(orchestrator,lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);
        var encodedPath = Assert.Single(resumeState).EncodedFilePath!;

        // Second pass: base path is still unreachable.
        await RunLaneAsync(orchestrator,lane, config, _tempDir, "20260101_000100", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var entry = Assert.Single(resumeState);
        Assert.Equal(ResumeStatus.MoveFailed, entry.Status);
        Assert.Equal(encodedPath, entry.EncodedFilePath);
        Assert.True(File.Exists(encodedPath)); // still sitting right where it was
    }

    [Fact]
    public async Task ProcessLaneAsync_TwoAttemptsShareTheSameDeterministicName_NeitherStagedFileClobbersTheOther()
    {
        // The core scenario the v2.1.3 code review's finding #4 flagged: two different source
        // files that both resolve to the same deterministic Output filename ("Caddyshack
        // (1980).mkv" here, from two different folders) both fail to route (shared unreachable
        // destination) and are left staged in Output at the same time. Before the fix, the SECOND
        // one's own File.Move(tempFileName, newFileName, overwrite: true) would have silently
        // destroyed the FIRST one's already-successful encode the moment both landed in Output -
        // this proves that can no longer happen: each gets its own collision-safe staging name.
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(Path.Combine(inputDir, "Extras"));
        Directory.CreateDirectory(outputDir);

        var firstSource = Path.Combine(inputDir, "Caddyshack (1980).mkv");
        var secondSource = Path.Combine(inputDir, "Extras", "Caddyshack (1980).mkv");
        File.WriteAllText(firstSource, "first source");
        File.WriteAllText(secondSource, "second source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = @"Z:\Unavailable\Movies" // unreachable - both routes will fail
        };

        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Maintain, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RecursiveFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new RecordingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry>();
        var results = await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal(2, resumeState.Count);
        Assert.All(resumeState, e => Assert.Equal(ResumeStatus.MoveFailed, e.Status));

        var stagedPaths = resumeState.Select(e => e.EncodedFilePath!).ToList();
        Assert.Equal(2, stagedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count()); // never collided on the same path
        Assert.All(stagedPaths, p => Assert.Contains(".compressarr-", p));
        Assert.All(stagedPaths, p => Assert.True(File.Exists(p))); // both survive simultaneously
        Assert.All(resumeState, e => Assert.Equal("Caddyshack (1980).mkv", e.EncodedFileDesiredName));
    }

    [Fact]
    public async Task PrepareLaneAsync_MoveFailedEntryStillFailing_DoesNotReEncodeOnRescan()
    {
        // Real regression this fix's own combination exposed: once a real move failure stopped
        // deleting the original source (finding #1), that source is legitimately still sitting in
        // Input on the next pass's scan - and PrepareLaneAsync's own scan-reconciliation step used
        // to unconditionally flip ANY existing resume entry it matched by path back to Pending,
        // including a MoveFailed one that's already being handled by the retry loop (move retry
        // only). That silently discarded the already-successful encode and re-encoded the same
        // file from scratch on every single poll for as long as the destination stayed
        // unreachable - invisible under the OLD deterministic staging name (a re-encode and a
        // real retry produced the exact same path either way), but a real, wasteful correctness
        // bug once staging moved to a per-attempt name (finding #4).
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourcePath = Path.Combine(inputDir, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = @"Z:\Unavailable\Movies"
        };

        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Delete, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);
        var processRunner = new FakeProcessRunner();

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            processRunner, new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new DeletingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry>();
        var firstPassResults = await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);
        Assert.Single(firstPassResults);
        Assert.Equal(1, processRunner.CallCount);
        Assert.Equal(ResumeStatus.MoveFailed, Assert.Single(resumeState).Status);
        Assert.True(File.Exists(sourcePath)); // finding #1's fix - not deleted on a real move failure

        // Second pass: base path is still unreachable, and the source scan will legitimately
        // rediscover sourcePath (it was never deleted).
        var secondPassResults = await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000100", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        Assert.Empty(secondPassResults); // a move retry, not a fresh conversion - no new ConversionResult
        Assert.Equal(1, processRunner.CallCount); // HandBrake must NOT have run again
        Assert.Equal(ResumeStatus.MoveFailed, Assert.Single(resumeState).Status);
    }

    [Fact]
    public async Task ProcessLaneAsync_MoveFailedEntry_RepeatedIdenticalFailure_LogsInfoNotErrorAfterTheFirstTime()
    {
        // Real gap found live: an offline destination (a network share down overnight) got
        // retried every single poll, and every one of those retries logged at Error severity -
        // which, combined with the "don't keep an empty/error-free pass's log" fix, meant a KEPT
        // log file every poll for the whole outage - the same file-proliferation problem the fix
        // was meant to solve, just gated on "error" instead of "empty." Only the first occurrence
        // of a given failure (and any later occurrence whose message actually changes) should log
        // at Error; an unchanged repeat should log at Info so it doesn't force the file to survive.
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourcePath = Path.Combine(inputDir, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = @"Z:\Unavailable\Movies" // stays unreachable for every pass below
        };

        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Delete, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var logger = new RecordingRunLogger();
        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new DeletingTrashService(), logger, new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry>();
        // Pass 1: real encode + first move failure.
        await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);
        // Pass 2 and 3: pure retries against the exact same unreachable path - same failure both times.
        await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000100", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);
        await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000200", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var retryLogs = logger.Logs.Where(l => l.Message.Contains("Retried move for")).ToList();
        Assert.Equal(2, retryLogs.Count); // one retry attempt per pass 2 and 3 (pass 1 was the original move, not a retry)
        Assert.Equal(LogSeverity.Error, retryLogs[0].Severity); // first retry failure - genuinely new information
        Assert.Equal(LogSeverity.Info, retryLogs[1].Severity); // second retry, identical failure - already known
    }

    [Fact]
    public async Task ProcessLaneAsync_MoveFailedEntry_EncodedFileGone_EntryRemoved()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "Movies"));

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = Path.Combine(_tempDir, "Movies")
        };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = true } };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new DeletingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        // Simulates a MoveFailed entry whose encoded file was since deleted/moved by hand.
        var resumeState = new List<ResumeEntry>
        {
            new()
            {
                LaneId = "lane1",
                FullName = Path.Combine(inputDir, "gone-already.mkv"),
                Status = ResumeStatus.MoveFailed,
                EncodedFilePath = Path.Combine(outputDir, "gone-already.mkv") // never actually created
            }
        };

        await RunLaneAsync(orchestrator,lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        Assert.Empty(resumeState);
    }

    [Fact]
    public async Task ProcessLaneAsync_DestinationCollisionModeSkip_SucceedsWithWarning_NotError()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        var movieBaseDir = Path.Combine(_tempDir, "Movies");
        Directory.CreateDirectory(movieBaseDir);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourcePath = Path.Combine(inputDir, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        // Pre-existing file already sitting at the destination this conversion would land at.
        var existingDestFolder = Path.Combine(movieBaseDir, "Caddyshack (1980)");
        Directory.CreateDirectory(existingDestFolder);
        File.WriteAllText(Path.Combine(existingDestFolder, "Caddyshack (1980).mkv"), "already here");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = movieBaseDir
        };

        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Maintain, MoveFiles = true, ClearTitleMetadata = false, OnDestinationCollision = DestinationCollisionMode.Skip }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new RecordingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry> { new() { LaneId = "lane1", FullName = sourcePath, Status = ResumeStatus.Pending } };

        var results = await RunLaneAsync(orchestrator,lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var result = Assert.Single(results);
        // A deliberate, configured skip is not a failure - the encode itself worked fine.
        Assert.True(result.Success);
        Assert.False(result.DiskFull);
        Assert.Contains("already exists", result.PostProcessWarning);
        // The just-converted file stays in Output, untouched - the pre-existing destination file
        // is also untouched.
        Assert.True(File.Exists(Path.Combine(outputDir, "Caddyshack (1980).mkv")));
        Assert.Equal("already here", File.ReadAllText(Path.Combine(existingDestFolder, "Caddyshack (1980).mkv")));

        var entry = Assert.Single(resumeState);
        Assert.Equal(ResumeStatus.Completed, entry.Status);
    }

    [Fact]
    public async Task ProcessLaneAsync_EncodeFailsFromDiskFull_ResultFlagsDiskFull()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourcePath = Path.Combine(inputDir, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset"
        };

        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Maintain, MoveFiles = false, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(),
            new RealFolderScanner(),
            new NoOpFileBotRunner(),
            new FixedExtensionPresetService(),
            new MetadataService(),
            new DiskFullProcessRunner(),
            new FileRouter(),
            new NoOpCompanionFileService(),
            new NoOpArrUnmonitorService(),
            new RecordingTrashService(),
            new NoOpRunLogger(),
            new NoOpResumeStateStore(),
            new NoOpProgressReporter(),
            configStore);

        var results = await RunLaneAsync(orchestrator,
            lane, config, _tempDir, "20260101_000000", new List<ResumeEntry>(), Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.False(result.Success);
        Assert.True(result.DiskFull);
        Assert.Equal("Output drive full, monitoring stopped", result.FailureReason);
        // A failed encode's truncated temp file is cleaned up - it never becomes the "output".
        Assert.False(File.Exists(Path.Combine(outputDir, "Caddyshack (1980).mkv")));
        // The real source is never touched by a failed encode either way.
        Assert.True(File.Exists(sourcePath));
    }

    [Fact]
    public async Task ProcessLaneAsync_PendingResumeFileMissing_RemovedAndFallsBackToScan()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        // Only a brand-new file sits in Input - the previously pending file was removed by hand
        // (e.g. between test runs) before monitoring started back up.
        var newFilePath = Path.Combine(inputDir, "New Movie (2024).mkv");
        File.WriteAllText(newFilePath, "source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset"
        };

        var config = BuildConfig(DeleteAfterConvertMode.Maintain);
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var resumeState = new List<ResumeEntry>
        {
            new() { LaneId = "lane1", FullName = Path.Combine(inputDir, "Deleted Movie (1999).mkv"), Status = ResumeStatus.Pending }
        };

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(),
            new RealFolderScanner(),
            new NoOpFileBotRunner(),
            new FixedExtensionPresetService(),
            new MetadataService(),
            new FakeProcessRunner(),
            new FileRouter(),
            new NoOpCompanionFileService(),
            new NoOpArrUnmonitorService(),
            new RecordingTrashService(),
            new NoOpRunLogger(),
            new NoOpResumeStateStore(),
            new NoOpProgressReporter(),
            configStore);

        var results = await RunLaneAsync(orchestrator,
            lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        // The dead pending entry pointing at the removed file is gone from resume state...
        Assert.DoesNotContain(resumeState, e => e.FullName.Contains("Deleted Movie"));
        // ...and the lane fell back to scanning Input instead of silently processing nothing,
        // picking up the genuinely new file that was actually sitting there.
        var result = Assert.Single(results);
        Assert.Equal("New Movie (2024).mkv", result.FileName);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task ProcessLaneAsync_FileReappearsAfterCompleting_ReusesResumeEntryInsteadOfDuplicating()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var filePath = Path.Combine(inputDir, "Private School (1983).mkv");
        File.WriteAllText(filePath, "source, re-added after already completing once");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset"
        };

        var config = BuildConfig(DeleteAfterConvertMode.Maintain);
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        // Simulates the exact state pulled from a real resume.json: this file already completed
        // in an earlier run, but the same path has since reappeared in Input.
        var resumeState = new List<ResumeEntry>
        {
            new() { LaneId = "lane1", FullName = filePath, Status = ResumeStatus.Completed }
        };

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(),
            new RealFolderScanner(),
            new NoOpFileBotRunner(),
            new FixedExtensionPresetService(),
            new MetadataService(),
            new FakeProcessRunner(),
            new FileRouter(),
            new NoOpCompanionFileService(),
            new NoOpArrUnmonitorService(),
            new RecordingTrashService(),
            new NoOpRunLogger(),
            new NoOpResumeStateStore(),
            new NoOpProgressReporter(),
            configStore);

        var results = await RunLaneAsync(orchestrator,
            lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        Assert.Single(results);
        // Exactly one resume entry for this path, not two - the pre-existing Completed entry was
        // reused (and is Completed again, since this run finished it) rather than left in place
        // alongside a second, newly-added entry.
        var entry = Assert.Single(resumeState, e => e.FullName == filePath);
        Assert.Equal(ResumeStatus.Completed, entry.Status);
    }

    [Fact]
    public async Task ProcessLaneAsync_OnlyErrorAndCompletedEntriesTracked_StillScansForNewFiles()
    {
        // Mirrors a real resume.json pulled from production: a pile of Completed entries plus one
        // Error entry (a file that failed to encode), and zero Pending entries. The user reported
        // that with a resume file shaped like this, a genuinely new file dropped into Input never
        // got picked up.
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var newFilePath = Path.Combine(inputDir, "Brand New Movie (2024).mkv");
        File.WriteAllText(newFilePath, "source");

        var lane = new LaneConfig
        {
            Id = "hdsd",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset"
        };

        var config = BuildConfig(DeleteAfterConvertMode.Maintain);
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var resumeState = new List<ResumeEntry>
        {
            new() { LaneId = "hdsd", FullName = @"D:\Media Download\Young Frankenstein (1974)\Young Frankenstein (1974).mkv", Status = ResumeStatus.Completed },
            new() { LaneId = "hdsd", FullName = @"D:\Media Download\Rocky V (1990)\Rocky V (1990).mkv", Status = ResumeStatus.Error },
            new() { LaneId = "hdsd", FullName = @"D:\Media Download\Working Girl (1988)\Working Girl (1988).mkv", Status = ResumeStatus.Completed }
        };

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(),
            new RealFolderScanner(),
            new NoOpFileBotRunner(),
            new FixedExtensionPresetService(),
            new MetadataService(),
            new FakeProcessRunner(),
            new FileRouter(),
            new NoOpCompanionFileService(),
            new NoOpArrUnmonitorService(),
            new RecordingTrashService(),
            new NoOpRunLogger(),
            new NoOpResumeStateStore(),
            new NoOpProgressReporter(),
            configStore);

        var results = await RunLaneAsync(orchestrator,
            lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal("Brand New Movie (2024).mkv", result.FileName);
        Assert.True(result.Success);
    }

    [Fact]
    public async Task ProcessLaneAsync_ErrorEntryWithMissingSourceFile_IsRemoved()
    {
        // A file that failed to encode (Error) and whose source later disappeared (deleted by
        // hand, or already handled by Sonarr/Radarr) can never be retried - unlike a dead Pending
        // entry, nothing else in this codebase ever re-checks or clears a dead Error entry.
        // Without this, it sits in resume.json forever and permanently blocks
        // RunOrchestrator's "stillOutstanding" cleanup for every other entry alongside it (even
        // ones that are fully Completed) - reproduced live: a resume.json with 14 Completed
        // entries plus one dead Error entry kept logging "Resuming previous incomplete run (15
        // file(s) tracked)" on every single pass, forever.
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var lane = new LaneConfig
        {
            Id = "hdsd",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset"
        };

        var config = BuildConfig(DeleteAfterConvertMode.Maintain);
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var deadFilePath = Path.Combine(_tempDir, "Gone Now (2020).mkv"); // never created on disk
        var resumeState = new List<ResumeEntry>
        {
            new() { LaneId = "hdsd", FullName = deadFilePath, Status = ResumeStatus.Error }
        };

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(),
            new RealFolderScanner(),
            new NoOpFileBotRunner(),
            new FixedExtensionPresetService(),
            new MetadataService(),
            new FakeProcessRunner(),
            new FileRouter(),
            new NoOpCompanionFileService(),
            new NoOpArrUnmonitorService(),
            new RecordingTrashService(),
            new NoOpRunLogger(),
            new NoOpResumeStateStore(),
            new NoOpProgressReporter(),
            configStore);

        await RunLaneAsync(orchestrator,
            lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        Assert.Empty(resumeState);
    }

    [Fact]
    public async Task ProcessLaneAsync_CompanionFileMoveFails_SetsWarningButStaysSuccessful()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "Movies"));

        var sourcePath = Path.Combine(inputDir, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = Path.Combine(_tempDir, "Movies")
        };

        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Maintain, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(),
            new RealFolderScanner(),
            new NoOpFileBotRunner(),
            new FixedExtensionPresetService(),
            new MetadataService(),
            new FakeProcessRunner(),
            new FileRouter(),
            new ThrowingCompanionFileService(),
            new NoOpArrUnmonitorService(),
            new RecordingTrashService(),
            new NoOpRunLogger(),
            new NoOpResumeStateStore(),
            new NoOpProgressReporter(),
            configStore);

        var results = await RunLaneAsync(orchestrator,
            lane, config, _tempDir, "20260101_000000", new List<ResumeEntry>(), Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var result = Assert.Single(results);
        // The video itself converted and was filed correctly - only a secondary step (moving
        // subtitles/nfo/artwork) failed, so this must not be reported as a failed conversion.
        Assert.True(result.Success);
        Assert.Contains("Companion files not moved", result.PostProcessWarning);
    }

    // Code-review finding (v2.1.4 review, #2 MED-HIGH): a companion move exception was logged and
    // warned about, but nothing stopped the source-folder cleanup that runs right after - if the
    // video itself was already routed and its own source already deleted, the folder could look
    // "empty of video" even though the failed-to-move companion is still sitting in it, and that
    // companion could then be swept up and deleted/recycled as an "unmatched leftover" by the very
    // next step. Fixed via a companionMoveFailed flag that suppresses CleanUpEmptySourceFolder for
    // that pass.
    [Fact]
    public async Task ProcessLaneAsync_CompanionFileMoveFails_DoesNotRunSourceFolderCleanup()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "Movies"));

        var sourcePath = Path.Combine(inputDir, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = Path.Combine(_tempDir, "Movies")
        };

        // Delete (not Maintain, unlike the sibling test above) - the actual at-risk scenario: the
        // video's own source gets deleted as normal, the folder now LOOKS empty of video, and the
        // only thing standing between "genuinely empty" and "a stranded companion still in there"
        // is this fix.
        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Delete, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var companionService = new ThrowOnMoveCompanionFileService();
        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(),
            new RealFolderScanner(),
            new NoOpFileBotRunner(),
            new FixedExtensionPresetService(),
            new MetadataService(),
            new FakeProcessRunner(),
            new FileRouter(),
            companionService,
            new NoOpArrUnmonitorService(),
            new RecordingTrashService(),
            new NoOpRunLogger(),
            new NoOpResumeStateStore(),
            new NoOpProgressReporter(),
            configStore);

        var results = await RunLaneAsync(orchestrator,
            lane, config, _tempDir, "20260101_000000", new List<ResumeEntry>(), Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.True(result.Success);
        Assert.Contains("Companion files not moved", result.PostProcessWarning);
        Assert.False(companionService.CleanUpCalled, "a companion left stranded by a failed move must not have its folder swept as if genuinely empty");
    }

    // Code-review finding (pre-release review, HIGH): the source folder cleanup used to run
    // whenever the arr-unmonitor CALL didn't throw, regardless of whether the rescan it triggered
    // actually confirmed finishing - a timeout, a "failed" status, or a cancelled wait all left the
    // folder deleted anyway, which is exactly the bug the whole unmonitor-before-cleanup ordering
    // was meant to prevent (Sonarr/Radarr reads an already-gone folder as disconnected, not empty,
    // and never clears the episode/movie). Fixed via ArrRescanOutcome/SafeToCleanUp - cleanup now
    // only proceeds once the rescan is POSITIVELY confirmed complete, not just "the call returned."
    [Theory]
    [InlineData(ArrRescanOutcome.TimedOut)]
    [InlineData(ArrRescanOutcome.Failed)]
    [InlineData(ArrRescanOutcome.Cancelled)]
    public async Task ProcessLaneAsync_ArrRescanNotConfirmedComplete_DoesNotRunSourceFolderCleanup(ArrRescanOutcome outcome)
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        var movieBaseDir = Path.Combine(_tempDir, "Movies");
        Directory.CreateDirectory(movieBaseDir);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        // Nested subfolder, same shape as the orphaned-source-folder regression test - this is
        // exactly what CleanUpEmptySourceFolder would remove if it ran.
        var sourceSubfolder = Path.Combine(inputDir, "Caddyshack (1980)");
        Directory.CreateDirectory(sourceSubfolder);
        var sourcePath = Path.Combine(sourceSubfolder, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = movieBaseDir
        };

        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Delete, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        // Real CompanionFileService (not a NoOp) so CleanUpEmptySourceFolder's actual behavior is
        // exercised - a test using a NoOp fake couldn't distinguish "correctly skipped" from
        // "would have thrown anyway."
        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RecursiveFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new CompanionFileService(new DeletingTrashService()), new FixedOutcomeArrUnmonitorService(outcome),
            new DeletingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry>();
        var results = await RunLaneAsync(orchestrator,
            lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var result = Assert.Single(results);
        // The video itself converted and routed successfully - only the folder cleanup is
        // deferred, not the file's own disposition.
        Assert.True(result.Success);
        Assert.Contains("cleanup deferred", result.PostProcessWarning);
        Assert.True(File.Exists(Path.Combine(movieBaseDir, "Caddyshack (1980)", "Caddyshack (1980).mkv")));

        // The real assertion: an unconfirmed rescan must leave the source folder alone.
        Assert.False(File.Exists(sourcePath), "the video's own source file should still be moved/deleted as normal");
        Assert.True(Directory.Exists(sourceSubfolder), "the now-empty source folder must survive until the rescan is confirmed complete");

        // Code-review follow-up finding: a deferred cleanup used to be reported as Completed with
        // no persistent state, so nothing ever came back to finish it. It must be tracked instead.
        var entry = Assert.Single(resumeState);
        Assert.Equal(ResumeStatus.CleanupPending, entry.Status);
        Assert.Equal(sourceSubfolder, entry.PendingCompanionSourceDirectory);
        Assert.Equal(Path.Combine(movieBaseDir, "Caddyshack (1980)", "Caddyshack (1980).mkv"), entry.PendingCompanionVideoDestPath);
    }

    // The other half of the same fix, proven end-to-end: a CleanupPending entry left by a first
    // pass whose rescan never confirmed must actually get retried (not just remembered) once a
    // later pass's fresh rescan DOES confirm complete.
    [Fact]
    public async Task PrepareLaneAsync_CleanupPendingEntry_RetriesTheRescanAndRemovesTheFolderOnceConfirmed()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        var movieBaseDir = Path.Combine(_tempDir, "Movies");
        Directory.CreateDirectory(movieBaseDir);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourceSubfolder = Path.Combine(inputDir, "Caddyshack (1980)");
        Directory.CreateDirectory(sourceSubfolder);
        var sourcePath = Path.Combine(sourceSubfolder, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = movieBaseDir
        };

        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Delete, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        // First call (the video's own first pass) times out; every call after (the cleanup-retry
        // loop's fresh rescan) confirms complete.
        var arrService = new SequencedOutcomeArrUnmonitorService(ArrRescanOutcome.TimedOut, ArrRescanOutcome.Completed);
        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RecursiveFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new CompanionFileService(new DeletingTrashService()), arrService,
            new DeletingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeFilePath = Path.Combine(_tempDir, "resume.json");
        var resumeState = new List<ResumeEntry>();

        // Pass 1: encode + route succeeds, rescan times out - cleanup deferred.
        await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", resumeState, resumeFilePath, CancellationToken.None);

        var afterFirstPass = Assert.Single(resumeState);
        Assert.Equal(ResumeStatus.CleanupPending, afterFirstPass.Status);
        Assert.True(Directory.Exists(sourceSubfolder), "cleanup must stay deferred after an unconfirmed rescan");

        // Pass 2: nothing new to encode (Input has no video left) - only the cleanup retry runs,
        // and this time the rescan is confirmed complete.
        await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000100", resumeState, resumeFilePath, CancellationToken.None);

        var afterSecondPass = Assert.Single(resumeState);
        Assert.Equal(ResumeStatus.Completed, afterSecondPass.Status);
        Assert.Null(afterSecondPass.PendingCompanionSourceDirectory);
        Assert.Null(afterSecondPass.PendingCompanionVideoDestPath);
        Assert.False(Directory.Exists(sourceSubfolder), "the now-confirmed-safe folder should finally be removed");
    }

    // Reviewer's explicit "also test" cases: the SAME state gap existed at the tail of both retry
    // loops, not just the first pass - a retry that itself succeeds but whose own fresh rescan
    // isn't confirmed must land on CleanupPending too, not Completed.
    [Fact]
    public async Task PrepareLaneAsync_MoveFailedRetrySucceeds_ArrRescanNotConfirmed_LandsOnCleanupPending()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        var movieBaseDir = Path.Combine(_tempDir, "Movies");
        Directory.CreateDirectory(movieBaseDir);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourcePath = Path.Combine(inputDir, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = @"Z:\Unavailable\Movies" // first pass: unreachable, forces MoveFailed
        };

        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Delete, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new FixedOutcomeArrUnmonitorService(ArrRescanOutcome.TimedOut),
            new DeletingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry>();
        var resumeFilePath = Path.Combine(_tempDir, "resume.json");
        await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", resumeState, resumeFilePath, CancellationToken.None);
        Assert.Equal(ResumeStatus.MoveFailed, Assert.Single(resumeState).Status);

        // Second pass: base path is reachable now, so the move retry itself succeeds - but the
        // arr service always times out, so the entry must land on CleanupPending, not Completed.
        lane.MovieBasePath = movieBaseDir;
        await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000100", resumeState, resumeFilePath, CancellationToken.None);

        var entry = Assert.Single(resumeState);
        Assert.Equal(ResumeStatus.CleanupPending, entry.Status);
        Assert.True(File.Exists(Path.Combine(movieBaseDir, "Caddyshack (1980)", "Caddyshack (1980).mkv")), "the move retry itself should still have succeeded");
    }

    [Fact]
    public async Task PrepareLaneAsync_CompanionMoveFailedRetrySucceeds_ArrRescanNotConfirmed_LandsOnCleanupPending()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        var movieBaseDir = Path.Combine(_tempDir, "Movies");
        Directory.CreateDirectory(movieBaseDir);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourceSubfolder = Path.Combine(inputDir, "Caddyshack (1980)");
        Directory.CreateDirectory(sourceSubfolder);
        var strandedCompanionPath = Path.Combine(sourceSubfolder, "Caddyshack (1980).en.srt");
        File.WriteAllText(strandedCompanionPath, "subtitle");

        var videoDestFolder = Path.Combine(movieBaseDir, "Caddyshack (1980)");
        Directory.CreateDirectory(videoDestFolder);
        var videoDestPath = Path.Combine(videoDestFolder, "Caddyshack (1980).mkv");
        File.WriteAllText(videoDestPath, "already routed");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = movieBaseDir
        };

        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Delete, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RecursiveFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new CompanionFileService(new DeletingTrashService()), new FixedOutcomeArrUnmonitorService(ArrRescanOutcome.TimedOut),
            new DeletingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry>
        {
            new()
            {
                LaneId = "lane1",
                FullName = Path.Combine(sourceSubfolder, "Caddyshack (1980).mkv"),
                Status = ResumeStatus.CompanionMoveFailed,
                PendingCompanionSourceDirectory = sourceSubfolder,
                PendingCompanionVideoDestPath = videoDestPath
            }
        };
        var resumeFilePath = Path.Combine(_tempDir, "resume.json");
        new JsonResumeStateStore().Save(resumeState, resumeFilePath);

        await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", resumeState, resumeFilePath, CancellationToken.None);

        var entry = Assert.Single(resumeState);
        // The companion retry itself succeeded (it moved) - only the fresh rescan wasn't confirmed.
        Assert.Equal(ResumeStatus.CleanupPending, entry.Status);
        Assert.True(File.Exists(Path.Combine(videoDestFolder, "Caddyshack (1980).en.srt")));
        Assert.False(File.Exists(strandedCompanionPath));
        // Cleanup itself must still be deferred, same as the first-pass case.
        Assert.True(Directory.Exists(sourceSubfolder));
    }

    // Code-review finding (follow-up review, MEDIUM): once the *arr rescan WAS positively
    // confirmed, CleanUpEmptySourceFolder was still called inside a "log and move on" try/catch -
    // if the filesystem cleanup itself threw (locked file, permission, antivirus interference,
    // etc), the entry was marked Completed anyway, permanently forgetting a transient problem that
    // should have gotten another chance. All four call sites (first-pass, MoveFailed retry,
    // CompanionMoveFailed retry, and the CleanupPending retry loop itself) now land on
    // CleanupPending instead when the cleanup call throws, not just when the rescan isn't confirmed.
    [Fact]
    public async Task ProcessLaneAsync_ArrConfirmedButCleanupThrows_LandsOnCleanupPendingNotCompleted()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        var movieBaseDir = Path.Combine(_tempDir, "Movies");
        Directory.CreateDirectory(movieBaseDir);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourceSubfolder = Path.Combine(inputDir, "Caddyshack (1980)");
        Directory.CreateDirectory(sourceSubfolder);
        File.WriteAllText(Path.Combine(sourceSubfolder, "Caddyshack (1980).mkv"), "source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = movieBaseDir
        };

        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Delete, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RecursiveFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new ThrowOnCleanupCompanionFileService(), new FixedOutcomeArrUnmonitorService(ArrRescanOutcome.Completed),
            new DeletingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry>();
        var results = await RunLaneAsync(orchestrator,
            lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.True(result.Success); // the video itself is fine - only cleanup is outstanding
        // Code-review follow-up finding: unlike the sibling "rescan not confirmed" case, a
        // filesystem cleanup exception used to leave PostProcessWarning null - the report could
        // show a plain, unqualified "OK" for a file whose cleanup actually failed and is pending.
        Assert.Contains("Source folder cleanup deferred", result.PostProcessWarning);

        var entry = Assert.Single(resumeState);
        Assert.Equal(ResumeStatus.CleanupPending, entry.Status);
        Assert.Equal(sourceSubfolder, entry.PendingCompanionSourceDirectory);
    }

    [Fact]
    public async Task PrepareLaneAsync_CleanupPendingEntry_ArrConfirmedButCleanupThrows_StaysCleanupPending()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        var movieBaseDir = Path.Combine(_tempDir, "Movies");
        Directory.CreateDirectory(movieBaseDir);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourceSubfolder = Path.Combine(inputDir, "Caddyshack (1980)");
        Directory.CreateDirectory(sourceSubfolder);
        var videoDestFolder = Path.Combine(movieBaseDir, "Caddyshack (1980)");
        Directory.CreateDirectory(videoDestFolder);
        var videoDestPath = Path.Combine(videoDestFolder, "Caddyshack (1980).mkv");
        File.WriteAllText(videoDestPath, "already routed");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = movieBaseDir
        };
        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Delete, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RecursiveFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new ThrowOnCleanupCompanionFileService(), new FixedOutcomeArrUnmonitorService(ArrRescanOutcome.Completed),
            new DeletingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry>
        {
            new()
            {
                LaneId = "lane1",
                FullName = Path.Combine(sourceSubfolder, "Caddyshack (1980).mkv"),
                Status = ResumeStatus.CleanupPending,
                PendingCompanionSourceDirectory = sourceSubfolder,
                PendingCompanionVideoDestPath = videoDestPath
            }
        };
        var resumeFilePath = Path.Combine(_tempDir, "resume.json");
        new JsonResumeStateStore().Save(resumeState, resumeFilePath);

        await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", resumeState, resumeFilePath, CancellationToken.None);

        var entry = Assert.Single(resumeState);
        Assert.Equal(ResumeStatus.CleanupPending, entry.Status);
        Assert.NotNull(entry.LastRetryFailureMessage);
        Assert.True(Directory.Exists(sourceSubfolder), "the folder cleanup threw, so the folder must still be there to retry");
    }

    [Fact]
    public async Task PrepareLaneAsync_MoveFailedRetrySucceeds_ArrConfirmedButCleanupThrows_LandsOnCleanupPending()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        var movieBaseDir = Path.Combine(_tempDir, "Movies");
        Directory.CreateDirectory(movieBaseDir);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourcePath = Path.Combine(inputDir, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = @"Z:\Unavailable\Movies" // first pass: unreachable, forces MoveFailed
        };
        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Delete, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new ThrowOnCleanupCompanionFileService(), new FixedOutcomeArrUnmonitorService(ArrRescanOutcome.Completed),
            new DeletingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry>();
        var resumeFilePath = Path.Combine(_tempDir, "resume.json");
        await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", resumeState, resumeFilePath, CancellationToken.None);
        Assert.Equal(ResumeStatus.MoveFailed, Assert.Single(resumeState).Status);

        // Second pass: base path is reachable now - the move retry succeeds, the arr rescan
        // confirms complete, but the folder cleanup itself throws.
        lane.MovieBasePath = movieBaseDir;
        await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000100", resumeState, resumeFilePath, CancellationToken.None);

        var entry = Assert.Single(resumeState);
        Assert.Equal(ResumeStatus.CleanupPending, entry.Status);
        Assert.True(File.Exists(Path.Combine(movieBaseDir, "Caddyshack (1980)", "Caddyshack (1980).mkv")), "the move retry itself should still have succeeded");
    }

    [Fact]
    public async Task PrepareLaneAsync_CompanionMoveFailedRetrySucceeds_ArrConfirmedButCleanupThrows_LandsOnCleanupPending()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        var movieBaseDir = Path.Combine(_tempDir, "Movies");
        Directory.CreateDirectory(movieBaseDir);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        // Deliberately no physical companion file left in sourceSubfolder - the test fake's
        // RecursiveFolderScanner (unlike the real VideoFileScanner) doesn't filter by extension,
        // so a leftover file there would get picked up as a spurious "new video" on the scan phase
        // that runs later in the same PrepareLaneAsync call, polluting resumeState with an unrelated
        // second entry. This test only needs to isolate the CleanUpEmptySourceFolder exception path,
        // not re-prove the companion move itself (already covered by the sibling test above).
        var sourceSubfolder = Path.Combine(inputDir, "Caddyshack (1980)");
        Directory.CreateDirectory(sourceSubfolder);

        var videoDestFolder = Path.Combine(movieBaseDir, "Caddyshack (1980)");
        Directory.CreateDirectory(videoDestFolder);
        var videoDestPath = Path.Combine(videoDestFolder, "Caddyshack (1980).mkv");
        File.WriteAllText(videoDestPath, "already routed");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = movieBaseDir
        };
        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Delete, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RecursiveFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new ThrowOnCleanupCompanionFileService(), new FixedOutcomeArrUnmonitorService(ArrRescanOutcome.Completed),
            new DeletingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry>
        {
            new()
            {
                LaneId = "lane1",
                FullName = Path.Combine(sourceSubfolder, "Caddyshack (1980).mkv"),
                Status = ResumeStatus.CompanionMoveFailed,
                PendingCompanionSourceDirectory = sourceSubfolder,
                PendingCompanionVideoDestPath = videoDestPath
            }
        };
        var resumeFilePath = Path.Combine(_tempDir, "resume.json");
        new JsonResumeStateStore().Save(resumeState, resumeFilePath);

        await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", resumeState, resumeFilePath, CancellationToken.None);

        var entry = Assert.Single(resumeState);
        Assert.Equal(ResumeStatus.CleanupPending, entry.Status);
    }

    // Code-review finding (pre-release review, MEDIUM): a companion-file move failure was logged
    // and warned about, but the resume entry was still marked Completed - nothing ever came back
    // around to retry the stranded companion, so it just sat there forever unless a human noticed
    // the warning and moved it by hand. Fixed via the new CompanionMoveFailed status: the video
    // itself IS done (this is NOT the same as a real move failure), only its companions need a
    // retry, which happens automatically on this lane's next pass (PrepareLaneAsync's own
    // companion-retry loop - see the sibling test below).
    [Fact]
    public async Task ProcessLaneAsync_CompanionFileMoveFails_MarksResumeEntryCompanionMoveFailedForRetry()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        var movieBaseDir = Path.Combine(_tempDir, "Movies");
        Directory.CreateDirectory(movieBaseDir);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourceSubfolder = Path.Combine(inputDir, "Caddyshack (1980)");
        Directory.CreateDirectory(sourceSubfolder);
        var sourcePath = Path.Combine(sourceSubfolder, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = movieBaseDir
        };

        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Delete, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var resumeState = new List<ResumeEntry>();
        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RecursiveFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new ThrowOnMoveCompanionFileService(), new NoOpArrUnmonitorService(),
            new DeletingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var entry = Assert.Single(resumeState);
        var expectedDestPath = Path.Combine(movieBaseDir, "Caddyshack (1980)", "Caddyshack (1980).mkv");
        Assert.Equal(ResumeStatus.CompanionMoveFailed, entry.Status);
        Assert.Equal(sourceSubfolder, entry.PendingCompanionSourceDirectory);
        Assert.Equal(expectedDestPath, entry.PendingCompanionVideoDestPath);
        // The video itself still landed correctly - only its companions are stranded.
        Assert.True(File.Exists(expectedDestPath));
    }

    [Fact]
    public async Task PrepareLaneAsync_CompanionMoveFailedEntry_RetriesTheStrandedCompanionAndCleansUpTheFolder()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        var movieBaseDir = Path.Combine(_tempDir, "Movies");
        Directory.CreateDirectory(movieBaseDir);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        // The stranded companion, still sitting in the video's own original source folder - the
        // video itself is long gone from here (already routed on an earlier pass).
        var sourceSubfolder = Path.Combine(inputDir, "Caddyshack (1980)");
        Directory.CreateDirectory(sourceSubfolder);
        var strandedCompanionPath = Path.Combine(sourceSubfolder, "Caddyshack (1980).en.srt");
        File.WriteAllText(strandedCompanionPath, "subtitle");

        // Where the video already, successfully, landed on the earlier pass.
        var videoDestFolder = Path.Combine(movieBaseDir, "Caddyshack (1980)");
        Directory.CreateDirectory(videoDestFolder);
        var videoDestPath = Path.Combine(videoDestFolder, "Caddyshack (1980).mkv");
        File.WriteAllText(videoDestPath, "already routed");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = movieBaseDir
        };

        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Delete, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RecursiveFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new CompanionFileService(new DeletingTrashService()), new NoOpArrUnmonitorService(),
            new DeletingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry>
        {
            new()
            {
                LaneId = "lane1",
                FullName = Path.Combine(sourceSubfolder, "Caddyshack (1980).mkv"), // original video path, no longer on disk
                Status = ResumeStatus.CompanionMoveFailed,
                PendingCompanionSourceDirectory = sourceSubfolder,
                PendingCompanionVideoDestPath = videoDestPath
            }
        };
        var resumeFilePath = Path.Combine(_tempDir, "resume.json");
        new JsonResumeStateStore().Save(resumeState, resumeFilePath);

        await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", resumeState, resumeFilePath, CancellationToken.None);

        var entry = Assert.Single(resumeState);
        Assert.Equal(ResumeStatus.Completed, entry.Status);
        Assert.Null(entry.PendingCompanionSourceDirectory);
        Assert.Null(entry.PendingCompanionVideoDestPath);

        // The companion actually moved alongside the video...
        Assert.True(File.Exists(Path.Combine(videoDestFolder, "Caddyshack (1980).en.srt")));
        Assert.False(File.Exists(strandedCompanionPath));
        // ...and now that it's no longer stranded, the source folder is safe to remove too.
        Assert.False(Directory.Exists(sourceSubfolder));
    }

    [Fact]
    public async Task ProcessLaneAsync_ArrUnmonitorFails_SetsWarningButStaysSuccessful()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourcePath = Path.Combine(inputDir, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset"
        };

        var config = BuildConfig(DeleteAfterConvertMode.Maintain);
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(),
            new RealFolderScanner(),
            new NoOpFileBotRunner(),
            new FixedExtensionPresetService(),
            new MetadataService(),
            new FakeProcessRunner(),
            new FileRouter(),
            new NoOpCompanionFileService(),
            new ThrowingArrUnmonitorService(),
            new RecordingTrashService(),
            new NoOpRunLogger(),
            new NoOpResumeStateStore(),
            new NoOpProgressReporter(),
            configStore);

        var results = await RunLaneAsync(orchestrator,
            lane, config, _tempDir, "20260101_000000", new List<ResumeEntry>(), Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.True(result.Success);
        Assert.StartsWith("Failed:", result.ArrStatus);
        Assert.Contains("Sonarr/Radarr unmonitor failed", result.PostProcessWarning);
    }

    [Fact]
    public async Task ProcessLaneAsync_NoPresetConfigured_ReportsSpecificFailureReason()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourcePath = Path.Combine(inputDir, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir
            // MoviePreset intentionally left unset - this is what's under test.
        };

        var config = BuildConfig(DeleteAfterConvertMode.Maintain);
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(),
            new RealFolderScanner(),
            new NoOpFileBotRunner(),
            new FixedExtensionPresetService(),
            new MetadataService(),
            new FakeProcessRunner(),
            new FileRouter(),
            new NoOpCompanionFileService(),
            new NoOpArrUnmonitorService(),
            new RecordingTrashService(),
            new NoOpRunLogger(),
            new NoOpResumeStateStore(),
            new NoOpProgressReporter(),
            configStore);

        var results = await RunLaneAsync(orchestrator,
            lane, config, _tempDir, "20260101_000000", new List<ResumeEntry>(), Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.False(result.Success);
        Assert.Equal("No Movie preset configured for this lane", result.FailureReason);
    }

    [Fact]
    public async Task ProcessLaneAsync_MoveFailsForUnrelatedReason_FallsBackToGenericError()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var sourcePath = Path.Combine(inputDir, "Caddyshack (1980).mkv");
        File.WriteAllText(sourcePath, "source");

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Test Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir,
            MoviePreset = "Any Preset",
            MovieBasePath = @"D:\Movies"
        };

        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Maintain, MoveFiles = true, ClearTitleMetadata = false }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(),
            new RealFolderScanner(),
            new NoOpFileBotRunner(),
            new FixedExtensionPresetService(),
            new MetadataService(),
            new FakeProcessRunner(),
            new ThrowingFileRouter(new UnauthorizedAccessException(@"Access to the path 'D:\Movies' is denied.")),
            new NoOpCompanionFileService(),
            new NoOpArrUnmonitorService(),
            new RecordingTrashService(),
            new NoOpRunLogger(),
            new NoOpResumeStateStore(),
            new NoOpProgressReporter(),
            configStore);

        var results = await RunLaneAsync(orchestrator,
            lane, config, _tempDir, "20260101_000000", new List<ResumeEntry>(), Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.False(result.Success);
        Assert.False(result.DiskFull);
        // A permission error isn't a path-availability problem - mislabeling it that way would be
        // actively wrong, not just unhelpfully vague, so this falls back to the generic reason.
        Assert.Null(result.FailureReason);
        // The file is not lost - it's exactly where HandBrake wrote it, since routing threw before
        // it could move it anywhere. Under its own collision-safe staging name (not
        // "Caddyshack (1980).mkv" directly) - see finding #4's fix: a deterministic name is never
        // used for a file whose routing hasn't actually succeeded yet.
        Assert.NotNull(result.NewFileName);
        Assert.True(File.Exists(result.NewFileName));
        Assert.Contains(".compressarr-", result.NewFileName);
    }

    [Fact]
    public async Task PrepareLane_FileBotFlagsOneFileUnmatched_OnlyThatEntryIsStamped()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        Directory.CreateDirectory(inputDir);

        var matchedPath = Path.Combine(inputDir, "matched.mkv");
        var unmatchedPath = Path.Combine(inputDir, "unmatched.mkv");
        File.WriteAllText(matchedPath, "a");
        File.WriteAllText(unmatchedPath, "b");

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = Path.Combine(_tempDir, "Output"), MoviePreset = "Any Preset" };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new FixedUnmatchedFileBotRunner(new HashSet<string> { unmatchedPath }),
            new FixedExtensionPresetService(), new MetadataService(), new FakeProcessRunner(), new FileRouter(),
            new NoOpCompanionFileService(), new NoOpArrUnmonitorService(), new RecordingTrashService(), new NoOpRunLogger(),
            new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry>();
        await orchestrator.PrepareLaneAsync(lane, config, resumeState, Path.Combine(_tempDir, "resume.json"));

        var matchedEntry = Assert.Single(resumeState, e => e.FullName == matchedPath);
        var unmatchedEntry = Assert.Single(resumeState, e => e.FullName == unmatchedPath);
        Assert.False(matchedEntry.FileBotUnmatched);
        Assert.True(unmatchedEntry.FileBotUnmatched);
    }

    private ConversionOrchestrator OrchestratorWithFileBot(IFileBotRunner fileBot, CompressarrConfig config) => new(
        new PassThroughPathExpander(), new RealFolderScanner(), fileBot,
        new FixedExtensionPresetService(), new MetadataService(), new FakeProcessRunner(), new FileRouter(),
        new NoOpCompanionFileService(), new NoOpArrUnmonitorService(), new RecordingTrashService(), new NoOpRunLogger(),
        new NoOpResumeStateStore(), new NoOpProgressReporter(), new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue));

    [Fact]
    public async Task PrepareLane_FileBotRenamesAQueuedFile_ItKeepsItsPlaceSkipAndPresetOverride()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        Directory.CreateDirectory(inputDir);
        var a = Path.Combine(inputDir, "a.mkv");
        var b = Path.Combine(inputDir, "b.mkv");
        var c = Path.Combine(inputDir, "c.mkv");
        foreach (var p in new[] { a, b, c }) File.WriteAllText(p, "x");
        var renamedB = Path.Combine(inputDir, "Show - S01E02 - Pilot.mkv");

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = Path.Combine(_tempDir, "Output"), MoviePreset = "Any Preset" };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.Lanes.Add(lane);
        var orchestrator = OrchestratorWithFileBot(new RenamingFileBotRunner(new() { [b] = renamedB }), config);

        // The user dragged b to the front, skipped it and gave it its own preset.
        var resumeState = new List<ResumeEntry>
        {
            new() { LaneId = "lane1", FullName = a, Status = ResumeStatus.Pending, Order = 2 },
            new() { LaneId = "lane1", FullName = b, Status = ResumeStatus.Pending, Order = 0, Skipped = true, PresetOverride = "Fast 1080p30" },
            new() { LaneId = "lane1", FullName = c, Status = ResumeStatus.Pending, Order = 1 }
        };
        await orchestrator.PrepareLaneAsync(lane, config, resumeState, Path.Combine(_tempDir, "resume.json"));

        Assert.Equal(3, resumeState.Count);
        Assert.DoesNotContain(resumeState, e => e.FullName == b);
        var renamed = Assert.Single(resumeState, e => e.FullName == renamedB);
        Assert.Equal(0, renamed.Order);
        Assert.True(renamed.Skipped);
        Assert.Equal("Fast 1080p30", renamed.PresetOverride);
        Assert.Equal(2, resumeState.Single(e => e.FullName == a).Order); // untouched
        Assert.Equal(1, resumeState.Single(e => e.FullName == c).Order); // untouched
    }

    [Fact]
    public async Task PrepareLane_FileBotAnnouncesARenameThatDidNotHappen_NothingIsRemapped()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        Directory.CreateDirectory(inputDir);
        var a = Path.Combine(inputDir, "a.mkv");
        File.WriteAllText(a, "x");

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = Path.Combine(_tempDir, "Output"), MoviePreset = "Any Preset" };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.Lanes.Add(lane);
        var orchestrator = OrchestratorWithFileBot(new RenamingFileBotRunner(new() { [a] = Path.Combine(inputDir, "never-created.mkv") }, actuallyMove: false), config);

        var resumeState = new List<ResumeEntry> { new() { LaneId = "lane1", FullName = a, Status = ResumeStatus.Pending, Order = 5 } };
        await orchestrator.PrepareLaneAsync(lane, config, resumeState, Path.Combine(_tempDir, "resume.json"));

        var entry = Assert.Single(resumeState);
        Assert.Equal(a, entry.FullName);
        Assert.Equal(5, entry.Order);
    }

    [Fact]
    public async Task PrepareLane_FileBotRenamesAFileTheQueueNeverTracked_ItIsJustANewArrival()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        Directory.CreateDirectory(inputDir);
        var waiting = Path.Combine(inputDir, "waiting.mkv");
        var fresh = Path.Combine(inputDir, "fresh.mkv");
        File.WriteAllText(waiting, "x");
        File.WriteAllText(fresh, "x");
        var renamedFresh = Path.Combine(inputDir, "A Fresh Name.mkv"); // sorts before "waiting" on a scan

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = Path.Combine(_tempDir, "Output"), MoviePreset = "Any Preset" };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.Lanes.Add(lane);
        var orchestrator = OrchestratorWithFileBot(new RenamingFileBotRunner(new() { [fresh] = renamedFresh }), config);

        var resumeState = new List<ResumeEntry> { new() { LaneId = "lane1", FullName = waiting, Status = ResumeStatus.Pending, Order = 0 } };
        await orchestrator.PrepareLaneAsync(lane, config, resumeState, Path.Combine(_tempDir, "resume.json"));

        // Never tracked before, so there was no place in line to keep: it joins at the end.
        Assert.Equal(2, resumeState.Count);
        Assert.Equal(0, resumeState.Single(e => e.FullName == waiting).Order);
        Assert.Equal(1, resumeState.Single(e => e.FullName == renamedFresh).Order);
    }

    [Fact]
    public async Task PrepareLane_FileBotCliPathHasToken_IsExpandedBeforeBeingPassedToRunner()
    {
        // Regression: PrepareLane originally passed config.FileBot straight through to
        // IFileBotRunner.Run without expanding CliPath first (unlike HandBrake's own CliPath,
        // expanded a few lines above this in real PrepareLane) - found live when a real
        // %ProgramFiles%-style token in Settings made FileBotRunner's own File.Exists check fail
        // and silently skip FileBot entirely.
        var inputDir = Path.Combine(_tempDir, "Input");
        Directory.CreateDirectory(inputDir);

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = Path.Combine(_tempDir, "Output"), MoviePreset = "Any Preset" };
        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false },
            FileBot = new FileBotSettings { Enabled = true, CliPath = "%TestToken%" }
        };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);
        var fileBotRunner = new RecordingFileBotRunner();

        var orchestrator = new ConversionOrchestrator(
            new TokenPathExpander(), new RealFolderScanner(), fileBotRunner,
            new FixedExtensionPresetService(), new MetadataService(), new FakeProcessRunner(), new FileRouter(),
            new NoOpCompanionFileService(), new NoOpArrUnmonitorService(), new RecordingTrashService(), new NoOpRunLogger(),
            new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        await orchestrator.PrepareLaneAsync(lane, config, new List<ResumeEntry>(), Path.Combine(_tempDir, "resume.json"));

        Assert.Equal(@"C:\Expanded\FileBot.exe", fileBotRunner.ReceivedCliPath);
    }

    [Fact]
    public void PruneOrphanedLaneEntries_RemovesEntriesUnderADeletedLane_ButLeavesRealLanesAlone()
    {
        // Real gap found live: a resume entry's LaneId can outlive the lane itself (renamed or
        // deleted from config) - every one of PrepareLaneAsync's own dead-entry/retry loops is
        // scoped to `e.LaneId == lane.Id` while iterating config.Lanes, so such an entry is
        // invisible to all of them and sits in resume.json forever - inflating "Resuming previous
        // incomplete run"'s tracked count on every pass and permanently blocking
        // RunOrchestrator's end-of-pass stillOutstanding wipe, so even legitimately-Completed
        // history entries sitting alongside it never get cleaned up either.
        var lane1Input = Path.Combine(_tempDir, "Lane1Input");
        Directory.CreateDirectory(lane1Input);
        var lane1 = new LaneConfig { Id = "lane1", DisplayName = "Lane One", Enabled = true, Input = lane1Input, Output = Path.Combine(_tempDir, "Output1"), MoviePreset = "Any Preset" };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.Lanes.Add(lane1);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new RecordingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var realEntry = new ResumeEntry { LaneId = "lane1", FullName = Path.Combine(lane1Input, "a.mkv"), Status = ResumeStatus.Completed };
        var orphaned = new ResumeEntry { LaneId = "deleted-lane-guid", FullName = Path.Combine(_tempDir, "Corrupt Test File (2020).mkv"), Status = ResumeStatus.Pending, Removed = true, Skipped = true };
        var resumeState = new List<ResumeEntry> { realEntry, orphaned };
        var resumeFilePath = Path.Combine(_tempDir, "resume.json");

        orchestrator.PruneOrphanedLaneEntries(config, resumeState, resumeFilePath);

        var remaining = Assert.Single(resumeState);
        Assert.Same(realEntry, remaining);

        // A second call with nothing left to prune must be a true no-op, not throw or re-save
        // needlessly.
        orchestrator.PruneOrphanedLaneEntries(config, resumeState, resumeFilePath);
        Assert.Single(resumeState);
    }

    [Fact]
    public async Task PrepareLaneAsync_FreshlyScannedFiles_GetPermanentSequentialOrderStamped()
    {
        // The core of the queue-order-locking requirement: the moment a file is first discovered
        // by a real scan, it must get a permanent Order - not stay null and keep sorting by
        // whatever the live filesystem enumeration happens to return (VideoFileScanner walks the
        // directory tree via Directory.EnumerateFiles/EnumerateDirectories, which is NOT
        // guaranteed to put a newly-added file last, or even stay stable run to run).
        var inputDir = Path.Combine(_tempDir, "Input");
        Directory.CreateDirectory(inputDir);
        var fileA = Path.Combine(inputDir, "a.mkv");
        var fileB = Path.Combine(inputDir, "b.mkv");
        File.WriteAllText(fileA, "a");
        File.WriteAllText(fileB, "b");

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = Path.Combine(_tempDir, "Output"), MoviePreset = "Any Preset" };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new RecordingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var resumeState = new List<ResumeEntry>();
        await orchestrator.PrepareLaneAsync(lane, config, resumeState, Path.Combine(_tempDir, "resume.json"));

        Assert.Equal(2, resumeState.Count);
        Assert.All(resumeState, e => Assert.True(e.Order.HasValue));
        Assert.Equal(resumeState.Select(e => e.Order).Distinct().Count(), resumeState.Count);

        // Simulate both files having since finished processing (PrepareLaneAsync only re-scans a
        // lane's Input folder for brand-new files once its Pending backlog is fully drained - see
        // its own pending.Count > 0 branch), then a new file appears that would sort ALPHABETICALLY
        // FIRST. It must still be appended AFTER both already-known files' Order values, never
        // inserted ahead of them just because of where it happens to sort in a live scan.
        foreach (var e in resumeState) e.Status = ResumeStatus.Completed;
        var maxOrderSoFar = resumeState.Max(e => e.Order)!.Value;

        var fileEarlyAlphabetically = Path.Combine(inputDir, "AAA_should_still_go_last.mkv");
        File.WriteAllText(fileEarlyAlphabetically, "c");
        await orchestrator.PrepareLaneAsync(lane, config, resumeState, Path.Combine(_tempDir, "resume.json"));

        var newEntry = Assert.Single(resumeState, e => e.FullName == fileEarlyAlphabetically);
        Assert.True(newEntry.Order > maxOrderSoFar, "a newly-discovered file must be appended after every already-known Order, never inserted ahead of them");
    }

    private ConversionOrchestrator NewOrderTestOrchestrator(LaneConfig lane, out CompressarrConfig config)
    {
        config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);
        return new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new RecordingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);
    }

    [Fact]
    public async Task PrepareLaneAsync_LaneAlreadyHasABacklog_NewFilesAreStillTrackedAtTheEndInsteadOfLeftToLiveScanOrder()
    {
        // Real bug: PrepareLaneAsync only scanned for new files when the lane had zero Pending
        // entries. While a backlog existed (any long encode), a new file stayed untracked and the
        // Monitor page ordered it by live folder-scan position - so a file that arrived later but
        // enumerated earlier displayed above one already waiting, and when the backlog finally
        // drained, both were stamped in scan order, making that wrong order permanent.
        var inputDir = Path.Combine(_tempDir, "Input");
        Directory.CreateDirectory(inputDir);
        var waiting = Path.Combine(inputDir, "zzz_already_waiting.mkv");
        File.WriteAllText(waiting, "w");

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = Path.Combine(_tempDir, "Output"), MoviePreset = "Any Preset" };
        var orchestrator = NewOrderTestOrchestrator(lane, out var config);
        var resumeState = new List<ResumeEntry>
        {
            new() { LaneId = "lane1", FullName = waiting, Status = ResumeStatus.Pending, Order = 0 }
        };

        var arrivedLater = Path.Combine(inputDir, "aaa_arrived_later.mkv");
        File.WriteAllText(arrivedLater, "n");
        await orchestrator.PrepareLaneAsync(lane, config, resumeState, Path.Combine(_tempDir, "resume.json"));

        var newEntry = Assert.Single(resumeState, e => e.FullName == arrivedLater);
        Assert.Equal(ResumeStatus.Pending, newEntry.Status);
        Assert.True(newEntry.Order > 0, "a file arriving while others are already waiting must be stamped after them");
        Assert.Equal(new[] { waiting, arrivedLater }, resumeState.OrderBy(e => e.Order).Select(e => e.FullName));
    }

    [Fact]
    public async Task PrepareLaneAsync_ReaddedFileWithOldCompletedEntry_GoesToTheEndInsteadOfKeepingItsOldLowOrder()
    {
        // Real bug: a file reappearing at a path that already had a Completed (or Error) entry was
        // flipped back to Pending but kept that entry's old Order - usually a low number - so a
        // re-added file jumped above everything actually waiting.
        var inputDir = Path.Combine(_tempDir, "Input");
        Directory.CreateDirectory(inputDir);
        var readded = Path.Combine(inputDir, "readded.mkv");
        File.WriteAllText(readded, "r");

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = Path.Combine(_tempDir, "Output"), MoviePreset = "Any Preset" };
        var orchestrator = NewOrderTestOrchestrator(lane, out var config);
        var oldEntry = new ResumeEntry { LaneId = "lane1", FullName = readded, Status = ResumeStatus.Completed, Order = 0 };
        var resumeState = new List<ResumeEntry>
        {
            oldEntry,
            new() { LaneId = "lane1", FullName = Path.Combine(inputDir, "long_gone.mkv"), Status = ResumeStatus.Completed, Order = 7 }
        };

        await orchestrator.PrepareLaneAsync(lane, config, resumeState, Path.Combine(_tempDir, "resume.json"));

        Assert.Equal(ResumeStatus.Pending, oldEntry.Status);
        Assert.True(oldEntry.Order > 7, "a re-added file is a new arrival and belongs at the end, not at its old position");
    }

    [Fact]
    public void BackfillMissingOrder_FreezesTodaysDisplayOrder_SoAPresetOverrideAfterwardNeverMovesAFile()
    {
        // The actual user requirement this exists to satisfy: "once files appear in the queue,
        // they must be locked in that order no matter what... if any changes are made to the
        // preset, they should NOT change the order... only the user has the power to change the
        // order of processing." Backfill exists to give every PRE-EXISTING (pre-upgrade,
        // Order-less) entry a permanent position too, not just newly-discovered ones.
        var lane1Input = Path.Combine(_tempDir, "Lane1Input");
        var lane2Input = Path.Combine(_tempDir, "Lane2Input");
        Directory.CreateDirectory(lane1Input);
        Directory.CreateDirectory(lane2Input);

        var lane1FileA = Path.Combine(lane1Input, "a.mkv");
        var lane1FileB = Path.Combine(lane1Input, "b.mkv");
        var lane2FileC = Path.Combine(lane2Input, "c.mkv");
        File.WriteAllText(lane1FileA, "a");
        File.WriteAllText(lane1FileB, "b");
        File.WriteAllText(lane2FileC, "c");

        var lane1 = new LaneConfig { Id = "lane1", DisplayName = "Lane One", Enabled = true, Input = lane1Input, Output = Path.Combine(_tempDir, "Output1"), MoviePreset = "Any Preset" };
        var lane2 = new LaneConfig { Id = "lane2", DisplayName = "Lane Two", Enabled = true, Input = lane2Input, Output = Path.Combine(_tempDir, "Output2"), MoviePreset = "Any Preset" };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.Lanes.Add(lane1);
        config.Lanes.Add(lane2);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            new FakeProcessRunner(), new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new RecordingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var entryA = new ResumeEntry { LaneId = "lane1", FullName = lane1FileA, Status = ResumeStatus.Pending };
        var entryB = new ResumeEntry { LaneId = "lane1", FullName = lane1FileB, Status = ResumeStatus.Pending };
        var entryC = new ResumeEntry { LaneId = "lane2", FullName = lane2FileC, Status = ResumeStatus.Pending };
        var resumeState = new List<ResumeEntry> { entryA, entryB, entryC };
        var resumeFilePath = Path.Combine(_tempDir, "resume.json");

        orchestrator.BackfillMissingOrder(config, resumeState, resumeFilePath);

        // Every entry now has a real, distinct Order, in lane-then-natural-scan sequence (lane1
        // before lane2, matching config.Lanes order; a before b within lane1's own scan).
        Assert.NotNull(entryA.Order);
        Assert.NotNull(entryB.Order);
        Assert.NotNull(entryC.Order);
        Assert.True(entryA.Order < entryB.Order);
        Assert.True(entryB.Order < entryC.Order);

        // Calling it again must be a true no-op - nobody's Order changes just because the method
        // ran a second time (the common case, every pass after the first).
        var (a0, b0, c0) = (entryA.Order, entryB.Order, entryC.Order);
        orchestrator.BackfillMissingOrder(config, resumeState, resumeFilePath);
        Assert.Equal(a0, entryA.Order);
        Assert.Equal(b0, entryB.Order);
        Assert.Equal(c0, entryC.Order);

        // Now override entryA's (currently first) preset - simulating the Monitor page's preset
        // click, which only ever changes PresetOverride. Its relative rank against B and C, by the
        // app's own real combined-queue sort key, must be completely unchanged.
        entryA.PresetOverride = "Some Other Preset";

        var order = resumeState.OrderBy(e => e.Order ?? int.MaxValue).ToList();
        Assert.Equal(new[] { entryA, entryB, entryC }, order);
    }
    // ---- Phase 4: assigning a file to land in another lane's library -----------------------------
    // The file never moves; only the library its finished encode is routed into changes. Everything
    // else - preset, Output folder, queue position, the cleanup boundary - stays with its own lane.

    private sealed class TwoLanes
    {
        public required CompressarrConfig Config { get; init; }
        public required LaneConfig Home { get; init; }
        public required LaneConfig Kids { get; init; }
        public required string HomeInput { get; init; }
        public required string KidsInput { get; init; }
        public required string HomeOutput { get; init; }
        public required string HomeMovies { get; init; }
        public required string HomeTv { get; init; }
        public required string KidsMovies { get; init; }
        public required string KidsTv { get; init; }
    }

    private TwoLanes BuildTwoLanes(bool moveFiles = true)
    {
        string Dir(string name) { var p = Path.Combine(_tempDir, name); Directory.CreateDirectory(p); return p; }

        var homeInput = Dir("HomeInput");
        var kidsInput = Dir("KidsInput");
        var homeOutput = Dir("HomeOutput");
        var home = new LaneConfig
        {
            Id = "hdsd", DisplayName = "SD-HD", Enabled = true, Input = homeInput, Output = homeOutput,
            TvPreset = "Home TV Preset", MoviePreset = "Home Movie Preset",
            TvShowBasePath = Dir("HomeTvLibrary"), MovieBasePath = Dir("HomeMovieLibrary")
        };
        var kids = new LaneConfig
        {
            Id = "kids", DisplayName = "Kids", Enabled = true, Input = kidsInput, Output = Dir("KidsOutput"),
            TvPreset = "Kids TV Preset", MoviePreset = "Kids Movie Preset",
            TvShowBasePath = Dir("KidsTvLibrary"), MovieBasePath = Dir("KidsMovieLibrary")
        };
        var config = new CompressarrConfig
        {
            Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Delete, MoveFiles = moveFiles, ClearTitleMetadata = false }
        };
        config.Lanes.Add(home);
        config.Lanes.Add(kids);

        return new TwoLanes
        {
            Config = config, Home = home, Kids = kids, HomeInput = homeInput, KidsInput = kidsInput, HomeOutput = homeOutput,
            HomeMovies = home.MovieBasePath, HomeTv = home.TvShowBasePath, KidsMovies = kids.MovieBasePath, KidsTv = kids.TvShowBasePath
        };
    }

    private ConversionOrchestrator OrchestratorFor(TwoLanes setup, IEncoderRunner runner) => new(
        new PassThroughPathExpander(), new RecursiveFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
        runner, new FileRouter(), new CompanionFileService(new DeletingTrashService()), new NoOpArrUnmonitorService(),
        new DeletingTrashService(), new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(),
        new SwitchingConfigStore(setup.Config, setup.Config, switchOnCall: int.MaxValue));

    /// <summary>One movie in its own subfolder of the home lane's Input, assigned to <paramref name="destination"/>.</summary>
    private (string SourcePath, string SourceFolder, ResumeEntry Entry) QueueMovie(TwoLanes setup, string? destination, string title = "Sesame Street (1969)")
    {
        var folder = Path.Combine(setup.HomeInput, title);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, title + ".mkv");
        File.WriteAllText(path, "source");
        var entry = new ResumeEntry { LaneId = setup.Home.Id, FullName = path, Status = ResumeStatus.Pending, Order = 7, DestinationLaneId = destination, PresetOverride = null };
        return (path, folder, entry);
    }

    [Fact]
    public async Task LaneAssignment_AMovieLandsInTheDestinationLanesLibrary_NotItsOwn()
    {
        var setup = BuildTwoLanes();
        var (_, _, entry) = QueueMovie(setup, destination: "kids");
        var orchestrator = OrchestratorFor(setup, new FakeProcessRunner());
        var resumeState = new List<ResumeEntry> { entry };

        var results = await RunLaneAsync(orchestrator, setup.Home, setup.Config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.True(result.Success);
        Assert.True(File.Exists(Path.Combine(setup.KidsMovies, "Sesame Street (1969)", "Sesame Street (1969).mkv")));
        Assert.Empty(Directory.GetFileSystemEntries(setup.HomeMovies)); // nothing landed in its own lane's library
        Assert.Equal("kids", result.RedirectedToLaneId);
        Assert.Equal("Kids", result.RedirectedToLaneName);
        Assert.Equal("SD-HD", result.HomeLaneName);
        Assert.Equal(ResumeStatus.Completed, entry.Status);
        Assert.Equal("hdsd", entry.LaneId); // its home lane never changes
    }

    [Fact]
    public async Task LaneAssignment_ATvEpisodeLandsInTheDestinationLanesTvLibrary()
    {
        var setup = BuildTwoLanes();
        var folder = Path.Combine(setup.HomeInput, "Sesame Street");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "Sesame Street - S01E01 - Pilot.mkv");
        File.WriteAllText(path, "source");
        var entry = new ResumeEntry { LaneId = "hdsd", FullName = path, Status = ResumeStatus.Pending, Order = 0, DestinationLaneId = "kids" };
        var orchestrator = OrchestratorFor(setup, new FakeProcessRunner());

        await RunLaneAsync(orchestrator, setup.Home, setup.Config, _tempDir, "20260101_000000", new List<ResumeEntry> { entry }, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        Assert.NotEmpty(Directory.GetFiles(setup.KidsTv, "*.mkv", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFileSystemEntries(setup.HomeTv));
    }

    [Fact]
    public async Task LaneAssignment_ChangesOnlyTheLandingLibrary_NotPresetOutputFolderOrOrder()
    {
        var setup = BuildTwoLanes();
        var (_, _, entry) = QueueMovie(setup, destination: "kids");
        var runner = new RequestRecordingRunner();
        var orchestrator = OrchestratorFor(setup, runner);
        var resumeState = new List<ResumeEntry> { entry };

        await RunLaneAsync(orchestrator, setup.Home, setup.Config, _tempDir, "20260101_000000", resumeState, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var request = Assert.Single(runner.Requests);
        Assert.Equal("Home Movie Preset", request.PresetName);                              // the home lane's preset, not Kids'
        Assert.Equal(setup.HomeOutput, Path.GetDirectoryName(request.OutputPath));          // staged in the home lane's Output
        Assert.Equal(7, entry.Order);                                                       // queue position untouched
    }

    [Fact]
    public async Task LaneAssignment_APerFilePresetOverrideStillApplies()
    {
        var setup = BuildTwoLanes();
        var (_, _, entry) = QueueMovie(setup, destination: "kids");
        entry.PresetOverride = "My Special Preset";
        var runner = new RequestRecordingRunner();

        await RunLaneAsync(OrchestratorFor(setup, runner), setup.Home, setup.Config, _tempDir, "20260101_000000", new List<ResumeEntry> { entry }, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        Assert.Equal("My Special Preset", Assert.Single(runner.Requests).PresetName);
    }

    [Fact]
    public async Task LaneAssignment_SourceCleanupStopsAtTheHomeLanesInputRoot_NeverTheDestinationsOrAnAncestor()
    {
        // The exact data-loss scenario from the abandoned 2.1 attempt: the cleanup boundary was taken from
        // the NEW lane, so the empty-folder cascade could climb out of the file's own lane and delete real
        // folders. The boundary must always be the home lane's Input root.
        var setup = BuildTwoLanes();
        var (sourcePath, sourceFolder, entry) = QueueMovie(setup, destination: "kids");
        File.WriteAllText(Path.Combine(setup.KidsInput, "keep-me.txt"), "someone else's file in the destination lane's Input");

        await RunLaneAsync(OrchestratorFor(setup, new FakeProcessRunner()), setup.Home, setup.Config, _tempDir, "20260101_000000", new List<ResumeEntry> { entry }, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        Assert.False(File.Exists(sourcePath));              // the encoded source was cleaned up...
        Assert.False(Directory.Exists(sourceFolder));       // ...and so was its now-empty subfolder
        Assert.True(Directory.Exists(setup.HomeInput));     // but never the home lane's Input root, even though it is now empty
        Assert.True(Directory.Exists(setup.KidsInput));     // nor the destination lane's
        Assert.True(File.Exists(Path.Combine(setup.KidsInput, "keep-me.txt")));
        Assert.True(Directory.Exists(_tempDir));            // nor anything above
    }

    [Fact]
    public async Task LaneAssignment_CompanionFilesLandWithTheVideoInTheDestinationLibrary()
    {
        var setup = BuildTwoLanes();
        var (_, sourceFolder, entry) = QueueMovie(setup, destination: "kids");
        File.WriteAllText(Path.Combine(sourceFolder, "Sesame Street (1969).srt"), "subtitles");

        await RunLaneAsync(OrchestratorFor(setup, new FakeProcessRunner()), setup.Home, setup.Config, _tempDir, "20260101_000000", new List<ResumeEntry> { entry }, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(setup.KidsMovies, "Sesame Street (1969)", "Sesame Street (1969).srt")));
        Assert.Empty(Directory.GetFileSystemEntries(setup.HomeMovies));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("hdsd")] // assigned to its own lane: means nothing
    public async Task LaneAssignment_NoAssignmentOrOneToItsOwnLane_LandsNormallyAndIsNotReportedAsARedirect(string? destination)
    {
        var setup = BuildTwoLanes();
        var (_, _, entry) = QueueMovie(setup, destination);

        var results = await RunLaneAsync(OrchestratorFor(setup, new FakeProcessRunner()), setup.Home, setup.Config, _tempDir, "20260101_000000", new List<ResumeEntry> { entry }, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.True(File.Exists(Path.Combine(setup.HomeMovies, "Sesame Street (1969)", "Sesame Street (1969).mkv")));
        Assert.Null(result.RedirectedToLaneId);
        Assert.Null(result.RedirectedToLaneName);
        Assert.Equal("SD-HD", result.HomeLaneName);
    }

    [Fact]
    public async Task LaneAssignment_WithMoveFilesOff_TheFileRestsInOutputAndIsNotReportedAsRedirected()
    {
        var setup = BuildTwoLanes(moveFiles: false);
        var (_, _, entry) = QueueMovie(setup, destination: "kids");

        var results = await RunLaneAsync(OrchestratorFor(setup, new FakeProcessRunner()), setup.Home, setup.Config, _tempDir, "20260101_000000", new List<ResumeEntry> { entry }, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        Assert.Null(Assert.Single(results).RedirectedToLaneId); // it landed in no library at all
    }

    [Fact]
    public async Task LaneAssignment_ADeletedDestinationLane_HoldsTheFileInOutput_NeverFallingBackToTheHomeLibrary()
    {
        var setup = BuildTwoLanes();
        var (sourcePath, _, entry) = QueueMovie(setup, destination: "kids");
        setup.Config.Lanes.Remove(setup.Kids); // the destination lane is deleted before the encode finishes
        var orchestrator = OrchestratorFor(setup, new FakeProcessRunner());
        var resumeState = new List<ResumeEntry> { entry };
        var resumePath = Path.Combine(_tempDir, "resume.json");

        var results = await RunLaneAsync(orchestrator, setup.Home, setup.Config, _tempDir, "20260101_000000", resumeState, resumePath, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.False(result.Success);
        Assert.Equal("Destination lane no longer exists, move skipped", result.FailureReason);
        Assert.Equal(ResumeStatus.MoveFailed, entry.Status);
        Assert.True(File.Exists(entry.EncodedFilePath));                                  // the finished encode is safe
        Assert.Equal(setup.HomeOutput, Path.GetDirectoryName(entry.EncodedFilePath));     // ...in Output
        Assert.Empty(Directory.GetFileSystemEntries(setup.HomeMovies));                   // and NOT misfiled in the home library
        Assert.True(File.Exists(sourcePath));                                             // the source is untouched until it really lands

        // Every later pass keeps holding it: still no home library, still MoveFailed.
        await RunLaneAsync(orchestrator, setup.Home, setup.Config, _tempDir, "20260101_000100", resumeState, resumePath, CancellationToken.None);
        Assert.Equal(ResumeStatus.MoveFailed, entry.Status);
        Assert.Empty(Directory.GetFileSystemEntries(setup.HomeMovies));

        // The user picks a real destination again: the held file lands there on the next retry.
        setup.Config.Lanes.Add(setup.Kids);
        await RunLaneAsync(orchestrator, setup.Home, setup.Config, _tempDir, "20260101_000200", resumeState, resumePath, CancellationToken.None);
        Assert.Equal(ResumeStatus.Completed, entry.Status);
        Assert.True(File.Exists(Path.Combine(setup.KidsMovies, "Sesame Street (1969)", "Sesame Street (1969).mkv")));
        Assert.Empty(Directory.GetFileSystemEntries(setup.HomeMovies));
    }

    [Fact]
    public async Task LaneAssignment_AFailedMoveIsRetriedIntoTheDestinationLane_NotTheHomeLane()
    {
        var setup = BuildTwoLanes();
        var (_, _, entry) = QueueMovie(setup, destination: "kids");
        var reachable = setup.Kids.MovieBasePath;
        setup.Kids.MovieBasePath = @"Z:\Unavailable\KidsMovies"; // the destination library is offline at first
        var orchestrator = OrchestratorFor(setup, new FakeProcessRunner());
        var resumeState = new List<ResumeEntry> { entry };
        var resumePath = Path.Combine(_tempDir, "resume.json");

        await RunLaneAsync(orchestrator, setup.Home, setup.Config, _tempDir, "20260101_000000", resumeState, resumePath, CancellationToken.None);
        Assert.Equal(ResumeStatus.MoveFailed, entry.Status);
        Assert.Equal("kids", entry.DestinationLaneId); // the assignment survives the failure

        setup.Kids.MovieBasePath = reachable; // ...and comes back
        await RunLaneAsync(orchestrator, setup.Home, setup.Config, _tempDir, "20260101_000100", resumeState, resumePath, CancellationToken.None);

        Assert.Equal(ResumeStatus.Completed, entry.Status);
        Assert.True(File.Exists(Path.Combine(reachable, "Sesame Street (1969)", "Sesame Street (1969).mkv")));
        Assert.Empty(Directory.GetFileSystemEntries(setup.HomeMovies));
    }

    [Fact]
    public async Task LaneAssignment_ADisabledDestinationLaneStillWorks()
    {
        var setup = BuildTwoLanes();
        setup.Kids.Enabled = false; // its paths still exist
        var (_, _, entry) = QueueMovie(setup, destination: "kids");

        await RunLaneAsync(OrchestratorFor(setup, new FakeProcessRunner()), setup.Home, setup.Config, _tempDir, "20260101_000000", new List<ResumeEntry> { entry }, Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(setup.KidsMovies, "Sesame Street (1969)", "Sesame Street (1969).mkv")));
    }

    [Theory]
    [InlineData(DeleteAfterConvertMode.Delete, true)]
    [InlineData(DeleteAfterConvertMode.Maintain, false)]
    public async Task Passthrough_CopiesToOutput_NeverStartsTheEncoder_AndTheSourceSettingStillApplies(DeleteAfterConvertMode mode, bool sourceIsRemoved)
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);
        var source = Path.Combine(inputDir, "file1.mkv");
        File.WriteAllText(source, "source bytes");

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = outputDir, MoviePreset = PassthroughPreset.Name };
        var config = BuildConfig(mode);
        config.Lanes.Add(lane);
        var configStore = new SwitchingConfigStore(config, config, switchOnCall: int.MaxValue);
        var encoder = new FakeProcessRunner();
        var trash = new RecordingTrashService();

        var orchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            encoder, new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            trash, new NoOpRunLogger(), new NoOpResumeStateStore(), new NoOpProgressReporter(), configStore);

        var results = await RunLaneAsync(orchestrator, lane, config, _tempDir, "20260101_000000", new List<ResumeEntry>(), Path.Combine(_tempDir, "resume.json"), CancellationToken.None);

        var result = Assert.Single(results);
        Assert.True(result.Success);
        Assert.True(result.IsPassthrough);
        Assert.Equal("passthrough", result.EncoderLabel);
        Assert.Equal(0, encoder.CallCount);                                              // the encoder was never started
        var copy = Assert.Single(Directory.GetFiles(outputDir));
        Assert.EndsWith(".mkv", copy);                                                   // keeps the source's own extension
        Assert.Equal("source bytes", File.ReadAllText(copy));
        Assert.Equal(result.BeginSizeGb, result.EndSizeGb);                              // nothing saved
        // The Delete / Maintain choice governs the source exactly as it does after an encode.
        Assert.Equal(sourceIsRemoved ? 1 : 0, trash.Calls.Count(c => c.Path == source));
    }
}
