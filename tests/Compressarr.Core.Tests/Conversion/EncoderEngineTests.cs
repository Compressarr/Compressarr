using Compressarr.Core.Arr;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.FFmpeg;
using Compressarr.Core.FileBot;
using Compressarr.Core.Logging;
using Compressarr.Core.Orchestration;
using Compressarr.Core.Presets;
using Compressarr.Core.Routing;
using Compressarr.Core.Tests.FFmpeg;

namespace Compressarr.Core.Tests.Conversion;

internal sealed class EngPathExpander : IPathExpander
{
    public string Expand(string value) => value;
    public bool PathExists(string value) => Directory.Exists(value) || File.Exists(value);
}

internal sealed class EngScanner : IVideoFileScanner
{
    public IReadOnlyList<FileInfo> FindVideoFiles(string inputPath, IReadOnlyList<string> vidTypes, long minSizeBytes, int limit) =>
        Directory.GetFiles(inputPath).Select(f => new FileInfo(f)).OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
}

internal sealed class EngFileBot : IFileBotRunner
{
    public FileBotRunResult Run(FileBotSettings settings, string inputPath, IReadOnlyList<string> vidTypes, IRunLogger logger) => FileBotRunResult.Empty;
}

internal sealed class EngCompanions : ICompanionFileService
{
    public void MoveCompanionFiles(string originalFileFullName, string originalFileDirectory, string routedVideoDestPath, DeleteAfterConvertMode deleteAfterConvert, IReadOnlyList<string> companionExtensions, DeleteAfterConvertMode unmatchedCompanionAction, DestinationCollisionMode collisionMode = DestinationCollisionMode.Overwrite) { }
    public void CleanUpEmptySourceFolder(string originalFileDirectory, string inputRoot, IReadOnlyList<string> vidTypes, DeleteAfterConvertMode deleteAfterConvert, DeleteAfterConvertMode unmatchedCompanionAction) { }
}

internal sealed class EngArr : IArrUnmonitorService
{
    public Task<ArrUnmonitorResult> UnmonitorAsync(CompressarrConfig config, string fileName, bool isTv, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ArrUnmonitorResult(null, ArrRescanOutcome.NotEnabled));
}

internal sealed class EngTrash : ITrashService
{
    public void DeleteFile(string path, DeleteAfterConvertMode mode) { }
    public void DeleteFolder(string path, DeleteAfterConvertMode mode) { }
}

internal sealed class EngResume : IResumeStateStore
{
    public List<ResumeEntry> Load(string path) => new();
    public void Save(List<ResumeEntry> state, string path) { }
    public void DeleteIfComplete(List<ResumeEntry> state, string path) { }
    public T Update<T>(string path, Func<List<ResumeEntry>, T> mutate) => mutate(new List<ResumeEntry>());
}

internal sealed class EngProgress : IRunProgressReporter
{
    public List<string?> Presets { get; } = new();
    public void RunStarted(string timestamp) { }
    public void LaneStarted(string laneId, string laneDisplayName, bool isResumed) { }
    public void FileBotStarted(string laneId) { }
    public void FileBotCompleted(string laneId) { }
    public void FileStarted(string laneId, int index, int total, string fileName, string fullName, string? presetName, double sizeGb) => Presets.Add(presetName);
    public void FileProgress(string laneId, double percent, double? fps, string? eta) { }
    public void FileCompleted(string laneId, string fileName, bool success) { }
    public void FileThroughputSample(string? presetName, double gb, TimeSpan duration) { }
    public void RunCompleted(int totalFiles) { }
}

internal sealed class EngLogger : IRunLogger
{
    public event Action<string, LogSeverity>? LineWritten { add { } remove { } }
    public List<(string Message, LogSeverity Severity)> Logs { get; } = new();
    public bool HasLoggedError => Logs.Any(l => l.Severity == LogSeverity.Error);
    public string Initialize(string logFilePath, string timestamp) => Path.GetTempFileName();
    public void Log(string message, LogSeverity severity = LogSeverity.Info) => Logs.Add((message, severity));
    public void LogProblem(string key, string message) => Log(message, LogSeverity.Error);
    public void ClearProblem(string key) { }
    public bool HasLaneProblemsChanged(string laneId, IReadOnlyCollection<string> problemCodes) => true;
    public void FileStart(string laneDisplayName, int index, int total, string fileName, double sizeGb, string contentType, string preset) { }
    public void FileComplete(string fileName, double beginSizeGb, double endSizeGb, TimeSpan duration, bool success, string? detailLogFile) { }
}

