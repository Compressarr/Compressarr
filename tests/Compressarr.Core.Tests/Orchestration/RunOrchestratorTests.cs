using Compressarr.Core.Arr;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.FileBot;
using Compressarr.Core.Logging;
using Compressarr.Core.Notifications;
using Compressarr.Core.Orchestration;
using Compressarr.Core.Presets;
using Compressarr.Core.Reporting;
using Compressarr.Core.Routing;
using Compressarr.Core.Scheduling;

namespace Compressarr.Core.Tests.Orchestration;

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

file sealed class NoOpFileBotRunner : IFileBotRunner
{
    public FileBotRunResult Run(FileBotSettings settings, string inputPath, IReadOnlyList<string> vidTypes, IRunLogger logger) => FileBotRunResult.Empty;
}

file sealed class FixedExtensionPresetService : IEncoderPresetService
{
    public IReadOnlyList<string> GetPresetNames() => Array.Empty<string>();
    public bool PresetExists(string presetName) => true;
    public string GetOutputExtension(string presetName, out string? warning) { warning = null; return ".mkv"; }
    public string PreparePresetSource() => "presets.json";
}

/// <summary>Simulates HandBrakeCLI by writing a real (tiny) output file, and records every source
/// path it was asked to encode, in call order - the only way to observe the TRUE global processing
/// order across lanes, since RunResult.Report.Lanes groups results back by lane afterward and can't
/// tell you the real interleaved timeline on its own.</summary>
file sealed class RecordingProcessRunner : IEncoderRunner
{
    public List<string> ProcessedInOrder { get; } = new();
    private readonly Action<string>? _onEachRun;

    public RecordingProcessRunner(Action<string>? onEachRun = null) => _onEachRun = onEachRun;

    public Task<EncodeResult> RunAsync(EncodeRequest request, Action<EncodeProgress>? onProgress, CancellationToken cancellationToken)
    {
        ProcessedInOrder.Add(Path.GetFileName(request.SourcePath));
        _onEachRun?.Invoke(request.SourcePath);
        File.WriteAllText(request.OutputPath, "fake encoded output");
        return Task.FromResult(new EncodeResult(Success: true, DetailLogFile: request.DetailLogFile));
    }
}

/// <summary>Simulates HandBrakeCLI failing every encode (no output file written).</summary>
file sealed class FailingProcessRunner : IEncoderRunner
{
    public Task<EncodeResult> RunAsync(EncodeRequest request, Action<EncodeProgress>? onProgress, CancellationToken cancellationToken) =>
        Task.FromResult(new EncodeResult(Success: false, DetailLogFile: request.DetailLogFile));
}

file sealed class NoOpCompanionFileService : ICompanionFileService
{
    public void MoveCompanionFiles(string originalFileFullName, string originalFileDirectory, string routedVideoDestPath, DeleteAfterConvertMode deleteAfterConvert, IReadOnlyList<string> companionExtensions, DeleteAfterConvertMode unmatchedCompanionAction, DestinationCollisionMode collisionMode = DestinationCollisionMode.Overwrite) { }
    public void CleanUpEmptySourceFolder(string originalFileDirectory, string inputRoot, IReadOnlyList<string> vidTypes, DeleteAfterConvertMode deleteAfterConvert, DeleteAfterConvertMode unmatchedCompanionAction) { }
}

file sealed class NoOpArrUnmonitorService : IArrUnmonitorService
{
    public Task<ArrUnmonitorResult> UnmonitorAsync(CompressarrConfig config, string fileName, bool isTv, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ArrUnmonitorResult(null, ArrRescanOutcome.NotEnabled));
}

file sealed class NoOpTrashService : ITrashService
{
    public void DeleteFile(string path, DeleteAfterConvertMode mode) { }
    public void DeleteFolder(string path, DeleteAfterConvertMode mode) { }
}