internal sealed class EngConfigStore : IConfigStore
{
    public CompressarrConfig Config { get; }
    public EngConfigStore(CompressarrConfig config) => Config = config;
    public CompressarrConfig Load(string path) => Config;
    public void Save(CompressarrConfig config, string path) { }
    public T Update<T>(string path, Func<CompressarrConfig, T> mutate) => mutate(Config);
}

/// <summary>An encoder stand-in: records each request and writes a tiny output file, or reports the
/// failure it was told to.</summary>
internal sealed class EngRunner : IEncoderRunner
{
    public string Name { get; }
    public List<EncodeRequest> Requests { get; } = new();
    public EncodeResult? FailWith { get; set; }

    public EngRunner(string name) => Name = name;

    public Task<EncodeResult> RunAsync(EncodeRequest request, Action<EncodeProgress>? onProgress, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Directory.CreateDirectory(Path.GetDirectoryName(request.DetailLogFile)!);
        File.WriteAllText(request.DetailLogFile, $"{Name} detail output");
        if (FailWith is { } failure)
        {
            return Task.FromResult(failure with { DetailLogFile = request.DetailLogFile });
        }
        File.WriteAllText(request.OutputPath, "fake encoded output");
        return Task.FromResult(new EncodeResult(true, request.DetailLogFile));
    }
}

internal sealed class EngPresets : IEncoderPresetService
{
    private readonly Dictionary<string, string> _extensions;
    public EngPresets(params (string Name, string Extension)[] presets) => _extensions = presets.ToDictionary(p => p.Name, p => p.Extension, StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> GetPresetNames() => _extensions.Keys.ToList();
    public bool PresetExists(string presetName) => _extensions.ContainsKey(presetName);
    public string GetOutputExtension(string presetName, out string? warning) { warning = null; return _extensions.GetValueOrDefault(presetName, ".mp4"); }
    public string PreparePresetSource() => "prepared-source";
}

internal sealed class EngResolver : IEncoderResolver
{
    public EngRunner HandBrake { get; } = new("HandBrake");
    public EngRunner FFmpeg { get; } = new("ffmpeg");
    public EngPresets HandBrakePresets { get; set; } = new(("HB Preset", ".mkv"), ("Compressarr SD-HD", ".mkv"));
    public EngPresets FFmpegPresets { get; set; } = new(("FF Preset", ".mp4"), ("FF Mkv", ".mkv"));
    public Dictionary<EncoderEngine, string> NotReady { get; } = new();
    public MediaProbeResult? Probe { get; set; }
    public Dictionary<string, string> Fallbacks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int ProbeCalls { get; private set; }

    public EncoderBinding For(EncoderEngine engine, CompressarrConfig config) => engine == EncoderEngine.FFmpeg
        ? new EncoderBinding(engine, FFmpeg, FFmpegPresets, "ffmpeg.exe", "ffprobe.exe", "-threads 4")
        : new EncoderBinding(engine, HandBrake, HandBrakePresets, "HandBrakeCLI.exe", null, "--two-pass");

    public IEncoderPresetService PresetsFor(EncoderEngine engine) => engine == EncoderEngine.FFmpeg ? FFmpegPresets : HandBrakePresets;
    public string? NotReadyReason(EncoderEngine engine, CompressarrConfig config) => NotReady.GetValueOrDefault(engine);

    public Task<MediaProbeResult?> ProbeAsync(CompressarrConfig config, string filePath, CancellationToken cancellationToken)
    {
        ProbeCalls++;
        return Task.FromResult(Probe);
    }