/// <summary>Mirrors FileRunLogger's own LogProblem/HasLaneProblemsChanged dedup logic for real
/// (not just a no-op stub) - needed so tests can actually exercise the "unchanged persistent lane
/// problem doesn't force a fresh report on every pass" fix across multiple RunOnceAsync calls
/// against the SAME logger instance, the same way the real app relies on this state surviving
/// across polls.</summary>
file sealed class NoOpRunLogger : IRunLogger
{
    public event Action<string, LogSeverity>? LineWritten { add { } remove { } }
    public bool HasLoggedError { get; private set; }

    /// <summary>Every message this logger has ever received, in order - lets a test assert on
    /// specific log text (e.g. the PostExec timeout/abort message) instead of just HasLoggedError's
    /// bare yes/no.</summary>
    public List<(string Message, LogSeverity Severity)> Messages { get; } = new();

    private readonly Dictionary<string, string> _lastProblemMessages = new();
    private readonly Dictionary<string, string> _lastReportedLaneProblems = new();
    public string Initialize(string logFilePath, string timestamp) { HasLoggedError = false; return Path.GetTempFileName(); }
    public void Log(string message, LogSeverity severity = LogSeverity.Info)
    {
        if (severity == LogSeverity.Error) HasLoggedError = true;
        Messages.Add((message, severity));
    }
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

file sealed class NoOpHistoryStore : IRunHistoryStore
{
    public List<RunHistoryRecord> Appended { get; } = new();
    public void AppendRun(string logFilePath, RunHistoryRecord record) => Appended.Add(record);
    public IReadOnlyList<RunHistoryRecord> GetHistory(string logFilePath) => Array.Empty<RunHistoryRecord>();
    public int GetRunCount(string runCountPath) => 0;
    public void IncrementRunCount(string runCountPath) { }
}

file sealed class NoOpHistoryRollupCalculator : IHistoryRollupCalculator
{
    public (HistoryRollup Today, HistoryRollup ThisMonth, HistoryRollup ThisYear) Calculate(string logFilePath) =>
        (new HistoryRollup(0, 0, 0), new HistoryRollup(0, 0, 0), new HistoryRollup(0, 0, 0));
}

file sealed class NoOpReportLauncher : IReportLauncher
{
    public void Open(string reportFilePath) { }
}

file sealed class NoOpNotificationDispatcher : INotificationDispatcher
{
    public Task DispatchAsync(NotificationSettings settings, NotificationEvent evt, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>IConfigStore is only used by ConversionOrchestrator's own mid-file reload (not by these
/// tests, which never change config mid-run) - always returning the same fixed config passed in at
/// construction is enough, and matches RunOnceAsync's own real config as the same reference these
/// tests already build and pass to it.</summary>
file sealed class StaticConfigStore : IConfigStore
{
    private readonly CompressarrConfig _config;
    public StaticConfigStore(CompressarrConfig config) => _config = config;
    public CompressarrConfig Load(string path) => _config;
    public void Save(CompressarrConfig config, string path) { }
    public T Update<T>(string path, Func<CompressarrConfig, T> mutate) => mutate(_config);
}

file sealed class ScriptedSchedule : IEncodeSchedule
{
    public ScheduleStatus Status { get; set; } = new(false, false, null, false, false, false, null);
    public bool Held { set => Status = value ? new(true, true, EncodePriority.BelowNormal, true, true, false, DateTimeOffset.Now.AddHours(8)) : new(true, false, EncodePriority.Normal, true, false, false, null); }
    public ScheduleStatus GetStatus() => Status;
    public ScheduleStatus GetStatus(CompressarrConfig config) => Status;
    public void RunAnyway() => Held = false;
    public DateTime? ProjectCompletion(double currentMinutes, double queuedMinutes) => null;
    public bool TryMarkHoldNoticed(ScheduleStatus status) => true;
    public void Start(IRunLoopController? runLoop = null) { }
    public Task StopAsync() => Task.CompletedTask;
    public void Tick() { }
}

/// <summary>Covers RunOrchestrator's global, cross-lane processing loop - added alongside the
/// cross-lane priority queue feature, since this exact loop (previously "drain lane 1 fully, then
/// drain lane 2") had zero dedicated regression coverage before this: ConversionOrchestratorTests
/// only ever drives a single lane, and RunLoopControllerTests mocks IRunOrchestrator entirely.
/// Uses the real ConversionOrchestrator (not mocked) so these tests exercise the actual
/// PrepareLane/ProcessOneFileAsync split RunOrchestrator drives in production.</summary>
[Collection("AppDataOverride")]
public class RunOrchestratorTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("compressarr-run-orchestrator-tests-").FullName;

    public RunOrchestratorTests()
    {
        // RunOrchestrator.RunOnceAsync reads/writes resume.json (and would read settings.json/
        // runcount.json too, if these tests ever triggered that code) via AppPaths.* internally -
        // they are NOT parameters it accepts, unlike ConversionOrchestrator's own methods. Without
        // this override, a real test run here would read and write THIS MACHINE's actual
        // production Compressarr data - confirmed the hard way once already (a prior draft of this
        // file polluted the real resume.json with temp-path test entries before this override was
        // added; cleaned up by hand afterward).
        AppPaths.TestOverrideAppDataDirectory = _tempDir;
    }

    public void Dispose()
    {
        AppPaths.TestOverrideAppDataDirectory = null;
        Directory.Delete(_tempDir, recursive: true);
    }

    // Returns (RunOrchestrator, string ResumeFilePath) only - a file-local type like
    // RecordingProcessRunner can't appear in a member signature of this non-file-local test class,
    // even a private one, so the caller constructs it and passes it in instead of getting it back.
    private (RunOrchestrator Orchestrator, string ResumeFilePath) BuildOrchestrator(
        CompressarrConfig config, IEncoderRunner processRunner, IResumeStateStore? resumeStore = null,
        IRunLogger? logger = null, TimeSpan? postExecTimeout = null, IEncodeSchedule? schedule = null, IRunHistoryStore? historyStore = null,
        IEncoderResolver? encoders = null)
    {
        // HandBrakeCLI/presets "paths" just need to exist on disk for PathExists to pass -
        // PassThroughPathExpander does no real expansion, and FixedExtensionPresetService never
        // actually reads presetsPath's contents. Tests are expected to have already set
        // config.HandBrake.CliPath/PresetsPath to these same real-on-disk paths.
        File.WriteAllText(config.HandBrake.CliPath, "fake");
        File.WriteAllText(config.HandBrake.PresetsPath, "{}");

        // Must match what RunOrchestrator.RunOnceAsync itself resolves internally
        // (AppPaths.GetResumeFilePath(), now rooted at _tempDir via the override above) - not an
        // independently-chosen path - so the ConversionOrchestrator side (which takes this as an
        // explicit parameter) and the RunOrchestrator side (which doesn't) agree on the same file.
        var resumeFilePath = AppPaths.GetResumeFilePath();
        var effectiveResumeStore = resumeStore ?? new JsonResumeStateStore();
        // One shared instance for both orchestrators, matching real DI (IRunLogger is a
        // singleton) - RunOrchestrator's own HasLoggedError check needs to see errors logged
        // from deep inside ConversionOrchestrator too, the same as it does in production.
        var effectiveLogger = logger ?? new NoOpRunLogger();
        var effectiveHistory = historyStore ?? new NoOpHistoryStore();
        var effectiveSchedule = schedule ?? new EncodeSchedule(new StaticConfigStore(config), new ActiveEncodeProcess());

        var conversionOrchestrator = new ConversionOrchestrator(
            new PassThroughPathExpander(), new RealFolderScanner(), new NoOpFileBotRunner(), new FixedExtensionPresetService(), new MetadataService(),
            processRunner, new FileRouter(), new NoOpCompanionFileService(), new NoOpArrUnmonitorService(),
            new NoOpTrashService(), effectiveLogger, effectiveResumeStore, new NoOpProgressReporter(),
            new StaticConfigStore(config), encoders);

        // postExecTimeout only ever non-null in the PostExec timeout test below - resolves the
        // internal test-only constructor overload; every other test keeps using the real
        // production (public) constructor and its real 5-minute default.
        var runOrchestrator = postExecTimeout is { } timeout
            ? new RunOrchestrator(
                new PassThroughPathExpander(), new FixedExtensionPresetService(), conversionOrchestrator, new MetadataService(),
                effectiveResumeStore, effectiveLogger, effectiveHistory, new NoOpHistoryRollupCalculator(),
                new Compressarr.Core.Reporting.HtmlReportGenerator(), new NoOpReportLauncher(), new NoOpNotificationService(), new NoOpNotificationDispatcher(),
                new NoOpTrashService(), new NoOpProgressReporter(), new ActiveRunController(), effectiveSchedule, timeout, encoders)
            : new RunOrchestrator(
                new PassThroughPathExpander(), new FixedExtensionPresetService(), conversionOrchestrator, new MetadataService(),
                effectiveResumeStore, effectiveLogger, effectiveHistory, new NoOpHistoryRollupCalculator(),
                new Compressarr.Core.Reporting.HtmlReportGenerator(), new NoOpReportLauncher(), new NoOpNotificationService(), new NoOpNotificationDispatcher(),
                new NoOpTrashService(), new NoOpProgressReporter(), new ActiveRunController(), effectiveSchedule, encoders);

        return (runOrchestrator, resumeFilePath);
    }

    private static LaneConfig MakeLane(string id, string displayName, string inputDir, string outputDir) => new()
    {
        Id = id,
        DisplayName = displayName,
        Enabled = true,
        Input = inputDir,
        Output = outputDir,
        MoviePreset = "Any Preset"
    };

    [Fact]
    public async Task RunOnceAsync_CrossLaneOrder_InterleavesAcrossLanes_NotLaneByLane()
    {
        var laneAInput = Path.Combine(_tempDir, "LaneA_Input");
        var laneAOutput = Path.Combine(_tempDir, "LaneA_Output");
        var laneBInput = Path.Combine(_tempDir, "LaneB_Input");
        var laneBOutput = Path.Combine(_tempDir, "LaneB_Output");
        Directory.CreateDirectory(laneAInput);
        Directory.CreateDirectory(laneAOutput);
        Directory.CreateDirectory(laneBInput);
        Directory.CreateDirectory(laneBOutput);

        var aFile1 = Path.Combine(laneAInput, "a-file1.mkv");
        var aFile2 = Path.Combine(laneAInput, "a-file2.mkv");
        var bFile1 = Path.Combine(laneBInput, "b-file1.mkv");
        var bFile2 = Path.Combine(laneBInput, "b-file2.mkv");
        File.WriteAllText(aFile1, "1");
        File.WriteAllText(aFile2, "2");
        File.WriteAllText(bFile1, "3");
        File.WriteAllText(bFile2, "4");

        var laneA = MakeLane("laneA", "Lane A", laneAInput, laneAOutput);
        var laneB = MakeLane("laneB", "Lane B", laneBInput, laneBOutput);

        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.HandBrake.CliPath = Path.Combine(_tempDir, "HandBrakeCLI.exe");
        config.HandBrake.PresetsPath = Path.Combine(_tempDir, "presets.json");
        // Defaults are literal "%CompressarrAppData%\..." tokens - PassThroughPathExpander doesn't
        // resolve them, so left unset these would create a stray relative folder (with a literal %
        // in its name) in the test runner's own working directory instead of nowhere real, but
        // still outside this test's controlled temp dir.
        config.Logging.LogFilePath = Path.Combine(_tempDir, "Logs");
        config.Report.ReportPath = Path.Combine(_tempDir, "Reports");
        config.Lanes.Add(laneA);
        config.Lanes.Add(laneB);

        var processRunner = new RecordingProcessRunner();
        var (orchestrator, resumeFilePath) = BuildOrchestrator(config, processRunner);

        // Explicit global interleave: B1 (0), A1 (1), B2 (2), A2 (3) - deliberately NOT grouped by
        // lane, so lane-by-lane processing (the old behavior) would produce a different observed
        // order than this.
        var resumeState = new List<ResumeEntry>
        {
            new() { LaneId = "laneA", FullName = aFile1, Status = ResumeStatus.Pending, Order = 1 },
            new() { LaneId = "laneA", FullName = aFile2, Status = ResumeStatus.Pending, Order = 3 },
            new() { LaneId = "laneB", FullName = bFile1, Status = ResumeStatus.Pending, Order = 0 },
            new() { LaneId = "laneB", FullName = bFile2, Status = ResumeStatus.Pending, Order = 2 }
        };
        new JsonResumeStateStore().Save(resumeState, resumeFilePath);

        var result = await orchestrator.RunOnceAsync(config);

        Assert.NotNull(result);
        Assert.Equal(4, result!.TotalFiles);
        Assert.Equal(new[] { "b-file1.mkv", "a-file1.mkv", "b-file2.mkv", "a-file2.mkv" }, processRunner.ProcessedInOrder);
    }

    private (CompressarrConfig Config, string Input) MakeSingleLaneConfig(params string[] fileNames)
    {
        var input = Path.Combine(_tempDir, "Single_Input");
        var output = Path.Combine(_tempDir, "Single_Output");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        foreach (var name in fileNames) File.WriteAllText(Path.Combine(input, name), "video");

        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.HandBrake.CliPath = Path.Combine(_tempDir, "HandBrakeCLI.exe");
        config.HandBrake.PresetsPath = Path.Combine(_tempDir, "presets.json");
        config.Logging.LogFilePath = Path.Combine(_tempDir, "Logs");
        config.Report.ReportPath = Path.Combine(_tempDir, "Reports");
        config.Lanes.Add(MakeLane("lane", "Lane", input, output));
        return (config, input);
    }

    [Fact]
    public async Task RunOnceAsync_HeldByTheSchedule_StartsNothing_ButStillTracksTheFilesInOrder()
    {
        var (config, input) = MakeSingleLaneConfig("b.mkv", "a.mkv");
        var processRunner = new RecordingProcessRunner();
        var schedule = new ScriptedSchedule { Held = true };
        var (orchestrator, resumeFilePath) = BuildOrchestrator(config, processRunner, schedule: schedule);

        var result = await orchestrator.RunOnceAsync(config);

        Assert.Empty(processRunner.ProcessedInOrder);
        Assert.Equal(0, result!.TotalFiles);
        var tracked = new JsonResumeStateStore().Load(resumeFilePath);
        Assert.Equal(2, tracked.Count);
        Assert.All(tracked, e => Assert.Equal(ResumeStatus.Pending, e.Status));
        Assert.Equal(new[] { "a.mkv", "b.mkv" }, tracked.OrderBy(e => e.Order).Select(e => Path.GetFileName(e.FullName)).ToArray());
    }

    [Fact]
    public async Task RunOnceAsync_TheDaytimeBeginsMidPass_FinishesTheFileInFlight_ThenHoldsTheNextOne()
    {
        var (config, _) = MakeSingleLaneConfig("a.mkv", "b.mkv", "c.mkv");
        var schedule = new ScriptedSchedule();
        // The window opens while the first file is encoding.
        var processRunner = new RecordingProcessRunner(onEachRun: _ => schedule.Held = true);
        var (orchestrator, resumeFilePath) = BuildOrchestrator(config, processRunner, schedule: schedule);

        var result = await orchestrator.RunOnceAsync(config);

        Assert.Equal(new[] { "a.mkv" }, processRunner.ProcessedInOrder);
        Assert.Equal(1, result!.TotalFiles);
        var pending = new JsonResumeStateStore().Load(resumeFilePath).Where(e => e.Status == ResumeStatus.Pending).ToList();
        Assert.Equal(2, pending.Count);
    }

    [Fact]
    public async Task RunOnceAsync_AHeldQueueThatIsReleased_ProcessesEverythingOnTheNextPass()
    {
        var (config, _) = MakeSingleLaneConfig("a.mkv", "b.mkv");
        var processRunner = new RecordingProcessRunner();
        var schedule = new ScriptedSchedule { Held = true };
        var (orchestrator, _) = BuildOrchestrator(config, processRunner, schedule: schedule);
        await orchestrator.RunOnceAsync(config);
        Assert.Empty(processRunner.ProcessedInOrder);

        schedule.RunAnyway();
        var result = await orchestrator.RunOnceAsync(config);

        Assert.Equal(new[] { "a.mkv", "b.mkv" }, processRunner.ProcessedInOrder);
        Assert.Equal(2, result!.TotalFiles);
    }

    [Fact]
    public async Task RunOnceAsync_AFileAssignedToAnotherLane_LandsThere_AndTheRunRecordsAndReportsTheRedirect()
    {
        var homeInput = Path.Combine(_tempDir, "HomeInput");
        var homeLibrary = Path.Combine(_tempDir, "HomeLibrary");
        var kidsLibrary = Path.Combine(_tempDir, "KidsLibrary");
        var folder = Path.Combine(homeInput, "Sesame Street (1969)");
        foreach (var d in new[] { folder, homeLibrary, kidsLibrary, Path.Combine(_tempDir, "HomeOutput"), Path.Combine(_tempDir, "KidsInput"), Path.Combine(_tempDir, "KidsOutput") }) Directory.CreateDirectory(d);
        var source = Path.Combine(folder, "Sesame Street (1969).mkv");
        File.WriteAllText(source, "video");

        var home = MakeLane("hdsd", "SD-HD", homeInput, Path.Combine(_tempDir, "HomeOutput"));
        home.MovieBasePath = homeLibrary;
        var kids = MakeLane("kids", "Kids", Path.Combine(_tempDir, "KidsInput"), Path.Combine(_tempDir, "KidsOutput"));
        kids.MovieBasePath = kidsLibrary;
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = true, ClearTitleMetadata = false, DeleteAfterConvert = DeleteAfterConvertMode.Delete } };
        config.HandBrake.CliPath = Path.Combine(_tempDir, "HandBrakeCLI.exe");
        config.HandBrake.PresetsPath = Path.Combine(_tempDir, "presets.json");
        config.Logging.LogFilePath = Path.Combine(_tempDir, "Logs");
        config.Report.ReportPath = Path.Combine(_tempDir, "Reports");
        config.Lanes.Add(home);
        config.Lanes.Add(kids);

        var history = new NoOpHistoryStore();
        var (orchestrator, resumeFilePath) = BuildOrchestrator(config, new RecordingProcessRunner(), historyStore: history);
        new JsonResumeStateStore().Save(new List<ResumeEntry>
        {
            new() { LaneId = "hdsd", FullName = source, Status = ResumeStatus.Pending, Order = 0, DestinationLaneId = "kids" }
        }, resumeFilePath);

        var result = await orchestrator.RunOnceAsync(config);

        Assert.Equal(1, result!.TotalFiles);
        Assert.True(File.Exists(Path.Combine(kidsLibrary, "Sesame Street (1969)", "Sesame Street (1969).mkv")));
        Assert.Empty(Directory.GetFileSystemEntries(homeLibrary));
        Assert.Equal(1, Assert.Single(history.Appended).RedirectCount);

        var reportHtml = File.ReadAllText(Assert.Single(Directory.GetFiles(config.Report.ReportPath, "*.html")));
        Assert.Contains("Landed in Kids - redirected from SD-HD", reportHtml);
        Assert.Contains("1 file(s) redirected to a different lane", reportHtml);
    }

    [Fact]
    public async Task RunOnceAsync_WithNoRedirects_RecordsAZeroRedirectCount()
    {
        var (config, _) = MakeSingleLaneConfig("a.mkv");
        var history = new NoOpHistoryStore();
        var (orchestrator, _) = BuildOrchestrator(config, new RecordingProcessRunner(), historyStore: history);

        await orchestrator.RunOnceAsync(config);

        Assert.Equal(0, Assert.Single(history.Appended).RedirectCount);
    }

    [Fact]
    public async Task RunOnceAsync_AFailedEncode_IsLeftOutOfTheHistoryTotals_ButStillRecordedAsAnError()
    {
        // A failed encode used to be recorded as a file with its size "before" and nothing "after" - every
        // failure showed as 100% saved and inflated the History totals (a file failing every pass piled up
        // hundreds of them).
        var (config, _) = MakeSingleLaneConfig("a.mkv");
        var history = new NoOpHistoryStore();
        var (orchestrator, _) = BuildOrchestrator(config, new FailingProcessRunner(), historyStore: history);

        await orchestrator.RunOnceAsync(config);

        var record = Assert.Single(history.Appended); // the run still has a report and a run number
        Assert.Equal(0, record.FileCount);
        Assert.Equal(0, record.BeginSizeGb);
        Assert.Equal(0, record.EndSizeGb);
        Assert.Equal(1, record.ErrorCount);
    }

    [Fact]
    public async Task RunOnceAsync_WhenEveryFileCompleted_DeletesTheWholeResumeFile()
    {
        // Characterization (2.1.8): resume.json is only kept while something is still outstanding.
        // Once every tracked entry is Completed, the file - including the Completed history - is
        // deleted at the end of the pass.
        var (config, _) = MakeSingleLaneConfig("a.mkv", "b.mkv");
        var (orchestrator, resumeFilePath) = BuildOrchestrator(config, new RecordingProcessRunner());

        var result = await orchestrator.RunOnceAsync(config);

        Assert.Equal(2, result!.TotalFiles);
        Assert.False(File.Exists(resumeFilePath));
    }

    [Fact]
    public async Task RunOnceAsync_WithMaintain_ASourceThatWasConverted_IsNotConvertedAgainOnTheNextPass()
    {
        // Maintain leaves every source in Input. Before, the finished entry was wiped at the end of the pass and the file
        // was found again as brand new, so the same file was converted on every single pass.
        var (config, input) = MakeSingleLaneConfig("a.mkv");
        config.Processing.DeleteAfterConvert = DeleteAfterConvertMode.Maintain;
        var processRunner = new RecordingProcessRunner();
        var (orchestrator, resumeFilePath) = BuildOrchestrator(config, processRunner);

        var first = await orchestrator.RunOnceAsync(config);
        var second = await orchestrator.RunOnceAsync(config);
        var third = await orchestrator.RunOnceAsync(config);

        Assert.Equal(1, first!.TotalFiles);
        Assert.Equal(0, second?.TotalFiles ?? 0);
        Assert.Equal(0, third?.TotalFiles ?? 0);
        Assert.Single(processRunner.ProcessedInOrder);
        Assert.True(File.Exists(Path.Combine(input, "a.mkv"))); // the source is still there, as asked
        var remembered = Assert.Single(new JsonResumeStateStore().Load(resumeFilePath));
        Assert.Equal(ResumeStatus.Completed, remembered.Status);
    }

    [Fact]
    public async Task RunOnceAsync_WithMaintain_AFileReplacedByADifferentOne_IsConvertedAgain_AndAForgottenSourceIsForgotten()
    {
        var (config, input) = MakeSingleLaneConfig("a.mkv");
        config.Processing.DeleteAfterConvert = DeleteAfterConvertMode.Maintain;
        var processRunner = new RecordingProcessRunner();
        var (orchestrator, resumeFilePath) = BuildOrchestrator(config, processRunner);
        await orchestrator.RunOnceAsync(config);

        File.WriteAllText(Path.Combine(input, "a.mkv"), "a different, longer video than before"); // replaced by another file
        var again = await orchestrator.RunOnceAsync(config);

        Assert.Equal(1, again!.TotalFiles);
        Assert.Equal(2, processRunner.ProcessedInOrder.Count);

        File.Delete(Path.Combine(input, "a.mkv")); // the user cleans the kept source up themselves
        await orchestrator.RunOnceAsync(config);
        Assert.False(File.Exists(resumeFilePath)); // nothing left to remember
    }

    [Fact]
    public async Task RunOnceAsync_WhenAFileFailed_KeepsTheResumeFileWithItsErrorEntry()
    {
        var (config, input) = MakeSingleLaneConfig("a.mkv");
        var (orchestrator, resumeFilePath) = BuildOrchestrator(config, new FailingProcessRunner());

        await orchestrator.RunOnceAsync(config);

        Assert.True(File.Exists(resumeFilePath));
        var entry = Assert.Single(new JsonResumeStateStore().Load(resumeFilePath));
        Assert.Equal(Path.Combine(input, "a.mkv"), entry.FullName);
        Assert.Equal(ResumeStatus.Error, entry.Status);
    }

    [Fact]
    public async Task RunOnceAsync_OneLaneHasNoEligibleFiles_OtherLaneStillProcessesFully()
    {
        var emptyLaneInput = Path.Combine(_tempDir, "Empty_Input");
        var emptyLaneOutput = Path.Combine(_tempDir, "Empty_Output");
        var busyLaneInput = Path.Combine(_tempDir, "Busy_Input");
        var busyLaneOutput = Path.Combine(_tempDir, "Busy_Output");
        Directory.CreateDirectory(emptyLaneInput);
        Directory.CreateDirectory(emptyLaneOutput);
        Directory.CreateDirectory(busyLaneInput);
        Directory.CreateDirectory(busyLaneOutput);

        var file1 = Path.Combine(busyLaneInput, "file1.mkv");
        var file2 = Path.Combine(busyLaneInput, "file2.mkv");
        File.WriteAllText(file1, "1");
        File.WriteAllText(file2, "2");

        var emptyLane = MakeLane("empty", "Empty Lane", emptyLaneInput, emptyLaneOutput);
        var busyLane = MakeLane("busy", "Busy Lane", busyLaneInput, busyLaneOutput);

        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.HandBrake.CliPath = Path.Combine(_tempDir, "HandBrakeCLI.exe");
        config.HandBrake.PresetsPath = Path.Combine(_tempDir, "presets.json");
        // Defaults are literal "%CompressarrAppData%\..." tokens - PassThroughPathExpander doesn't
        // resolve them, so left unset these would create a stray relative folder (with a literal %
        // in its name) in the test runner's own working directory instead of nowhere real, but
        // still outside this test's controlled temp dir.
        config.Logging.LogFilePath = Path.Combine(_tempDir, "Logs");
        config.Report.ReportPath = Path.Combine(_tempDir, "Reports");
        config.Lanes.Add(emptyLane);
        config.Lanes.Add(busyLane);

        var processRunner = new RecordingProcessRunner();
        var (orchestrator, _) = BuildOrchestrator(config, processRunner);

        var result = await orchestrator.RunOnceAsync(config);

        Assert.NotNull(result);
        Assert.Equal(2, result!.TotalFiles);
        Assert.Equal(new[] { "file1.mkv", "file2.mkv" }, processRunner.ProcessedInOrder);
    }

    [Fact]
    public async Task RunOnceAsync_NonContiguousLaneVisits_ReportGroupsResultsCorrectlyPerLane()
    {
        var laneAInput = Path.Combine(_tempDir, "LaneA_Input");
        var laneAOutput = Path.Combine(_tempDir, "LaneA_Output");
        var laneBInput = Path.Combine(_tempDir, "LaneB_Input");
        var laneBOutput = Path.Combine(_tempDir, "LaneB_Output");
        Directory.CreateDirectory(laneAInput);
        Directory.CreateDirectory(laneAOutput);
        Directory.CreateDirectory(laneBInput);
        Directory.CreateDirectory(laneBOutput);

        var aFile1 = Path.Combine(laneAInput, "a-file1.mkv");
        var aFile2 = Path.Combine(laneAInput, "a-file2.mkv");
        var bFile1 = Path.Combine(laneBInput, "b-file1.mkv");
        File.WriteAllText(aFile1, "1");
        File.WriteAllText(aFile2, "2");
        File.WriteAllText(bFile1, "3");

        var laneA = MakeLane("laneA", "Lane A", laneAInput, laneAOutput);
        var laneB = MakeLane("laneB", "Lane B", laneBInput, laneBOutput);

        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.HandBrake.CliPath = Path.Combine(_tempDir, "HandBrakeCLI.exe");
        config.HandBrake.PresetsPath = Path.Combine(_tempDir, "presets.json");
        // Defaults are literal "%CompressarrAppData%\..." tokens - PassThroughPathExpander doesn't
        // resolve them, so left unset these would create a stray relative folder (with a literal %
        // in its name) in the test runner's own working directory instead of nowhere real, but
        // still outside this test's controlled temp dir.
        config.Logging.LogFilePath = Path.Combine(_tempDir, "Logs");
        config.Report.ReportPath = Path.Combine(_tempDir, "Reports");
        config.Lanes.Add(laneA);
        config.Lanes.Add(laneB);

        var (orchestrator, resumeFilePath) = BuildOrchestrator(config, new RecordingProcessRunner());

        // Lane B's one file sandwiched between lane A's two - lane A is visited, then lane B, then
        // lane A again (non-contiguous).
        var resumeState = new List<ResumeEntry>
        {
            new() { LaneId = "laneA", FullName = aFile1, Status = ResumeStatus.Pending, Order = 0 },
            new() { LaneId = "laneB", FullName = bFile1, Status = ResumeStatus.Pending, Order = 1 },
            new() { LaneId = "laneA", FullName = aFile2, Status = ResumeStatus.Pending, Order = 2 }
        };
        new JsonResumeStateStore().Save(resumeState, resumeFilePath);

        var result = await orchestrator.RunOnceAsync(config);

        Assert.NotNull(result);
        var laneASection = result!.Report.Lanes.Single(l => l.LaneDisplayName == "Lane A");
        var laneBSection = result.Report.Lanes.Single(l => l.LaneDisplayName == "Lane B");
        Assert.Equal(new[] { "a-file1.mkv", "a-file2.mkv" }, laneASection.Results.Select(r => r.FileName));
        Assert.Equal(new[] { "b-file1.mkv" }, laneBSection.Results.Select(r => r.FileName));
    }

    [Fact]
    public async Task RunOnceAsync_CrossLaneReorderDuringEncode_IsHonoredNotLost()
    {
        // The most safety-critical scenario for this whole feature: a reorder landing while a file
        // is mid-encode must move a DIFFERENT lane's file ahead of one still queued in the
        // currently-encoding file's own lane - not just be honored within one lane (already covered
        // by ConversionOrchestratorTests' own single-lane version of this test).
        var laneAInput = Path.Combine(_tempDir, "LaneA_Input");
        var laneAOutput = Path.Combine(_tempDir, "LaneA_Output");
        var laneBInput = Path.Combine(_tempDir, "LaneB_Input");
        var laneBOutput = Path.Combine(_tempDir, "LaneB_Output");
        Directory.CreateDirectory(laneAInput);
        Directory.CreateDirectory(laneAOutput);
        Directory.CreateDirectory(laneBInput);
        Directory.CreateDirectory(laneBOutput);

        var episode1 = Path.Combine(laneAInput, "episode1.mkv");
        var episode2 = Path.Combine(laneAInput, "episode2.mkv");
        var movieA = Path.Combine(laneBInput, "movieA.mkv");
        File.WriteAllText(episode1, "1");
        File.WriteAllText(episode2, "2");
        File.WriteAllText(movieA, "3");

        var laneA = MakeLane("laneA", "Lane A", laneAInput, laneAOutput);
        var laneB = MakeLane("laneB", "Lane B", laneBInput, laneBOutput);

        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.HandBrake.CliPath = Path.Combine(_tempDir, "HandBrakeCLI.exe");
        config.HandBrake.PresetsPath = Path.Combine(_tempDir, "presets.json");
        // Defaults are literal "%CompressarrAppData%\..." tokens - PassThroughPathExpander doesn't
        // resolve them, so left unset these would create a stray relative folder (with a literal %
        // in its name) in the test runner's own working directory instead of nowhere real, but
        // still outside this test's controlled temp dir.
        config.Logging.LogFilePath = Path.Combine(_tempDir, "Logs");
        config.Report.ReportPath = Path.Combine(_tempDir, "Reports");
        config.Lanes.Add(laneA);
        config.Lanes.Add(laneB);

        // Must match AppPaths.GetResumeFilePath()'s real result (rooted at _tempDir via the
        // constructor's override) - a separately-computed path here was a real bug caught while
        // writing this test: it silently wrote the concurrent edit to a file RunOrchestrator never
        // actually reads from, making it look like the edit was never honored at all.
        var resumeFilePath = AppPaths.GetResumeFilePath();
        var realResumeStore = new JsonResumeStateStore();

        var initialState = new List<ResumeEntry>
        {
            new() { LaneId = "laneA", FullName = episode1, Status = ResumeStatus.Pending, Order = 0 },
            new() { LaneId = "laneA", FullName = episode2, Status = ResumeStatus.Pending, Order = 1 },
            new() { LaneId = "laneB", FullName = movieA, Status = ResumeStatus.Pending, Order = 2 }
        };
        realResumeStore.Save(initialState, resumeFilePath);

        // Fires while episode1 is still "encoding" - simulates POST /api/run/queue/reorder moving
        // lane B's movie to the very front, ahead of lane A's own still-queued episode2.
        var processRunner = new RecordingProcessRunner(sourcePath =>
        {
            if (Path.GetFileName(sourcePath) != "episode1.mkv") return;
            realResumeStore.Update(resumeFilePath, state =>
            {
                state.Single(e => e.FullName == movieA).Order = 0;
                state.Single(e => e.FullName == episode2).Order = 1;
                return true;
            });
        });
        var (orchestrator, _) = BuildOrchestrator(config, processRunner, realResumeStore);

        var result = await orchestrator.RunOnceAsync(config);

        Assert.NotNull(result);
        Assert.Equal(3, result!.TotalFiles);
        Assert.Equal(new[] { "episode1.mkv", "movieA.mkv", "episode2.mkv" }, processRunner.ProcessedInOrder);

        var finalState = realResumeStore.Load(resumeFilePath);
        Assert.All(finalState, e => Assert.Equal(ResumeStatus.Completed, e.Status));
    }

    // Real bug found live: with a normal monitoring poll interval, a pass that finds nothing new
    // (by far the most common outcome) still wrote a full summary log file AND a full HTML report
    // to disk every single time - over 8000 of each on one production machine, most of them
    // completely empty of anything worth seeing, making the genuinely useful ones hard to find.
    // History already correctly skipped an empty pass; log/report needed the same treatment.
    [Fact]
    public async Task RunOnceAsync_ZeroFilesAndNoError_DeletesTheSummaryLogFile_AndWritesNoReport()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);
        // Deliberately empty - this pass has nothing to do.

        var lane = MakeLane("lane1", "Test Lane", inputDir, outputDir);
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.HandBrake.CliPath = Path.Combine(_tempDir, "HandBrakeCLI.exe");
        config.HandBrake.PresetsPath = Path.Combine(_tempDir, "presets.json");
        config.Logging.LogFilePath = Path.Combine(_tempDir, "Logs");
        config.Report.ReportPath = Path.Combine(_tempDir, "Reports");
        config.Lanes.Add(lane);

        var logger = new Compressarr.Core.Logging.FileRunLogger();
        var (orchestrator, _) = BuildOrchestrator(config, new RecordingProcessRunner(), logger: logger);

        var result = await orchestrator.RunOnceAsync(config);

        Assert.NotNull(result);
        Assert.Equal(0, result!.TotalFiles);
        Assert.False(Directory.Exists(config.Report.ReportPath) && Directory.GetFiles(config.Report.ReportPath).Length > 0,
            "an empty pass should not write a report file");
        Assert.False(Directory.Exists(config.Logging.LogFilePath) && Directory.GetFiles(config.Logging.LogFilePath).Length > 0,
            "an empty, error-free pass should not leave a summary log file behind");
    }

    // Contrast case for the test above - a pass that processes 0 files because a lane is genuinely
    // misconfigured (missing preset, here) is a real diagnostic worth keeping, unlike a quiet
    // "nothing new" poll - its log must survive even though TotalFiles is also 0.
    [Fact]
    public async Task RunOnceAsync_ZeroFilesButErrorLogged_KeepsTheSummaryLogFile()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var lane = new LaneConfig
        {
            Id = "lane1",
            DisplayName = "Misconfigured Lane",
            Enabled = true,
            Input = inputDir,
            Output = outputDir
            // No TvPreset or MoviePreset - RunOrchestrator logs an Error and skips this lane
            // entirely, so the pass still ends with TotalFiles == 0.
        };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.HandBrake.CliPath = Path.Combine(_tempDir, "HandBrakeCLI.exe");
        config.HandBrake.PresetsPath = Path.Combine(_tempDir, "presets.json");
        config.Logging.LogFilePath = Path.Combine(_tempDir, "Logs");
        config.Report.ReportPath = Path.Combine(_tempDir, "Reports");
        config.Lanes.Add(lane);

        var logger = new Compressarr.Core.Logging.FileRunLogger();
        var (orchestrator, _) = BuildOrchestrator(config, new RecordingProcessRunner(), logger: logger);

        var result = await orchestrator.RunOnceAsync(config);

        Assert.NotNull(result);
        Assert.Equal(0, result!.TotalFiles);
        Assert.True(Directory.Exists(config.Logging.LogFilePath) && Directory.GetFiles(config.Logging.LogFilePath).Length > 0,
            "a pass that hit a real error should keep its summary log even with 0 files processed");
    }

    // Code-review finding (pre-release review, LOW-MEDIUM): a pass whose ONLY activity was a
    // successful MoveFailed retry never went through ProcessOneFileAsync (the encode already
    // happened on an earlier pass - only the move itself was retried), so TotalFiles stayed 0 and
    // this pass looked identical to a genuinely idle poll to both gates above - its own summary
    // log got deleted and no report was ever written, even though a real, previously-stranded file
    // was just successfully filed. RunResult.RetriesSucceeded (and RunOnceCoreAsync's report-write
    // gate/RunOnceAsync's log-keep gate both checking it) fixes this: real activity now survives
    // even when it produced no ConversionResult of its own.
    [Fact]
    public async Task RunOnceAsync_OnlyActivityIsSuccessfulMoveRetry_KeepsLogAndWritesReport()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        var movieBaseDir = Path.Combine(_tempDir, "Movies");
        Directory.CreateDirectory(movieBaseDir);
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);
        // Input deliberately empty - nothing new to encode this pass, only the retry below.

        var lane = MakeLane("lane1", "Test Lane", inputDir, outputDir);
        lane.MovieBasePath = movieBaseDir;

        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = true, ClearTitleMetadata = false } };
        config.HandBrake.CliPath = Path.Combine(_tempDir, "HandBrakeCLI.exe");
        config.HandBrake.PresetsPath = Path.Combine(_tempDir, "presets.json");
        config.Logging.LogFilePath = Path.Combine(_tempDir, "Logs");
        config.Report.ReportPath = Path.Combine(_tempDir, "Reports");
        config.Lanes.Add(lane);

        var logger = new Compressarr.Core.Logging.FileRunLogger();
        var (orchestrator, resumeFilePath) = BuildOrchestrator(config, new RecordingProcessRunner(), logger: logger);

        // Simulates an already-encoded file sitting in Output from an earlier pass, still needing
        // its move to the movie library retried (base path was unreachable back then, reachable now).
        var encodedFilePath = Path.Combine(outputDir, "Caddyshack (1980).compressarr-aaaaaaaa.mkv");
        File.WriteAllText(encodedFilePath, "already encoded");
        var resumeState = new List<ResumeEntry>
        {
            new()
            {
                LaneId = "lane1",
                FullName = Path.Combine(inputDir, "Caddyshack (1980).mkv"),
                Status = ResumeStatus.MoveFailed,
                EncodedFilePath = encodedFilePath,
                EncodedFileDesiredName = "Caddyshack (1980).mkv"
            }
        };
        new JsonResumeStateStore().Save(resumeState, resumeFilePath);

        var result = await orchestrator.RunOnceAsync(config);

        Assert.NotNull(result);
        Assert.Equal(0, result!.TotalFiles);
        Assert.Equal(1, result.RetriesSucceeded);
        Assert.True(File.Exists(Path.Combine(movieBaseDir, "Caddyshack (1980)", "Caddyshack (1980).mkv")), "the retried move should have actually landed the file");

        Assert.True(Directory.Exists(config.Logging.LogFilePath) && Directory.GetFiles(config.Logging.LogFilePath).Length > 0,
            "a pass whose only activity was a successful retry should keep its summary log, same as one that processed real files");
        Assert.True(Directory.Exists(config.Report.ReportPath) && Directory.GetFiles(config.Report.ReportPath).Length > 0,
            "a pass whose only activity was a successful retry should still get a report written");
    }

    // Same file-proliferation problem the test above guards against, but for a STANDING config
    // problem repeated every poll rather than a one-off - real gap found live: a persistently
    // misconfigured lane (missing preset, missing HandBrakeCLI, etc) logged Error on literally
    // every poll for as long as it stayed broken, which (combined with the fix above) meant a
    // kept log file every poll for the whole time it stayed broken. IRunLogger.LogProblem fixes
    // this the same way the per-file move-retry dedup does: Error only the first time (or if the
    // message changes), Info for an unchanged repeat, and back to Error again once the problem is
    // confirmed resolved (ClearProblem) and then recurs later - proven end-to-end here across 4
    // real passes through RunOrchestrator.
    [Fact]
    public async Task RunOnceAsync_StandingLaneConfigProblem_OnlyKeepsLogOnFirstOccurrenceAndOnRecurrenceAfterResolution()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = inputDir, Output = outputDir };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.HandBrake.CliPath = Path.Combine(_tempDir, "HandBrakeCLI.exe");
        config.HandBrake.PresetsPath = Path.Combine(_tempDir, "presets.json");
        config.Logging.LogFilePath = Path.Combine(_tempDir, "Logs");
        config.Report.ReportPath = Path.Combine(_tempDir, "Reports");
        config.Lanes.Add(lane);

        var logger = new Compressarr.Core.Logging.FileRunLogger();
        var (orchestrator, _) = BuildOrchestrator(config, new RecordingProcessRunner(), logger: logger);

        // A kept file from an earlier pass legitimately stays on disk forever (nothing retroactively
        // deletes it) - the real signal for "was THIS pass's own log kept or discarded" is whether a
        // NEW file appeared, not the running total. RunOnceAsync's timestamp has 1-second
        // resolution, so each pass needs its own tick to get a distinct filename.
        HashSet<string> Snapshot() => Directory.Exists(config.Logging.LogFilePath)
            ? Directory.GetFiles(config.Logging.LogFilePath).ToHashSet()
            : new HashSet<string>();

        // Pass 1: lane has no preset - first occurrence, kept (a new file appears).
        var before1 = Snapshot();
        await orchestrator.RunOnceAsync(config);
        var newFiles1 = Snapshot().Except(before1).ToList();
        Assert.Single(newFiles1);

        // Pass 2: still no preset, identical message - unchanged repeat, discarded (no new file).
        await Task.Delay(1100);
        var before2 = Snapshot();
        await orchestrator.RunOnceAsync(config);
        Assert.Empty(Snapshot().Except(before2));

        // Pass 3: fixed - no preset problem this time, and nothing else happened either, so this
        // pass's own log is also discarded (a genuinely quiet pass), but ClearProblem should have
        // run so a LATER recurrence isn't treated as "already known" any more.
        lane.MoviePreset = "Any Preset";
        await Task.Delay(1100);
        var before3 = Snapshot();
        await orchestrator.RunOnceAsync(config);
        Assert.Empty(Snapshot().Except(before3));

        // Pass 4: broken again, identical message to passes 1-2 - but since it was confirmed
        // resolved in between, this must be treated as new again (kept - a new file appears),
        // not silently swallowed as "still the same old problem."
        lane.MoviePreset = "";
        await Task.Delay(1100);
        var before4 = Snapshot();
        await orchestrator.RunOnceAsync(config);
        Assert.Single(Snapshot().Except(before4));
    }

    // Extends the fix above one step further: a misconfigured lane used to leave the pass with
    // TotalFiles == 0 and, before ReportErrorCode's 106+ values existed, no report at all - the
    // real reason was log-only, invisible to anyone who only checks the report (which can even
    // auto-open on error). A report must now be written even with zero files processed, and it
    // must actually show why.
    [Fact]
    public async Task RunOnceAsync_MisconfiguredLane_WritesAReportExplainingWhy_EvenWithZeroFilesProcessed()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        // No TvPreset/MoviePreset - the exact "1 lane problem(s)" scenario found live.
        var lane = new LaneConfig { Id = "lane1", DisplayName = "Broken Lane", Enabled = true, Input = inputDir, Output = outputDir };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.HandBrake.CliPath = Path.Combine(_tempDir, "HandBrakeCLI.exe");
        config.HandBrake.PresetsPath = Path.Combine(_tempDir, "presets.json");
        config.Logging.LogFilePath = Path.Combine(_tempDir, "Logs");
        config.Report.ReportPath = Path.Combine(_tempDir, "Reports");
        config.Lanes.Add(lane);

        var (orchestrator, _) = BuildOrchestrator(config, new RecordingProcessRunner());

        var result = await orchestrator.RunOnceAsync(config);

        Assert.NotNull(result);
        Assert.Equal(0, result!.TotalFiles);
        Assert.True(File.Exists(result.ReportFilePath), "a misconfigured lane must still produce a report, even with 0 files processed");
        var reportHtml = await File.ReadAllTextAsync(result.ReportFilePath);
        Assert.Contains("ERROR 106", reportHtml);
        Assert.Contains("1 lane problem(s)", reportHtml);
    }

    // Code-review finding (v2.1.4 review, #1 HIGH): LogProblem's own dedup only downgrades the
    // per-pass TEXT LOG line for an unchanged problem - it has no effect on whether the HTML
    // report gets written at all, since that gate only ever checked "does a problem currently
    // exist," not "is this the SAME problem as last time." A persistently misconfigured lane could
    // therefore still force a brand-new report on literally every single poll forever. Fixed via
    // IRunLogger.HasLaneProblemsChanged, the same per-key "changed since last check" memory
    // LogProblem already uses, applied one level up.
    [Fact]
    public async Task RunOnceAsync_SameUnchangedLaneProblemAcrossPolls_DoesNotWriteARepeatReport()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Broken Lane", Enabled = true, Input = inputDir, Output = outputDir };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.HandBrake.CliPath = Path.Combine(_tempDir, "HandBrakeCLI.exe");
        config.HandBrake.PresetsPath = Path.Combine(_tempDir, "presets.json");
        config.Logging.LogFilePath = Path.Combine(_tempDir, "Logs");
        config.Report.ReportPath = Path.Combine(_tempDir, "Reports");
        config.Lanes.Add(lane);

        // A real, shared IRunLogger across both passes - matching production, where the logger is
        // a singleton and this dedup memory has to survive from one poll to the next. Using the
        // default NoOpRunLogger() per call (as most other tests here do) would silently defeat this
        // exact test, since a fresh instance has no memory of "pass 1" by the time pass 2 runs.
        var sharedLogger = new NoOpRunLogger();
        var (orchestrator, _) = BuildOrchestrator(config, new RecordingProcessRunner(), logger: sharedLogger);

        var result1 = await orchestrator.RunOnceAsync(config);
        Assert.True(File.Exists(result1!.ReportFilePath), "the first pass with a new lane problem must still write a report");
        var reportsAfterPass1 = Directory.GetFiles(config.Report.ReportPath).Length;

        // Forces a genuinely different run timestamp (and so a different report filename) than
        // pass 1 - without this, a bug that DID try to write a second report could coincidentally
        // land on the exact same filename and get silently overwritten, making the file-count
        // assertion below pass even though the fix doesn't actually work.
        await Task.Delay(1100);

        var result2 = await orchestrator.RunOnceAsync(config);
        var reportsAfterPass2 = Directory.GetFiles(config.Report.ReportPath).Length;

        Assert.Equal(0, result2!.TotalFiles);
        Assert.Equal(reportsAfterPass1, reportsAfterPass2);
    }

    // Same scenario, but the lane problem clears in between (a preset gets configured) then
    // reappears - must be treated as new again once it comes back, not silently swallowed as
    // "already known" forever just because the SAME code (LaneNoPresetConfigured) was seen before.
    [Fact]
    public async Task RunOnceAsync_LaneProblemClearsThenReturns_WritesAFreshReportOnReturn()
    {
        var inputDir = Path.Combine(_tempDir, "Input");
        var outputDir = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(inputDir);
        Directory.CreateDirectory(outputDir);

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Broken Lane", Enabled = true, Input = inputDir, Output = outputDir };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.HandBrake.CliPath = Path.Combine(_tempDir, "HandBrakeCLI.exe");
        config.HandBrake.PresetsPath = Path.Combine(_tempDir, "presets.json");
        config.Logging.LogFilePath = Path.Combine(_tempDir, "Logs");
        config.Report.ReportPath = Path.Combine(_tempDir, "Reports");
        config.Lanes.Add(lane);

        var sharedLogger = new NoOpRunLogger();
        var (orchestrator, _) = BuildOrchestrator(config, new RecordingProcessRunner(), logger: sharedLogger);

        await orchestrator.RunOnceAsync(config);
        var reportsAfterPass1 = Directory.GetFiles(config.Report.ReportPath).Length;
        Assert.Equal(1, reportsAfterPass1);

        // Pass 2: fixed - no lane problem, and nothing else happened, so no report either (a
        // genuinely quiet pass) - but this clears the "last reported" memory back to empty.
        await Task.Delay(1100);
        lane.MoviePreset = "Any Preset";
        await orchestrator.RunOnceAsync(config);
        var reportsAfterPass2 = Directory.GetFiles(config.Report.ReportPath).Length;
        Assert.Equal(1, reportsAfterPass2);

        // Pass 3: broken again, identical problem code/message to pass 1 - must still count as
        // "changed" because it was genuinely clear in between.
        await Task.Delay(1100);
        lane.MoviePreset = "";
        var result3 = await orchestrator.RunOnceAsync(config);
        var reportsAfterPass3 = Directory.GetFiles(config.Report.ReportPath).Length;

        Assert.Equal(0, result3!.TotalFiles);
        Assert.Equal(2, reportsAfterPass3);
    }

    // Architecture-roadmap item 11 (PostExec timeout): a hung post-exec script previously blocked
    // the whole pass indefinitely, with nothing (not even Abort) able to interrupt it, since no
    // token ever reached the process. Uses a real ping.exe (always present on Windows, pings
    // loopback so it needs no real network access) as a genuinely long-running process to prove
    // the new timeout actually kills it, rather than mocking Process.Start away entirely.
    [Fact]
    public async Task RunOnceAsync_PostExecHangs_KilledAfterTimeout_AndLogsError()
    {
        var pingPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE");
        Assert.True(File.Exists(pingPath), "This test needs a real ping.exe on the test machine to simulate a hung PostExec command.");

        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.HandBrake.CliPath = Path.Combine(_tempDir, "HandBrakeCLI.exe");
        config.HandBrake.PresetsPath = Path.Combine(_tempDir, "presets.json");
        config.Logging.LogFilePath = Path.Combine(_tempDir, "Logs");
        config.Report.ReportPath = Path.Combine(_tempDir, "Reports");
        config.PostExec.Cmd = pingPath;
        config.PostExec.Args = "-n 999 127.0.0.1"; // ~999 seconds if never killed - real "hangs" stand-in

        var logger = new NoOpRunLogger();
        var (orchestrator, _) = BuildOrchestrator(config, new RecordingProcessRunner(), logger: logger, postExecTimeout: TimeSpan.FromMilliseconds(300));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await orchestrator.RunOnceAsync(config);
        stopwatch.Stop();

        // Proves the process was actually killed, not just that WaitForExitAsync gave up locally -
        // a real un-killed 999-ping run would take far longer than this test's own budget.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"RunOnceAsync took {stopwatch.Elapsed} - the hung PostExec process doesn't look like it was actually killed.");
        Assert.Contains(logger.Messages, m => m.Severity == LogSeverity.Error &&
            m.Message.Contains("Post-execution command was killed", StringComparison.Ordinal) &&
            m.Message.Contains("didn't finish within", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunOnceAsync_PostExecCompletesQuickly_RunsNormally_NoTimeoutError()
    {
        // Guards against the timeout/linked-token plumbing itself breaking the ordinary, expected
        // case - a post-exec command that behaves and exits well within its timeout.
        var pingPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE");

        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.HandBrake.CliPath = Path.Combine(_tempDir, "HandBrakeCLI.exe");
        config.HandBrake.PresetsPath = Path.Combine(_tempDir, "presets.json");
        config.Logging.LogFilePath = Path.Combine(_tempDir, "Logs");
        config.Report.ReportPath = Path.Combine(_tempDir, "Reports");
        config.PostExec.Cmd = pingPath;
        config.PostExec.Args = "-n 1 127.0.0.1"; // one ping, exits almost immediately

        var logger = new NoOpRunLogger();
        var (orchestrator, _) = BuildOrchestrator(config, new RecordingProcessRunner(), logger: logger, postExecTimeout: TimeSpan.FromSeconds(30));

        await orchestrator.RunOnceAsync(config);

        Assert.DoesNotContain(logger.Messages, m => m.Message.Contains("Post-execution command was killed", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, m => m.Message.Contains("Running post-execution command:", StringComparison.Ordinal));
    }

    // ---- per-lane encoders (2.2) ----------------------------------------------------------------

    private CompressarrConfig TwoEngineConfig(out string handBrakeFile, out string ffmpegFile)
    {
        var config = new CompressarrConfig { Processing = new ProcessingSettings { MoveFiles = false, ClearTitleMetadata = false } };
        config.HandBrake.CliPath = Path.Combine(_tempDir, "HandBrakeCLI.exe");
        config.HandBrake.PresetsPath = Path.Combine(_tempDir, "presets.json");
        config.Logging.LogFilePath = Path.Combine(_tempDir, "Logs");
        config.Report.ReportPath = Path.Combine(_tempDir, "Reports");

        handBrakeFile = null!;
        ffmpegFile = null!;
        foreach (var (id, engine) in new[] { ("hb", EncoderEngine.HandBrake), ("ff", EncoderEngine.FFmpeg) })
        {
            var input = Path.Combine(_tempDir, id + "_in");
            var output = Path.Combine(_tempDir, id + "_out");
            Directory.CreateDirectory(input);
            Directory.CreateDirectory(output);
            var file = Path.Combine(input, id + "-movie.mkv");
            File.WriteAllText(file, "x");
            config.Lanes.Add(new LaneConfig { Id = id, DisplayName = id.ToUpperInvariant() + " lane", Enabled = true, Input = input, Output = output, MoviePreset = id == "hb" ? "HB Preset" : "FF Preset", Engine = engine });
            if (id == "hb") handBrakeFile = file; else ffmpegFile = file;
        }
        return config;
    }

    [Fact]
    public async Task RunOnceAsync_EachLaneIsEncodedByItsOwnEngine()
    {
        var config = TwoEngineConfig(out _, out _);
        var resolver = new Compressarr.Core.Tests.Conversion.EngResolver();
        var (orchestrator, _) = BuildOrchestrator(config, new RecordingProcessRunner(), encoders: resolver);

        var result = await orchestrator.RunOnceAsync(config);

        Assert.Equal(2, result!.TotalFiles);
        Assert.Equal("HB Preset", Assert.Single(resolver.HandBrake.Requests).PresetName);
        Assert.Equal("FF Preset", Assert.Single(resolver.FFmpeg.Requests).PresetName);
    }

    [Fact]
    public async Task RunOnceAsync_AnFfmpegLaneWithNoFfmpeg_IsSkippedWithReportCode113_WhileTheHandBrakeLaneStillRuns()
    {
        var config = TwoEngineConfig(out _, out var ffmpegFile);
        var resolver = new Compressarr.Core.Tests.Conversion.EngResolver();
        resolver.NotReady[EncoderEngine.FFmpeg] = "ffmpeg was not found at 'X'.";
        var (orchestrator, _) = BuildOrchestrator(config, new RecordingProcessRunner(), encoders: resolver);

        var result = await orchestrator.RunOnceAsync(config);

        Assert.Equal(1, result!.TotalFiles);
        Assert.Single(resolver.HandBrake.Requests);
        Assert.Empty(resolver.FFmpeg.Requests);
        Assert.True(File.Exists(ffmpegFile));
        var ffLane = result.Report.Lanes.Single(l => l.LaneDisplayName == "FF lane");
        Assert.Contains(ReportErrorCode.LaneEncoderNotAvailable, ffLane.LaneProblems);
    }

    [Fact]
    public async Task RunOnceAsync_WithAnFfmpegLane_AMissingHandBrakeOnlySkipsTheHandBrakeLanes()
    {
        var config = TwoEngineConfig(out var handBrakeFile, out _);
        var resolver = new Compressarr.Core.Tests.Conversion.EngResolver();
        resolver.NotReady[EncoderEngine.HandBrake] = "HandBrakeCLI.exe was not found at 'X'.";
        var (orchestrator, _) = BuildOrchestrator(config, new RecordingProcessRunner(), encoders: resolver);
        File.Delete(config.HandBrake.CliPath); // BuildOrchestrator created it; the whole-run abort must not fire

        var result = await orchestrator.RunOnceAsync(config);

        Assert.NotNull(result);
        Assert.Equal(1, result!.TotalFiles);
        Assert.Single(resolver.FFmpeg.Requests);
        Assert.Empty(resolver.HandBrake.Requests);
        Assert.True(File.Exists(handBrakeFile));
    }

    [Fact]
    public async Task RunOnceAsync_AHandBrakeOnlySetup_StillAbortsTheWholeRunWhenHandBrakeIsMissing()
    {
        var config = TwoEngineConfig(out _, out _);
        config.Lanes.RemoveAll(l => l.Engine == EncoderEngine.FFmpeg);
        var resolver = new Compressarr.Core.Tests.Conversion.EngResolver();
        var (orchestrator, _) = BuildOrchestrator(config, new RecordingProcessRunner(), encoders: resolver);
        File.Delete(config.HandBrake.CliPath);

        var result = await orchestrator.RunOnceAsync(config);

        Assert.Null(result);
        Assert.Empty(resolver.HandBrake.Requests);
    }

    [Fact]
    public async Task RunOnceAsync_Passthrough_CopiesInsteadOfEncoding_AndHistoryCountsTheFileButNotItsBytes()
    {
        var (config, input) = MakeSingleLaneConfig();
        config.Lanes[0].MoviePreset = PassthroughPreset.Name;
        var big = new byte[3 * 1024 * 1024];
        new Random(1).NextBytes(big);
        File.WriteAllBytes(Path.Combine(input, "Fight Club (1999).mkv"), big);
        var processRunner = new RecordingProcessRunner();
        var history = new NoOpHistoryStore();
        var (orchestrator, _) = BuildOrchestrator(config, processRunner, historyStore: history);

        var result = await orchestrator.RunOnceAsync(config);

        Assert.Empty(processRunner.ProcessedInOrder);                                       // no encoder was ever started
        Assert.Equal(1, result!.TotalFiles);
        var copy = Assert.Single(Directory.GetFiles(config.Lanes[0].Output, "*.mkv"));
        Assert.Equal(big, await File.ReadAllBytesAsync(copy));                              // a byte-for-byte copy reached the Output step
        var record = Assert.Single(history.Appended);
        Assert.Equal(1, record.FileCount);                                                  // history counts the file...
        Assert.Equal(0, record.BeginSizeGb);                                                // ...but not its bytes
        Assert.Equal(0, record.EndSizeGb);
        var report = File.ReadAllText(Assert.Single(Directory.GetFiles(config.Report.ReportPath, "*.html")));
        Assert.Contains("pt-tag", report);
    }

    [Fact]
    public async Task RunOnceAsync_AMixedRun_CountsBothFiles_ButOnlyTheEncodedFilesBytes()
    {
        var (config, input) = MakeSingleLaneConfig();
        var data = new byte[3 * 1024 * 1024];
        var encodedFile = Path.Combine(input, "a-encoded.mkv");
        var copiedFile = Path.Combine(input, "b-copied.mkv");
        File.WriteAllBytes(encodedFile, data);
        File.WriteAllBytes(copiedFile, data);
        var processRunner = new RecordingProcessRunner();
        var history = new NoOpHistoryStore();
        var (orchestrator, resumeFilePath) = BuildOrchestrator(config, processRunner, historyStore: history);
        new JsonResumeStateStore().Save(new List<ResumeEntry>
        {
            new() { LaneId = "lane", FullName = encodedFile, Status = ResumeStatus.Pending, Order = 0 },
            new() { LaneId = "lane", FullName = copiedFile, Status = ResumeStatus.Pending, Order = 1, PresetOverride = PassthroughPreset.Name }   // one file's own override
        }, resumeFilePath);

        var result = await orchestrator.RunOnceAsync(config);

        Assert.Equal(new[] { "a-encoded.mkv" }, processRunner.ProcessedInOrder);            // only the first went to the encoder
        Assert.Equal(2, result!.TotalFiles);
        var record = Assert.Single(history.Appended);
        Assert.Equal(2, record.FileCount);
        Assert.Equal(0.003, record.BeginSizeGb);                                            // the encoded file's 3 MB, not 6
    }

    [Fact]
    public async Task RunOnceAsync_ALaneThatOnlyPassesThrough_DoesNotNeedAnEncoderToBeInstalled()
    {
        var (config, input) = MakeSingleLaneConfig("Inception (2010).mkv");
        config.Lanes[0].MoviePreset = PassthroughPreset.Name;
        config.Lanes[0].Engine = EncoderEngine.FFmpeg;                                       // ffmpeg is not installed in this test
        var resolver = new Compressarr.Core.Tests.Conversion.EngResolver();
        resolver.NotReady[EncoderEngine.FFmpeg] = "ffmpeg was not found at X.";
        var (orchestrator, _) = BuildOrchestrator(config, new RecordingProcessRunner(), encoders: resolver);

        var result = await orchestrator.RunOnceAsync(config);

        Assert.Equal(1, result!.TotalFiles);
        Assert.Single(Directory.GetFiles(config.Lanes[0].Output, "*.mkv"));
    }
}