    public string? FallbackHandBrakePreset(string ffmpegProfileName) => Fallbacks.GetValueOrDefault(ffmpegProfileName);
    public HashSet<string> DropsDynamicHdr { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool AllowsDroppingDynamicHdr(string ffmpegProfileName) => DropsDynamicHdr.Contains(ffmpegProfileName);
    public Dictionary<string, HandBrakeProfileTraits> Traits { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HandBrakeProfileTraits? HandBrakeTraits(string handBrakeProfileName) => Traits.GetValueOrDefault(handBrakeProfileName);
}

/// <summary>The orchestrator choosing the encoder per lane: the lane's engine decides the runner, the
/// tool paths, the output extension and the log name; a Dolby Vision / HDR10+ file on an ffmpeg lane goes
/// to HandBrake via the profile's fallback preset or is refused; and an ffmpeg length-check failure is
/// reported with its own code.</summary>
public class EncoderEngineTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("compressarr-engine-tests-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private sealed record Rig(ConversionOrchestrator Orchestrator, LaneConfig Lane, CompressarrConfig Config, EngResolver Resolver, EngLogger Logger, EngProgress Progress, string Source, string ResumePath);

    private Rig BuildRig(EncoderEngine engine, string preset, bool withResolver = true)
    {
        var input = Path.Combine(_tempDir, "Input");
        var output = Path.Combine(_tempDir, "Output");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        var source = Path.Combine(input, "Some Movie (2020).mkv");
        File.WriteAllText(source, "source bytes");

        var lane = new LaneConfig { Id = "lane1", DisplayName = "Test Lane", Enabled = true, Input = input, Output = output, MoviePreset = preset, Engine = engine };
        var config = new CompressarrConfig { Processing = new ProcessingSettings { DeleteAfterConvert = DeleteAfterConvertMode.Maintain, MoveFiles = false, ClearTitleMetadata = false } };
        config.Lanes.Add(lane);
        config.HandBrake.CliPath = "HandBrakeCLI.exe";
        config.HandBrake.Options = "--two-pass";

        var resolver = new EngResolver();
        var logger = new EngLogger();
        var progress = new EngProgress();
        var orchestrator = new ConversionOrchestrator(
            new EngPathExpander(), new EngScanner(), new EngFileBot(), resolver.HandBrakePresets, new MetadataService(),
            resolver.HandBrake, new FileRouter(), new EngCompanions(), new EngArr(), new EngTrash(), logger, new EngResume(), progress,
            new EngConfigStore(config), withResolver ? resolver : null);

        return new Rig(orchestrator, lane, config, resolver, logger, progress, source, Path.Combine(_tempDir, "resume.json"));
    }

    private static async Task<ConversionResult> RunAsync(Rig rig, string timestamp = "20260101_000000")
    {
        var resume = new List<ResumeEntry>();
        var context = await rig.Orchestrator.PrepareLaneAsync(rig.Lane, rig.Config, resume, rig.ResumePath);
        var entry = resume.Single(e => e.LaneId == rig.Lane.Id);
        return await rig.Orchestrator.ProcessOneFileAsync(context!, entry, rig.ResumePath.Replace("resume.json", ""), timestamp, resume, rig.ResumePath, CancellationToken.None);
    }

    private static MediaProbeResult Probe(string json) => FFprobeParser.Parse(json);

    // ---- which encoder runs ---------------------------------------------------------------------

    [Fact]
    public async Task AHandBrakeLane_UsesHandBrake_WithItsOwnToolAndOptions_AndNoEncoderTag()
    {
        var rig = BuildRig(EncoderEngine.HandBrake, "HB Preset");

        var result = await RunAsync(rig);

        Assert.True(result.Success);
        var request = Assert.Single(rig.Resolver.HandBrake.Requests);
        Assert.Equal("HandBrakeCLI.exe", request.ToolPath);
        Assert.Equal("prepared-source", request.PresetSource);
        Assert.Equal("HB Preset", request.PresetName);
        Assert.Equal("--two-pass", request.ExtraOptions);
        Assert.Null(request.ProbePath);
        Assert.EndsWith(".mkv", request.OutputPath);
        Assert.EndsWith("_HBdetails.txt", request.DetailLogFile);
        Assert.Null(result.EncoderLabel);
        Assert.Empty(rig.Resolver.FFmpeg.Requests);
        Assert.Equal(0, rig.Resolver.ProbeCalls); // HandBrake lanes are never probed
    }

    [Fact]
    public async Task AnFfmpegLane_UsesFfmpeg_WithItsToolProbeAndOptions_ItsContainer_AndTagsTheResult()
    {
        var rig = BuildRig(EncoderEngine.FFmpeg, "FF Preset");
        rig.Resolver.Probe = Probe(FFmpegFixtures.UhdHdr10Rip); // static HDR10 only: ffmpeg handles it

        var result = await RunAsync(rig);

        Assert.True(result.Success);
        var request = Assert.Single(rig.Resolver.FFmpeg.Requests);
        Assert.Equal("ffmpeg.exe", request.ToolPath);
        Assert.Equal("ffprobe.exe", request.ProbePath);
        Assert.Equal("-threads 4", request.ExtraOptions);
        Assert.Equal("FF Preset", request.PresetName);
        Assert.EndsWith(".mp4", request.OutputPath);             // the ffmpeg profile's container, not HandBrake's
        Assert.EndsWith("_FFdetails.txt", request.DetailLogFile);
        Assert.Equal("ffmpeg", result.EncoderLabel);
        Assert.Empty(rig.Resolver.HandBrake.Requests);
    }

    [Fact]
    public async Task AnFfmpegLane_WhenFfprobeCannotReadTheFile_StillHandsItToFfmpeg_WhichReportsTheProblem()
    {
        var rig = BuildRig(EncoderEngine.FFmpeg, "FF Mkv");
        rig.Resolver.Probe = null;
        rig.Resolver.FFmpeg.FailWith = new EncodeResult(false, "", Failure: EncodeFailureKind.NotStarted, FailureMessage: "ffprobe could not read 'x'.");

        var result = await RunAsync(rig);

        Assert.False(result.Success);
        Assert.Equal(ReportErrorCode.EncodeFailed, result.ErrorCode);
        Assert.Equal("ffprobe could not read 'x'.", result.FailureReason);
        Assert.Single(rig.Resolver.FFmpeg.Requests);
    }

    [Fact]
    public async Task WithNoResolver_EverythingIsHandBrake_AsBefore22()
    {
        var rig = BuildRig(EncoderEngine.FFmpeg, "HB Preset", withResolver: false); // the lane says ffmpeg, but nothing can honour it

        var result = await RunAsync(rig);

        Assert.True(result.Success);
        Assert.Single(rig.Resolver.HandBrake.Requests);
        Assert.Null(result.EncoderLabel);
    }

    // ---- Dolby Vision / HDR10+ ------------------------------------------------------------------

    [Fact]
    public async Task ADolbyVisionFile_OnAnFfmpegLane_GoesToHandBrake_WithTheFallbackPreset()
    {
        var rig = BuildRig(EncoderEngine.FFmpeg, "FF Preset");
        rig.Resolver.Probe = Probe(FFmpegFixtures.DolbyVisionRip);
        rig.Resolver.Fallbacks["FF Preset"] = "Compressarr SD-HD";

        var result = await RunAsync(rig);

        Assert.True(result.Success);
        Assert.Empty(rig.Resolver.FFmpeg.Requests);
        var request = Assert.Single(rig.Resolver.HandBrake.Requests);
        Assert.Equal("Compressarr SD-HD", request.PresetName);
        Assert.Equal("HandBrakeCLI.exe", request.ToolPath);
        Assert.EndsWith(".mkv", request.OutputPath);             // HandBrake's preset decides the container now
        Assert.EndsWith("_HBdetails.txt", request.DetailLogFile);
        Assert.Equal("Compressarr SD-HD", result.PresetName);
        Assert.Equal("HandBrake (Dolby Vision fallback)", result.EncoderLabel);
        Assert.Contains(rig.Logger.Logs, l => l.Message.Contains("Dolby Vision detected") && l.Message.Contains("encoded by HandBrake"));
        Assert.Contains("Compressarr SD-HD", rig.Progress.Presets); // the Monitor is told the preset really used
    }

    [Fact]
    public async Task Hdr10Plus_IsTreatedLikeDolbyVision()
    {
        var rig = BuildRig(EncoderEngine.FFmpeg, "FF Preset");
        rig.Resolver.Probe = FFprobeParser.Parse(FFmpegFixtures.UhdHdr10Rip, FFmpegFixtures.Hdr10PlusFrames);
        rig.Resolver.Fallbacks["FF Preset"] = "HB Preset";

        var result = await RunAsync(rig);

        Assert.Equal("HandBrake (HDR10+ fallback)", result.EncoderLabel);
        Assert.Single(rig.Resolver.HandBrake.Requests);
        Assert.Contains(rig.Logger.Logs, l => l.Message.Contains("HDR10+ detected"));
    }

    [Fact]
    public async Task ADolbyVisionFile_WithNoFallback_IsRefused_AndTheSourceIsLeftAlone()
    {
        var rig = BuildRig(EncoderEngine.FFmpeg, "FF Preset");
        rig.Resolver.Probe = Probe(FFmpegFixtures.DolbyVisionRip);

        var result = await RunAsync(rig);

        Assert.False(result.Success);
        Assert.Equal(ReportErrorCode.DynamicHdrNeedsHandBrake, result.ErrorCode);
        Assert.Contains("no HandBrake fallback preset", result.FailureReason);
        Assert.Empty(rig.Resolver.FFmpeg.Requests);
        Assert.Empty(rig.Resolver.HandBrake.Requests);
        Assert.True(File.Exists(rig.Source));
    }

    // ---- "encode anyway": a profile that allows dropping the dynamic metadata ------------------------

    private static MediaProbeResult DvRip(int profile, int? compatibility)
    {
        var record = compatibility is { } c ? $"\"dv_profile\": {profile}, \"dv_bl_signal_compatibility_id\": {c}, \"dv_level\": 6" : $"\"dv_profile\": {profile}, \"dv_level\": 6";
        return Probe(FFmpegFixtures.DolbyVisionRip.Replace("\"dv_profile\": 8, \"dv_level\": 6", record));
    }

    [Fact]
    public async Task ADolbyVisionFile_WithEncodeAnywayOn_IsEncodedByFfmpeg_AndTheReportTagSaysItWasDropped()
    {
        var rig = BuildRig(EncoderEngine.FFmpeg, "FF Preset");
        rig.Resolver.Probe = DvRip(8, 1); // profile 8.1: an HDR10 base layer
        rig.Resolver.DropsDynamicHdr.Add("FF Preset");

        var result = await RunAsync(rig);

        Assert.True(result.Success);
        Assert.Single(rig.Resolver.FFmpeg.Requests);
        Assert.Empty(rig.Resolver.HandBrake.Requests);
        Assert.Equal("ffmpeg (Dolby Vision dropped)", result.EncoderLabel);
        Assert.Contains(rig.Logger.Logs, l => l.Message.Contains("Dolby Vision detected") && l.Message.Contains("encode anyway"));
    }

    [Theory]
    [InlineData(8, 2)]   // SDR base layer
    [InlineData(8, 4)]   // HLG base layer
    [InlineData(7, 6)]   // UHD Blu-ray (Blu-ray HDR10 base layer)
    [InlineData(8, null)] // a profile 8 file whose ffprobe doesn't report the compatibility id: profile 8 always has one
    [InlineData(7, null)]
    public async Task EncodeAnyway_AllowsEveryDolbyVisionFileWithAUsableBaseLayer(int profile, int? compatibility)
    {
        var rig = BuildRig(EncoderEngine.FFmpeg, "FF Preset");
        rig.Resolver.Probe = DvRip(profile, compatibility);
        rig.Resolver.DropsDynamicHdr.Add("FF Preset");

        var result = await RunAsync(rig);

        Assert.True(result.Success);
        Assert.Single(rig.Resolver.FFmpeg.Requests);
    }

    [Theory]
    [InlineData(5, 0)]      // profile 5: no HDR10-compatible base layer
    [InlineData(5, null)]
    [InlineData(4, null)]   // anything else we can't vouch for
    public async Task EncodeAnyway_StillRefusesADolbyVisionFileWithNoUsableBaseLayer(int profile, int? compatibility)
    {
        var rig = BuildRig(EncoderEngine.FFmpeg, "FF Preset");
        rig.Resolver.Probe = DvRip(profile, compatibility);
        rig.Resolver.DropsDynamicHdr.Add("FF Preset");

        var result = await RunAsync(rig);

        Assert.False(result.Success);
        Assert.Equal(ReportErrorCode.DynamicHdrNeedsHandBrake, result.ErrorCode);
        Assert.Contains("no HDR10-compatible base layer", result.FailureReason);
        Assert.Contains($"profile {profile}", result.FailureReason);
        Assert.Empty(rig.Resolver.FFmpeg.Requests);
        Assert.True(File.Exists(rig.Source)); // the original is untouched
    }

    [Fact]
    public async Task Hdr10Plus_WithEncodeAnywayOn_IsEncodedByFfmpeg()
    {
        var rig = BuildRig(EncoderEngine.FFmpeg, "FF Preset");
        rig.Resolver.Probe = FFprobeParser.Parse(FFmpegFixtures.UhdHdr10Rip, FFmpegFixtures.Hdr10PlusFrames);
        rig.Resolver.DropsDynamicHdr.Add("FF Preset");

        var result = await RunAsync(rig);

        Assert.True(result.Success);
        Assert.Equal("ffmpeg (HDR10+ dropped)", result.EncoderLabel);
        Assert.Single(rig.Resolver.FFmpeg.Requests);
    }

    [Fact]
    public async Task ANamedHandBrakeFallback_WinsOverEncodeAnyway()
    {
        var rig = BuildRig(EncoderEngine.FFmpeg, "FF Preset");
        rig.Resolver.Probe = DvRip(8, 1);
        rig.Resolver.Fallbacks["FF Preset"] = "Compressarr SD-HD";
        rig.Resolver.DropsDynamicHdr.Add("FF Preset");

        var result = await RunAsync(rig);

        Assert.Equal("HandBrake (Dolby Vision fallback)", result.EncoderLabel);
        Assert.Empty(rig.Resolver.FFmpeg.Requests);
    }

    [Fact]
    public async Task ABrokenHandBrakeFallback_IsNotQuietlyReplacedByEncodeAnyway()
    {
        var rig = BuildRig(EncoderEngine.FFmpeg, "FF Preset");
        rig.Resolver.Probe = DvRip(8, 1);
        rig.Resolver.Fallbacks["FF Preset"] = "Deleted Preset";
        rig.Resolver.DropsDynamicHdr.Add("FF Preset");

        var result = await RunAsync(rig);

        Assert.False(result.Success);
        Assert.Equal(ReportErrorCode.DynamicHdrNeedsHandBrake, result.ErrorCode);
        Assert.Empty(rig.Resolver.FFmpeg.Requests);
    }

    [Fact]
    public async Task WithEncodeAnywayOff_NothingChanges_ADolbyVisionFileIsStillRefused()
    {
        var rig = BuildRig(EncoderEngine.FFmpeg, "FF Preset");
        rig.Resolver.Probe = DvRip(8, 1);

        var result = await RunAsync(rig);

        Assert.False(result.Success);
        Assert.Contains("no HandBrake fallback preset", result.FailureReason);
    }

    // ---- HandBrake lanes: an HDR file is not encoded with a profile that would lose the HDR ----------------

    private static MediaProbeResult Hdr10() => FFprobeParser.Parse(FFmpegFixtures.UhdHdr10Rip);
    private static MediaProbeResult Sdr() => FFmpegFixtures.Dvd();

    private Rig HandBrakeRig(string encoder, bool dynamicMetadata, MediaProbeResult? probe, bool allowHdrLoss = false)
    {
        var rig = BuildRig(EncoderEngine.HandBrake, "HB Preset");
        rig.Resolver.Traits["HB Preset"] = new HandBrakeProfileTraits(encoder, dynamicMetadata);
        rig.Resolver.Probe = probe;
        rig.Lane.AllowHdrLoss = allowHdrLoss;
        return rig;
    }

    [Fact]
    public async Task AnHdrFile_WithAnSdrOnlyH264Profile_IsRefused_HeldAndLeftAlone()
    {
        var rig = HandBrakeRig("x264", dynamicMetadata: true, Hdr10());
        var resume = new List<ResumeEntry>();
        var context = await rig.Orchestrator.PrepareLaneAsync(rig.Lane, rig.Config, resume, rig.ResumePath);
        var entry = resume.Single(e => e.LaneId == rig.Lane.Id);

        var result = await rig.Orchestrator.ProcessOneFileAsync(context!, entry, rig.ResumePath.Replace("resume.json", ""), "20260101_000000", resume, rig.ResumePath, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(ReportErrorCode.ProfileCannotKeepHdr, result.ErrorCode);
        Assert.Contains("can only produce SDR", result.FailureReason);
        Assert.Contains("(x264)", result.FailureReason);
        Assert.Empty(rig.Resolver.HandBrake.Requests);
        Assert.True(File.Exists(rig.Source));
        Assert.Equal(ResumeStatus.Error, entry.Status);
        Assert.True(entry.HeldAfterFailure);
    }

    [Fact]
    public async Task TheLanesChoiceToAllowHdrLoss_LetsThatFileThrough()
    {
        var rig = HandBrakeRig("x264", dynamicMetadata: true, Hdr10(), allowHdrLoss: true);

        var result = await RunAsync(rig);

        Assert.True(result.Success);
        Assert.Single(rig.Resolver.HandBrake.Requests);
    }

    [Fact]
    public async Task AnSdrFile_WithAnH264Profile_IsEncodedAsAlways()
    {
        var rig = HandBrakeRig("x264", dynamicMetadata: true, Sdr());

        var result = await RunAsync(rig);

        Assert.True(result.Success);
        Assert.Single(rig.Resolver.HandBrake.Requests);
    }

    [Theory]
    [InlineData("x265_10bit")]
    [InlineData("svt_av1_10bit")]
    public async Task AProfileThatKeepsEverything_IsNeverProbed_SoTheLaneIsNotSlowedDown(string encoder)
    {
        var rig = HandBrakeRig(encoder, dynamicMetadata: true, DvRip(8, 1));

        var result = await RunAsync(rig);

        Assert.True(result.Success);
        Assert.Equal(0, rig.Resolver.ProbeCalls);
    }

    [Fact]
    public async Task ADolbyVisionFile_WithAHardwareEncoderThatCannotKeepIt_IsRefused()
    {
        var rig = HandBrakeRig("nvenc_h265_10bit", dynamicMetadata: true, DvRip(8, 1));

        var result = await RunAsync(rig);

        Assert.Equal(ReportErrorCode.ProfileCannotKeepHdr, result.ErrorCode);
        Assert.Contains("Dolby Vision", result.FailureReason);
        Assert.Contains("can't keep", result.FailureReason);
        Assert.Empty(rig.Resolver.HandBrake.Requests);
    }

    [Fact]
    public async Task APlainHdr10File_WithAHardwareEncoder_IsEncoded_BecauseItKeepsHdr10()
    {
        var rig = HandBrakeRig("nvenc_h265_10bit", dynamicMetadata: true, Hdr10());

        var result = await RunAsync(rig);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task AProfileThatHasSwitchedDynamicMetadataOff_HasAlreadySaidItDoesNotWantIt()
    {
        var rig = HandBrakeRig("nvenc_h265_10bit", dynamicMetadata: false, DvRip(8, 1));

        var result = await RunAsync(rig);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ALanesChoiceNeverCoversDolbyVisionProfile5_WhichWouldShowWrongColours()
    {
        var rig = HandBrakeRig("nvenc_h265_10bit", dynamicMetadata: true, DvRip(5, 0), allowHdrLoss: true);

        var result = await RunAsync(rig);

        Assert.Equal(ReportErrorCode.ProfileCannotKeepHdr, result.ErrorCode);
        Assert.Contains("profile 5", result.FailureReason);
        Assert.Contains("wrong colours", result.FailureReason);
        Assert.Empty(rig.Resolver.HandBrake.Requests);
    }

    [Fact]
    public async Task WithoutFfprobe_TheHdrCheckIsSkippedWithOneNote_AndTheFileIsEncodedAsBefore()
    {
        var rig = HandBrakeRig("x264", dynamicMetadata: true, probe: null);

        var result = await RunAsync(rig);

        Assert.True(result.Success);
        Assert.Single(rig.Logger.Logs, l => l.Message.Contains("HDR check skipped"));
    }

    [Fact]
    public async Task TheSkippedHdrCheckNote_IsSaidAgainInTheNextRun_NotOnlyOncePerProcess()
    {
        var rig = HandBrakeRig("x264", dynamicMetadata: true, probe: null);

        await RunAsync(rig, "20260101_000000");
        File.WriteAllText(rig.Source, "source bytes");   // the first run may have moved or deleted it
        await RunAsync(rig, "20260102_000000");          // the next pass has its own timestamp

        Assert.Equal(2, rig.Logger.Logs.Count(l => l.Message.Contains("HDR check skipped")));
    }

    [Fact]
    public async Task AnUnknownProfile_IsNotJudged()
    {
        var rig = BuildRig(EncoderEngine.HandBrake, "HB Preset"); // no traits registered for it
        rig.Resolver.Probe = Hdr10();

        var result = await RunAsync(rig);

        Assert.True(result.Success);
        Assert.Equal(0, rig.Resolver.ProbeCalls);
    }

    [Fact]
    public async Task ADolbyVisionFile_HandedToAnH264HandBrakeFallback_IsStillRefused()
    {
        var rig = BuildRig(EncoderEngine.FFmpeg, "FF Preset");
        rig.Resolver.Probe = DvRip(8, 1);
        rig.Resolver.Fallbacks["FF Preset"] = "Compressarr SD-HD";
        rig.Resolver.Traits["Compressarr SD-HD"] = new HandBrakeProfileTraits("x264", true);

        var result = await RunAsync(rig);

        Assert.Equal(ReportErrorCode.ProfileCannotKeepHdr, result.ErrorCode);
        Assert.Equal("HandBrake (Dolby Vision fallback)", result.EncoderLabel);
        Assert.Empty(rig.Resolver.HandBrake.Requests);
        Assert.Empty(rig.Resolver.FFmpeg.Requests);
    }

    [Fact]
    public async Task ARefusedDolbyVisionFile_IsHeldInTheQueue_SoItIsNotReportedAgainOnEveryPass()
    {
        var rig = BuildRig(EncoderEngine.FFmpeg, "FF Preset");
        rig.Resolver.Probe = DvRip(8, 1);
        var resume = new List<ResumeEntry>();
        var context = await rig.Orchestrator.PrepareLaneAsync(rig.Lane, rig.Config, resume, rig.ResumePath);
        var entry = resume.Single(e => e.LaneId == rig.Lane.Id);

        var result = await rig.Orchestrator.ProcessOneFileAsync(context!, entry, rig.ResumePath.Replace("resume.json", ""), "20260101_000000", resume, rig.ResumePath, CancellationToken.None);

        Assert.Equal(ReportErrorCode.DynamicHdrNeedsHandBrake, result.ErrorCode);
        Assert.Equal(ResumeStatus.Error, entry.Status);
        Assert.True(entry.HeldAfterFailure);
    }

    [Fact]
    public async Task ADolbyVisionFile_WhenHandBrakeIsMissing_IsRefusedWithThatReason()
    {
        var rig = BuildRig(EncoderEngine.FFmpeg, "FF Preset");
        rig.Resolver.Probe = Probe(FFmpegFixtures.DolbyVisionRip);
        rig.Resolver.Fallbacks["FF Preset"] = "Compressarr SD-HD";
        rig.Resolver.NotReady[EncoderEngine.HandBrake] = "HandBrakeCLI.exe was not found";

        var result = await RunAsync(rig);

        Assert.Equal(ReportErrorCode.DynamicHdrNeedsHandBrake, result.ErrorCode);
        Assert.Contains("HandBrakeCLI.exe was not found", result.FailureReason);
        Assert.Empty(rig.Resolver.HandBrake.Requests);
    }

    [Fact]
    public async Task ADolbyVisionFile_WhenTheFallbackPresetDoesNotExist_IsRefused()
    {
        var rig = BuildRig(EncoderEngine.FFmpeg, "FF Preset");
        rig.Resolver.Probe = Probe(FFmpegFixtures.DolbyVisionRip);
        rig.Resolver.Fallbacks["FF Preset"] = "Deleted Preset";

        var result = await RunAsync(rig);

        Assert.Equal(ReportErrorCode.DynamicHdrNeedsHandBrake, result.ErrorCode);
        Assert.Contains("'Deleted Preset' doesn't exist", result.FailureReason);
    }

    [Fact]
    public async Task StaticHdr10_StaysOnFfmpeg()
    {
        var rig = BuildRig(EncoderEngine.FFmpeg, "FF Preset");
        rig.Resolver.Probe = Probe(FFmpegFixtures.UhdHdr10Rip);
        rig.Resolver.Fallbacks["FF Preset"] = "Compressarr SD-HD";

        var result = await RunAsync(rig);

        Assert.Equal("ffmpeg", result.EncoderLabel);
        Assert.Single(rig.Resolver.FFmpeg.Requests);
        Assert.Empty(rig.Resolver.HandBrake.Requests);
    }

    // ---- results --------------------------------------------------------------------------------

    [Fact]
    public async Task ALengthMismatch_IsReportedAsErrorCode111_AndTheSourceIsKept()
    {
        var rig = BuildRig(EncoderEngine.FFmpeg, "FF Mkv");
        rig.Resolver.Probe = Probe(FFmpegFixtures.UhdHdr10Rip);
        rig.Resolver.FFmpeg.FailWith = new EncodeResult(false, "", Failure: EncodeFailureKind.LengthMismatch,
            FailureMessage: "The encoded file is 3:12 long but the source is 11:47 - it was not kept and the original was left alone.");

        var result = await RunAsync(rig);

        Assert.False(result.Success);
        Assert.Equal(ReportErrorCode.EncodedLengthMismatch, result.ErrorCode);
        Assert.Equal(111, (int)result.ErrorCode!.Value);
        Assert.Contains("3:12", result.FailureReason);
        Assert.True(File.Exists(rig.Source));
        Assert.Contains(rig.Logger.Logs, l => l.Severity == LogSeverity.Error && l.Message.Contains("3:12"));
        Assert.Equal("ffmpeg", result.EncoderLabel);
    }

    [Fact]
    public async Task AnOrdinaryHandBrakeFailure_StillIsErrorCode101()
    {
        var rig = BuildRig(EncoderEngine.HandBrake, "HB Preset");
        rig.Resolver.HandBrake.FailWith = new EncodeResult(false, "");

        var result = await RunAsync(rig);

        Assert.Equal(ReportErrorCode.EncodeFailed, result.ErrorCode);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void TheNewReportCodes_HaveStableNumbersAndDescriptions()
    {
        Assert.Equal(111, (int)ReportErrorCode.EncodedLengthMismatch);
        Assert.Equal(112, (int)ReportErrorCode.DynamicHdrNeedsHandBrake);
        Assert.Equal(113, (int)ReportErrorCode.LaneEncoderNotAvailable);
        Assert.DoesNotContain("unspecified", ReportErrorCode.EncodedLengthMismatch.Describe());
        Assert.DoesNotContain("unspecified", ReportErrorCode.DynamicHdrNeedsHandBrake.Describe());
        Assert.DoesNotContain("unspecified", ReportErrorCode.LaneEncoderNotAvailable.Describe());
    }
}
