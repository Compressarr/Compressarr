using Compressarr.Core.Arr;
using Compressarr.Core.Config;
using Compressarr.Core.FFmpeg;
using Compressarr.Core.FileBot;
using Compressarr.Core.Logging;
using Compressarr.Core.Orchestration;
using Compressarr.Core.Presets;
using Compressarr.Core.Queue;
using Compressarr.Core.Routing;
using static Compressarr.Core.Conversion.FileOutcomeHelpers;

namespace Compressarr.Core.Conversion;

/// <summary>Per-lane state that survives across non-contiguous visits to the same lane in a global,
/// cross-lane processing order - one lane's file can now be processed, then a different lane's
/// file, then this lane's next file, so the config/path snapshot a mid-run settings reload updates
/// (see ProcessOneFileAsync) needs an explicit home instead of a local variable closed over by a
/// single lane's own while loop. InputPath is deliberately init-only and never refreshed, matching
/// the original method's own asymmetry: companion-file routing math depends on the ORIGINAL Input
/// path as its relative-move cutoff root, not whatever it's reloaded to mid-run.</summary>
public sealed class LaneProcessingContext
{
    public required LaneConfig Lane { get; set; }
    public required CompressarrConfig Config { get; set; }
    public required string InputPath { get; init; }
    public required string OutputBase { get; set; }
    public required string TvShowBasePath { get; set; }
    public required string MovieBasePath { get; set; }
    public required string HbLoc { get; set; }

    /// <summary>"File i of N" and log-filename zero-padding - a point-in-time estimate from this
    /// lane's own prep, cosmetic only (a queue-control edit mid-run can make the real count drift
    /// slightly), kept per-lane rather than switched to a global count across every lane so today's
    /// "Compressing File in Lane X, i of N" UI copy keeps meaning what it already says.</summary>
    public int FileIndex { get; set; }
    public int FileTotal { get; set; }
    public int PadSize { get; set; }

    /// <summary>This lane's own natural (recursive folder scan) order, by full path - the fallback
    /// tie-break for entries with no explicit user-set Order, used identically by both the global
    /// cross-lane picking loop (RunOrchestrator) and the Monitor page's queue display
    /// (RunEndpoints.ComputeUpNext) so the two can never disagree about "what's next" for a file
    /// nobody has ever dragged.</summary>
    public required IReadOnlyDictionary<string, int> NaturalOrderIndex { get; init; }

    /// <summary>Count of MoveFailed/CompanionMoveFailed/CleanupPending resume entries this call's
    /// own retry loops (run before any of the above) successfully resolved this pass - zero for the overwhelmingly
    /// common case where there was nothing to retry. Read by RunOrchestrator so a pass whose ONLY
    /// activity was a successful retry (no new file ever reached ProcessOneFileAsync, so
    /// TotalFiles stays 0) still keeps its own log and gets a report/history entry, instead of
    /// looking indistinguishable from a genuinely idle poll and having that real activity silently
    /// discarded - a real gap found via code review.</summary>
    public int RetriesSucceeded { get; set; }
}

public interface IConversionOrchestrator
{
    /// <summary>Per-lane setup, run once per enabled lane before any cross-lane interleaved
    /// processing begins: validates the lane's Input/Output configuration, drops dead Pending/Error
    /// entries whose source file is gone, retries any MoveFailed entries from a prior pass, always
    /// scans Input recursively (both to seed resumeState with Pending entries when none exist yet,
    /// and to build the natural fallback order used for untouched entries either way). Returns null
    /// if this lane can't be processed at
    /// all (bad Input path, or no Output configured with "write output to same folder as input"
    /// off) - the caller should skip the lane entirely in that case, the same way the lane-scoped
    /// early-returns worked before this method existed.
    ///
    /// reportProblems, if given, has every lane/run-level ReportErrorCode found during this call
    /// appended to it (no Output configured, FileBot path not found) - the caller supplies the
    /// list so it can merge these with whatever it already found itself (e.g. RunOrchestrator's
    /// own no-preset-configured/preset-not-found checks) into one combined per-lane list for the
    /// report. Optional and simply not populated if the caller doesn't care.
    ///
    /// cancellationToken (Abort) is passed through to any MoveFailed-retry arr-unmonitor call this
    /// makes - it can only stop that call's own rescan-completion WAIT early, not the retry's own
    /// filesystem work (there's no in-flight HandBrakeCLI process here to kill, unlike
    /// ProcessOneFileAsync's own use of a cancellation token).</summary>
    Task<LaneProcessingContext?> PrepareLaneAsync(
        LaneConfig lane,
        CompressarrConfig config,
        List<ResumeEntry> resumeState,
        string resumeFilePath,
        List<ReportErrorCode>? reportProblems = null,
        CancellationToken cancellationToken = default);

    /// <summary>Processes exactly one already-selected file for the lane context carries - fully
    /// finished (converted, routed, arr-unmonitored, companions handled) before returning, so the
    /// resume state on disk is always accurate up to the file currently in flight, the same
    /// guarantee ProcessLaneAsync used to make for a whole lane. Mutates context in place (config
    /// reload, derived paths) so this lane's NEXT call - however many other lanes' files are
    /// processed in between - picks up mid-run settings changes exactly like before.
    ///
    /// cancellationToken (Abort) kills the in-flight encoder process immediately -
    /// the encoder runner registers a Kill(entireProcessTree) callback directly on it. The
    /// caller is responsible for checking the separate, gentler stopToken (Stop Monitoring) BEFORE
    /// calling this method for the next file - once a file is in flight here, it always finishes
    /// completely.</summary>
    Task<ConversionResult> ProcessOneFileAsync(
        LaneProcessingContext context,
        ResumeEntry resumeEntry,
        string logFilePath,
        string timestamp,
        List<ResumeEntry> resumeState,
        string resumeFilePath,
        CancellationToken cancellationToken);

    /// <summary>Merges Order/Skipped/PresetOverride/Removed from whatever is currently on disk onto
    /// the matching entries in resumeState (by LaneId+FullName), mutating resumeState in place.
    /// Deliberately narrow: only those four user-editable fields are ever touched, never Status or
    /// EncodedFilePath, so this can never resurrect/misclassify an entry actively being driven
    /// through ProcessOneFileAsync's own state machine - it only pulls in what a queue-control web
    /// request (reorder/skip/preset-override/remove) could actually have changed. A disk entry with
    /// no in-memory match (a freshly-scanned file the user acted on before it was ever tracked) is
    /// adopted as a new entry. The caller is responsible for calling this before picking the next
    /// file to process, across every lane - see RunOrchestrator's global picking loop.</summary>
    void RefreshResumeState(List<ResumeEntry> resumeState, string resumeFilePath);

    /// <summary>Removes any resume entry whose LaneId doesn't match any currently-configured lane -
    /// the lane it belonged to was since renamed or deleted. Every one of PrepareLaneAsync's own
    /// dead-entry/retry loops is scoped to `e.LaneId == lane.Id` while iterating config.Lanes, so an
    /// entry like this is invisible to all of them and would otherwise sit in resume.json forever:
    /// inflating "Resuming previous incomplete run"'s tracked count on every pass and permanently
    /// blocking RunOrchestrator's end-of-pass stillOutstanding check from ever wiping the file once
    /// every real lane's own work is done. Called once per pass, before that log line, so both the
    /// count and the end-of-pass check reflect only lanes that still exist.</summary>
    void PruneOrphanedLaneEntries(CompressarrConfig config, List<ResumeEntry> resumeState, string resumeFilePath);

    /// <summary>One-time migration: gives every pre-existing Pending entry that still lacks an
    /// Order (i.e. it predates ResumeEntry.Order being stamped automatically at creation time) a
    /// permanent one, computed by freezing today's own effective display order - the same
    /// Order-null -> lane's position in config.Lanes -> natural per-lane scan order tie-break
    /// RunEndpoints.ComputeUpNext already computes independently - as everyone's new permanent
    /// position. A cheap no-op on every call after the first: bails immediately once every Pending
    /// entry already has one. Called once per pass, before any lane is prepared, so the ordering it
    /// freezes reflects the queue exactly as it looked before this pass could touch anything.</summary>
    void BackfillMissingOrder(CompressarrConfig config, List<ResumeEntry> resumeState, string resumeFilePath);
}

public sealed class ConversionOrchestrator : IConversionOrchestrator
{
    private const long BytesPerGb = 1024L * 1024L * 1024L;

    private readonly IPathExpander _pathExpander;
    private readonly IVideoFileScanner _scanner;
    private readonly IFileBotRunner _fileBotRunner;
    private readonly IEncoderPresetService _presets;
    private readonly IMetadataService _metadata;
    private readonly IEncoderRunner _processRunner;
    private readonly IFileRouter _fileRouter;
    private readonly ICompanionFileService _companionFiles;
    private readonly IArrUnmonitorService _arrUnmonitor;
    private readonly ITrashService _trash;
    private readonly IRunLogger _logger;
    /// <summary>The pass (its timestamp) for which the "HDR check skipped" notice was last logged, so each pass says it once.</summary>
    private string? _hdrCheckSkippedNotedFor;
    private readonly IResumeStateStore _resumeStore;
    private readonly IRunProgressReporter _progress;
    private readonly IConfigStore _configStore;
    private readonly RetryRecovery _retryRecovery;
    private readonly SourceCleanupCoordinator _cleanup;
    private readonly EncodedFileFinisher _finisher;

    /// <summary>Picks the encoder per lane (2.2). Null means HandBrake only, exactly as before - the
    /// tests that build this class directly never need to supply one.</summary>
    private readonly IEncoderResolver? _encoders;

    /// <summary>The "encoder" behind the Passthrough preset: copies the file instead of re-encoding it. Created here rather than injected: the
    /// service container already maps IEncoderRunner to the HandBrake runner, so an injected parameter of that type would be the wrong one.</summary>
    private readonly IEncoderRunner _passthroughRunner = new PassthroughRunner();

    public ConversionOrchestrator(
        IPathExpander pathExpander,
        IVideoFileScanner scanner,
        IFileBotRunner fileBotRunner,
        IEncoderPresetService presets,
        IMetadataService metadata,
        IEncoderRunner processRunner,
        IFileRouter fileRouter,
        ICompanionFileService companionFiles,
        IArrUnmonitorService arrUnmonitor,
        ITrashService trash,
        IRunLogger logger,
        IResumeStateStore resumeStore,
        IRunProgressReporter progress,
        IConfigStore configStore,
        IEncoderResolver? encoders = null)
    {
        _encoders = encoders;
        _pathExpander = pathExpander;
        _scanner = scanner;
        _fileBotRunner = fileBotRunner;
        _presets = presets;
        _metadata = metadata;
        _processRunner = processRunner;
        _fileRouter = fileRouter;
        _companionFiles = companionFiles;
        _arrUnmonitor = arrUnmonitor;
        _trash = trash;
        _logger = logger;
        _resumeStore = resumeStore;
        _progress = progress;
        _configStore = configStore;
        _retryRecovery = new RetryRecovery(pathExpander, fileRouter, companionFiles, arrUnmonitor, trash, logger, resumeStore);
        _cleanup = new SourceCleanupCoordinator(arrUnmonitor, companionFiles, logger);
        _finisher = new EncodedFileFinisher(pathExpander, metadata, fileRouter, companionFiles, trash, logger, _cleanup);
    }

    public async Task<LaneProcessingContext?> PrepareLaneAsync(
        LaneConfig lane,
        CompressarrConfig config,
        List<ResumeEntry> resumeState,
        string resumeFilePath,
        List<ReportErrorCode>? reportProblems = null,
        CancellationToken cancellationToken = default)
    {
        var inputPath = _pathExpander.Expand(lane.Input);
        var outputBase = _pathExpander.Expand(lane.Output);
        var tvShowBasePath = _pathExpander.Expand(lane.TvShowBasePath);
        var movieBasePath = _pathExpander.Expand(lane.MovieBasePath);

        if (string.IsNullOrWhiteSpace(inputPath) || !Directory.Exists(inputPath))
        {
            return null;
        }

        var hbloc = _pathExpander.Expand(config.HandBrake.CliPath);

        // LaneValidator is the single source of truth for "does this lane have an Output folder
        // configured" - shared with RunOrchestrator's own lane-prep loop and the Lanes page's own
        // validation (LaneEndpoints.cs). Only the "output" finding is acted on here - the
        // preset-related findings this same call also returns were already checked and logged by
        // RunOrchestrator right before it called PrepareLane; re-deriving them here would just be
        // redundant, not wrong, but there's nothing useful to do with a second copy of the same
        // warning.
        if (LaneValidator.Validate(lane, config, _pathExpander, _presets).Any(i => i.Field == "output"))
        {
            _logger.LogProblem($"lane-no-output:{lane.Id}", $"Lane '{lane.DisplayName}' has no Output folder configured and 'write output to same folder as input' is off - skipping.");
            reportProblems?.Add(ReportErrorCode.LaneNoOutputConfigured);
            return null;
        }
        _logger.ClearProblem($"lane-no-output:{lane.Id}");

        List<FileInfo> videoFiles;
        var retriesSucceeded = 0;

        // Everything left unfinished from earlier passes (see RetryRecovery) is resolved before this
        // pass scans for anything new.
        retriesSucceeded = await _retryRecovery.RecoverAsync(lane, config, inputPath, tvShowBasePath, movieBasePath, resumeState, resumeFilePath, cancellationToken);

        // Optional pre-processing pass (FileBot) - no-ops instantly if disabled. Runs before the
        // real scan below so anything it renamed/organized is what Compressarr's own classifier
        // and scan actually see; whatever it left completely untouched is flagged on the fresh
        // ResumeEntry created below, driving the Monitor page's "Unmatched" badge. CliPath needs
        // expanding the same way HandBrake's own CliPath is above (config.FileBot.CliPath can
        // carry a %ProgramFiles%-style token) - IFileBotRunner itself never expands anything, same
        // division of responsibility as the HandBrakeCLI path.
        var expandedFileBotSettings = new FileBotSettings
        {
            Enabled = config.FileBot.Enabled,
            CliPath = _pathExpander.Expand(config.FileBot.CliPath),
            TvEnabled = config.FileBot.TvEnabled,
            TvArgs = config.FileBot.TvArgs,
            MovieEnabled = config.FileBot.MovieEnabled,
            MovieArgs = config.FileBot.MovieArgs
        };
        // Same condition IFileBotRunner.Run itself checks (and logs via LogProblem) - duplicated
        // here rather than having Run report it back, since Run's own return value is just the
        // unmatched-files set and changing that shape for one caller's report-visibility need
        // wasn't worth it. Kept simple and equivalent; if IFileBotRunner's own check ever changes,
        // this one needs to move with it.
        if (config.FileBot.Enabled && (string.IsNullOrWhiteSpace(expandedFileBotSettings.CliPath) || !File.Exists(expandedFileBotSettings.CliPath)))
        {
            reportProblems?.Add(ReportErrorCode.FileBotPathNotFound);
        }
        // A live UI otherwise has nothing to show between LaneStarted and the first FileStarted -
        // FileBot's own lookups can take real wall-clock time (a live TheTVDB/TMDB call), during
        // which the Monitor page would look stuck/idle without this.
        if (config.FileBot.Enabled)
        {
            _progress.FileBotStarted(lane.Id);
            _logger.Log("Renaming files with FileBot...");
        }
        var fileBotResult = _fileBotRunner.Run(expandedFileBotSettings, inputPath, config.Processing.VidTypes, _logger);
        var fileBotUnmatched = fileBotResult.Unmatched;
        if (config.FileBot.Enabled)
        {
            _progress.FileBotCompleted(lane.Id);
        }

        // Always scan (recursively - a lane's files can be nested in subfolders, e.g. one per
        // movie) so the natural fallback order for entries with no explicit user-set Order matches
        // exactly what RunEndpoints.ComputeUpNext independently computes the same way for the
        // Monitor page's own queue display - both must never disagree about "what's next" for a
        // file nobody has ever dragged.
        var scanned = _scanner.FindVideoFiles(inputPath, config.Processing.VidTypes, config.Processing.MinSizeBytes, config.Processing.Limit).ToList();
        var naturalOrderIndex = QueueRules.NaturalIndexMap(scanned);

        // FileBot above (and any earlier lane's own prep) can take real wall-clock time, during
        // which the Monitor page's own poll may have already tracked brand-new files and stamped
        // their Order on disk. Merge that in before adding anything or saving below - otherwise the
        // blind Save at the end of this method would silently wipe those entries out, and they'd
        // have to be re-stamped later, out of the order they actually arrived in.
        RefreshResumeState(resumeState, resumeFilePath);

        // A file FileBot just renamed keeps its place in line (and any skip / preset override)
        // rather than being tracked as a new arrival at the end - this has to run after the refresh
        // above (so a user edit made to the old name is already merged in) and before tracking below
        // (so the renamed file isn't added as new).
        var remappedRenames = QueueRules.RemapRenamedFiles(resumeState, lane.Id, fileBotResult.Renames);

        // Which files are tracked, in what order, and what happens when one reappears all live in
        // QueueRules - see its TrackScannedFiles for the reasoning behind each case.
        var laneHadPending = resumeState.Any(e => e.LaneId == lane.Id && e.Status == ResumeStatus.Pending);
        var addedUntracked = QueueRules.TrackScannedFiles(resumeState, lane.Id, scanned, fileBotUnmatched, laneHadPending);

        if (laneHadPending)
        {
            // Processing order drives this count: user-set/stamped Order first (see QueueRules).
            videoFiles = QueueRules.WaitingEntriesInOrder(resumeState, lane.Id)
                .Select(p => new FileInfo(p.FullName))
                .ToList();
            if (addedUntracked || remappedRenames) _resumeStore.Save(resumeState, resumeFilePath);
        }
        else
        {
            // A file held after an encode failure is shown with its ERROR badge but is not work to do.
            var heldPaths = resumeState.Where(e => e.LaneId == lane.Id && e.HeldAfterFailure).Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var keptSources = ResumeKeeping.KeptSources(resumeState, lane.Id);
            videoFiles = scanned
                .Where(f => !heldPaths.Contains(f.FullName))
                .Where(f => !ResumeKeeping.AlreadyConverted(keptSources, f)) // converted already, source kept (Maintain) and unchanged
                .ToList();
            _resumeStore.Save(resumeState, resumeFilePath);
        }

        var fileCount = videoFiles.Count;
        var padSize = fileCount.ToString().Length;

        return new LaneProcessingContext
        {
            Lane = lane,
            Config = config,
            InputPath = inputPath,
            OutputBase = outputBase,
            TvShowBasePath = tvShowBasePath,
            MovieBasePath = movieBasePath,
            HbLoc = hbloc,
            FileIndex = 0,
            FileTotal = fileCount,
            PadSize = padSize,
            NaturalOrderIndex = naturalOrderIndex,
            RetriesSucceeded = retriesSucceeded
        };
    }

    public async Task<ConversionResult> ProcessOneFileAsync(
        LaneProcessingContext context,
        ResumeEntry resumeEntry,
        string logFilePath,
        string timestamp,
        List<ResumeEntry> resumeState,
        string resumeFilePath,
        CancellationToken cancellationToken)
    {
        context.FileIndex++;
        var i = context.FileIndex;
        var fileCount = context.FileTotal;
        var padSize = context.PadSize;
        var lane = context.Lane;
        var config = context.Config;
        var inputPath = context.InputPath;
        var outputBase = context.OutputBase;
        var tvShowBasePath = context.TvShowBasePath;
        var movieBasePath = context.MovieBasePath;
        var hbloc = context.HbLoc;

        var file = new FileInfo(resumeEntry.FullName);

        var isTv = ContentClassifier.IsTvFile(file.Name);
        var contentType = isTv ? "TV Show" : "Movie";
        // A per-file preset override (set from the Monitor page's queue) wins over the lane's own
        // TvPreset/MoviePreset for this one entry only - looked up before presetName is used
        // anywhere (logging, the FileStarted progress event, the "no preset" check) so all of it
        // reflects the actual preset this file will encode with.
        var presetName = !string.IsNullOrWhiteSpace(resumeEntry.PresetOverride)
            ? resumeEntry.PresetOverride
            : (isTv ? lane.TvPreset : lane.MoviePreset);

        var beginSizeGb = Math.Round(file.Length / (double)BytesPerGb, 3);
        var startTime = DateTime.Now;

        _logger.FileStart(lane.DisplayName, i, fileCount, file.Name, beginSizeGb, contentType, presetName);
        _progress.FileStarted(lane.Id, i, fileCount, file.Name, file.FullName, presetName, beginSizeGb);

        if (string.IsNullOrWhiteSpace(presetName))
        {
            _logger.Log($"  No {contentType} preset configured for this lane - skipping.", LogSeverity.Error);
            resumeEntry.Status = ResumeStatus.Error;
            SaveEntryResult(resumeEntry, resumeFilePath);

            return new ConversionResult
            {
                LaneId = lane.Id,
                FileName = file.Name,
                FullName = file.FullName,
                ContentType = contentType,
                PresetName = presetName,
                BeginSizeGb = beginSizeGb,
                EndSizeGb = 0,
                Success = false,
                FailureReason = $"No {contentType} preset configured for this lane",
                ErrorCode = ReportErrorCode.NoPresetConfigured,
                StartTime = startTime,
                EndTime = DateTime.Now
            };
        }

        // Which encoder runs this file: the lane's own (HandBrake unless the lane says ffmpeg). With no
        // resolver (HandBrake-only wiring, and every test that builds this class directly) it is the
        // HandBrake runner and preset service exactly as before 2.2.
        var binding = _encoders?.For(lane.Engine, config)
            ?? new EncoderBinding(EncoderEngine.HandBrake, _processRunner, _presets, hbloc, null, config.HandBrake.Options);
        string? encoderLabel = binding.Engine == EncoderEngine.FFmpeg ? "ffmpeg" : null;
        MediaProbeResult? sourceProbe = null;

        // The Passthrough preset: the file is copied, not re-encoded, so nothing that guards an encode (the ffmpeg / HandBrake HDR checks, the
        // profile file, an output extension) applies, and everything after the "encode" - routing, the source delete/recycle/keep setting,
        // companions, Sonarr/Radarr, the title-tag clearing - runs exactly as it does for an encoded file.
        var isPassthrough = PassthroughPreset.Is(presetName);
        if (isPassthrough)
        {
            binding = binding with { Runner = _passthroughRunner };
            encoderLabel = "passthrough";
            _logger.Log("  Passthrough: the file is copied, not re-encoded.");
        }

        if (!isPassthrough && binding.Engine == EncoderEngine.FFmpeg && _encoders is not null)
        {
            // Stock ffmpeg + libx265 silently drops Dolby Vision / HDR10+ dynamic metadata, so such a
            // file goes to HandBrake (which carries it through) using the ffmpeg profile's fallback
            // preset - or is refused. It is never encoded in a way that quietly loses the HDR, except when the
            // profile explicitly says to drop it (and the file has a base layer that still looks right).
            var probe = await _encoders.ProbeAsync(config, file.FullName, cancellationToken);
            sourceProbe = probe;
            if (probe is { HasDynamicHdrMetadata: true })
            {
                var fallback = _encoders.FallbackHandBrakePreset(presetName);
                var handBrake = _encoders.For(EncoderEngine.HandBrake, config);
                var handBrakeProblem = _encoders.NotReadyReason(EncoderEngine.HandBrake, config);

                if (fallback is not null && handBrakeProblem is null && handBrake.Presets.PresetExists(fallback))
                {
                    _logger.Log($"  {probe.DynamicHdrName} detected - ffmpeg would drop it, so this file is encoded by HandBrake with '{fallback}' instead of '{presetName}'.");
                    binding = handBrake;
                    presetName = fallback;
                    encoderLabel = $"HandBrake ({probe.DynamicHdrName} fallback)";
                    _progress.FileStarted(lane.Id, i, fileCount, file.Name, file.FullName, presetName, beginSizeGb);
                }
                else if (fallback is null && _encoders.AllowsDroppingDynamicHdr(presetName) && probe.DynamicHdrCanBeDropped)
                {
                    // The user chose this: the output keeps the HDR10 base layer, not the dynamic metadata. The report tag says so.
                    _logger.Log($"  {probe.DynamicHdrName} detected - this profile is set to encode anyway, so it is dropped (the output keeps the HDR10 base layer).");
                    encoderLabel = $"ffmpeg ({probe.DynamicHdrName} dropped)";
                }
                else
                {
                    var why = fallback is null && _encoders.AllowsDroppingDynamicHdr(presetName)
                            ? $"this Dolby Vision file ({(probe.DolbyVisionProfileLabel.Length > 0 ? probe.DolbyVisionProfileLabel : "profile unknown")}) has no HDR10-compatible base layer, so encoding without Dolby Vision would give wrong colours"
                        : fallback is null ? "the profile has no HandBrake fallback preset"
                        : handBrakeProblem is not null ? handBrakeProblem
                        : $"the HandBrake preset '{fallback}' doesn't exist";
                    var message = $"{probe.DynamicHdrName} dynamic metadata would be lost by ffmpeg, and {why} - the file was left alone.";
                    _logger.Log($"  {message}", LogSeverity.Error);
                    resumeEntry.Status = ResumeStatus.Error;
                    // Held, like a failed encode: nothing about the file changes by itself, so retrying it every pass would only
                    // repeat the same refusal (and a new error report each time) until the profile is changed.
                    resumeEntry.HeldAfterFailure = true;
                    SaveEntryResult(resumeEntry, resumeFilePath);

                    return new ConversionResult
                    {
                        LaneId = lane.Id,
                        FileName = file.Name,
                        FullName = file.FullName,
                        ContentType = contentType,
                        PresetName = presetName,
                        EncoderLabel = "ffmpeg",
                        BeginSizeGb = beginSizeGb,
                        EndSizeGb = 0,
                        Success = false,
                        FailureReason = message,
                        ErrorCode = ReportErrorCode.DynamicHdrNeedsHandBrake,
                        StartTime = startTime,
                        EndTime = DateTime.Now,
                        HomeLaneName = lane.DisplayName
                    };
                }
            }
        }

        // The HandBrake side of the same rule (a lane that runs HandBrake, or a file an ffmpeg lane handed to it): an HDR / Dolby Vision
        // file is not re-encoded with a profile whose video encoder would lose the HDR - and then replace the original. A profile
        // whose encoder keeps everything (x265, SVT-AV1 with dynamic metadata on) is never probed, so those lanes pay nothing.
        if (!isPassthrough && binding.Engine == EncoderEngine.HandBrake && _encoders is not null
            && _encoders.HandBrakeTraits(presetName) is { } hdrTraits && HandBrakeHdrGuard.NeedsProbe(hdrTraits))
        {
            sourceProbe ??= await _encoders.ProbeAsync(config, file.FullName, cancellationToken);
            if (sourceProbe is null)
            {
                if (_hdrCheckSkippedNotedFor != timestamp)
                {
                    _hdrCheckSkippedNotedFor = timestamp;
                    _logger.Log("  HDR check skipped: ffprobe was not found, so HDR files can't be recognised before encoding (install ffmpeg on the Encoder page to turn it on).");
                }
            }
            else if (HandBrakeHdrGuard.RefusalReason(sourceProbe, hdrTraits, lane.AllowHdrLoss) is { } hdrWhy)
            {
                var hdrMessage = $"Not encoded: {hdrWhy} - the file was left alone.";
                _logger.Log($"  {hdrMessage}", LogSeverity.Error);
                resumeEntry.Status = ResumeStatus.Error;
                resumeEntry.HeldAfterFailure = true; // nothing about the file changes by itself; see the ffmpeg refusal above
                SaveEntryResult(resumeEntry, resumeFilePath);

                return new ConversionResult
                {
                    LaneId = lane.Id,
                    FileName = file.Name,
                    FullName = file.FullName,
                    ContentType = contentType,
                    PresetName = presetName,
                    EncoderLabel = encoderLabel,
                    BeginSizeGb = beginSizeGb,
                    EndSizeGb = 0,
                    Success = false,
                    FailureReason = hdrMessage,
                    ErrorCode = ReportErrorCode.ProfileCannotKeepHdr,
                    StartTime = startTime,
                    EndTime = DateTime.Now,
                    HomeLaneName = lane.DisplayName
                };
            }
        }

        string? extensionWarning = null;
        var extension = isPassthrough ? file.Extension : binding.Presets.GetOutputExtension(presetName, out extensionWarning);
        if (extensionWarning is not null) _logger.Log(extensionWarning, LogSeverity.Error);

        var destFolder = config.Processing.OutSameAsIn ? file.DirectoryName! : outputBase;
        Directory.CreateDirectory(destFolder);

        var baseName = Path.GetFileNameWithoutExtension(file.Name);
        // Nominal/display path only, NOT an actual staging target any more - HandBrake writes
        // directly to tempFileName's own collision-safe GUID name below, which stays the file's
        // real on-disk location until routing (or resting in Output) actually resolves it to this
        // deterministic name. Used only for its own leaf (desiredFileName, see the success branch
        // below) and as a nominal fallback for the encode-failure case, where nothing was ever
        // physically written here. A deterministic Output filename that gets silently overwritten
        // by File.Move(..., overwrite: true) before routing even knows whether it will succeed was
        // itself a real bug (v2.1.3 code review finding #4) - a still-pending MoveFailed entry from
        // an earlier attempt could be clobbered by a later, unrelated conversion landing on the
        // exact same name while both were sitting in Output.
        var newFileName = Path.Combine(destFolder, baseName + extension);
        var tempFileName = Path.Combine(destFolder, baseName + ".compressarr-" + Guid.NewGuid().ToString("N")[..8] + extension);

        var logName = $"{baseName}_{timestamp}_{i.ToString().PadLeft(padSize, '0')}_{(isPassthrough ? "PTdetails" : binding.Engine == EncoderEngine.FFmpeg ? "FFdetails" : "HBdetails")}.txt";
        var detailLogFile = Path.Combine(logFilePath, logName);

        var lastLoggedPercent = -10.0;
        void OnProgress(EncodeProgress progress)
        {
            _progress.FileProgress(lane.Id, progress.Percent, progress.Fps, progress.Eta);

            // HandBrakeCLI emits a progress line roughly once a second - logging every one of them
            // would flood the recent-log window, so only a real 10% step gets written.
            if (progress.Percent - lastLoggedPercent >= 10.0)
            {
                lastLoggedPercent = progress.Percent;
                var etaSuffix = progress.Eta is not null ? $", ETA {progress.Eta}" : "";
                var fpsSuffix = progress.Fps is not null ? $", {progress.Fps:0.0} fps" : "";
                _logger.Log($"  {progress.Percent:0.0}%{fpsSuffix}{etaSuffix}");
            }
        }

        // Brings the generated profile file up to date (a no-op unless a profile changed since it
        // was last written) right before the encoder is started.
        var presetSource = isPassthrough ? "" : binding.Presets.PreparePresetSource();

        var runResult = await binding.Runner.RunAsync(
            new EncodeRequest(binding.ToolPath, file.FullName, tempFileName, presetSource, presetName, binding.ExtraOptions, detailLogFile, binding.ProbePath),
            OnProgress,
            cancellationToken);
        var endTime = DateTime.Now;

        // Settings are otherwise only read once, at the start of a manual run or for the whole
        // lifetime of the monitoring loop - a change made mid-run (delete-after-convert mode, clear
        // title metadata, a lane's base path, etc.) would otherwise have no effect until the run/
        // loop is restarted. Re-read as soon as HandBrakeCLI is done for this file, before any of
        // the post-processing below, so it takes effect on this file's own routing/cleanup and on
        // every file still to come in this lane, however many OTHER lanes' files get processed in
        // between - written back onto context so this lane's next call sees it, not just local
        // variables that would otherwise be lost between non-contiguous visits to this lane.
        config = _configStore.Load(AppPaths.GetConfigFilePath());
        lane = config.Lanes.FirstOrDefault(l => l.Id == lane.Id) ?? lane;
        outputBase = _pathExpander.Expand(lane.Output);
        tvShowBasePath = _pathExpander.Expand(lane.TvShowBasePath);
        movieBasePath = _pathExpander.Expand(lane.MovieBasePath);
        hbloc = _pathExpander.Expand(config.HandBrake.CliPath);
        _metadata.Enabled = config.Processing.ClearTitleMetadata;
        context.Lane = lane;
        context.Config = config;
        context.OutputBase = outputBase;
        context.TvShowBasePath = tvShowBasePath;
        context.MovieBasePath = movieBasePath;
        context.HbLoc = hbloc;

        // Same reasoning as the config reload just above, for resume state: pull in whatever a
        // queue-control request changed on OTHER entries while this file was encoding, so the save
        // below (and whichever entry gets picked next, across any lane) doesn't clobber/ignore it.
        // Never touches resumeEntry's own Status - only RefreshResumeState's own three fields.
        RefreshResumeState(resumeState, resumeFilePath);

        if (runResult.Cancelled)
        {
            try { File.Delete(tempFileName); }
            catch (Exception ex) { _logger.Log($"  Unable to remove temporary file '{tempFileName}': {ex.Message}", LogSeverity.Error); }
            _logger.Log($"  Conversion of '{file.Name}' aborted by user.", LogSeverity.Error);
            resumeEntry.Status = ResumeStatus.Error;
            SaveEntryResult(resumeEntry, resumeFilePath);
            cancellationToken.ThrowIfCancellationRequested();
        }

        var success = runResult.Success;

        double endSizeGb = 0;
        string? arrStatus = null;
        string? finalFileName = newFileName;
        var moveFailed = false;
        var diskFull = false;
        LaneConfig? redirectedTo = null;
        string? failureReason = null;
        ReportErrorCode? errorCode = null;
        string? postProcessWarning = null;

        if (success)
        {
            var finish = await _finisher.FinishAsync(
                new FinishRequest(config, resumeEntry, file, isTv, tempFileName, newFileName, destFolder, tvShowBasePath, movieBasePath, inputPath),
                cancellationToken);
            endSizeGb = finish.EndSizeGb;
            arrStatus = finish.ArrStatus;
            finalFileName = finish.FinalFileName;
            moveFailed = finish.MoveFailed;
            diskFull = finish.DiskFull;
            failureReason = finish.FailureReason;
            errorCode = finish.ErrorCode;
            postProcessWarning = finish.PostProcessWarning;
            redirectedTo = finish.RedirectedTo;
        }
        else
        {
            try { File.Delete(tempFileName); }
            catch (Exception ex) { _logger.Log($"  Unable to remove temporary file '{tempFileName}': {ex.Message}", LogSeverity.Error); }
            resumeEntry.Status = ResumeStatus.Error;
            // HandBrake's own detail log is genuinely the relevant diagnostic for an encode
            // failure (unlike a move failure, where it's irrelevant) - stays linked from the
            // report for this code.
            errorCode = runResult.Failure == EncodeFailureKind.LengthMismatch
                ? ReportErrorCode.EncodedLengthMismatch
                : ReportErrorCode.EncodeFailed;

            // A plain encode failure is held for the user to look at; see ResumeEntry.HeldAfterFailure.
            resumeEntry.HeldAfterFailure = errorCode == ReportErrorCode.EncodeFailed;
            if (runResult.Failure != EncodeFailureKind.None && runResult.FailureMessage is not null)
            {
                failureReason = runResult.FailureMessage;
                _logger.Log($"  {runResult.FailureMessage}", LogSeverity.Error);
            }

            // HandBrakeCLI still writes its own log even on a failed encode - confirmed live
            // against a genuinely full disk that its mux error names the cause explicitly ("No
            // space left on device"), which is a much more specific signal than just "the encode
            // failed" (which covers plenty of other, unrelated causes too).
            if (File.Exists(detailLogFile) && File.ReadLines(detailLogFile).Any(l => LooksLikeDiskFull(l)))
            {
                diskFull = true;
                failureReason = "Output drive full, monitoring stopped";
            }
        }

        // "Keep Logs of successful HandBrake Encodes" (Settings, off by default): a successful
        // encode's own HBdetails.txt has nothing more to say once every other step for this file
        // is done - unlike a failed encode's (still linked from the report, kept regardless of
        // this setting), or a move-retry's (the retry never re-invokes HandBrake, so this is the
        // only detail log that file will ever get - deleting it here based on encode success
        // alone, before knowing whether the move itself will succeed, is still correct: the report
        // never links a move failure's HandBrake log anyway, per ReportErrorCode's own design).
        // Best-effort, same as every other cleanup-only delete in this method - a locked file
        // (an antivirus scan, etc.) shouldn't fail an otherwise-successful file's processing.
        if (success && !config.Logging.KeepSuccessfulHandBrakeLogs)
        {
            try { if (File.Exists(detailLogFile)) File.Delete(detailLogFile); }
            catch (Exception ex) { _logger.Log($"  Unable to remove HandBrake detail log '{detailLogFile}': {ex.Message}", LogSeverity.Error); }
        }

        var duration = endTime - startTime;
        if (duration < TimeSpan.Zero) duration = duration.Negate();

        // The encode can succeed while the file still doesn't end up where it's supposed to (an
        // unreachable TV/Movie base path) - reflect that in this file's own reported status rather
        // than only in the log, so a report/History reader isn't told "OK" for a file that's
        // actually sitting unfiled in the Output folder.
        var overallSuccess = success && !moveFailed;

        _logger.FileComplete(finalFileName ?? newFileName, beginSizeGb, endSizeGb, duration, overallSuccess, detailLogFile);
        _progress.FileCompleted(lane.Id, finalFileName ?? newFileName, overallSuccess);
        _progress.FileThroughputSample(presetName, beginSizeGb, duration);

        SaveEntryResult(resumeEntry, resumeFilePath);

        return new ConversionResult
        {
            LaneId = lane.Id,
            FileName = file.Name,
            FullName = file.FullName,
            NewFileName = finalFileName,
            ContentType = contentType,
            PresetName = presetName,
            EncoderLabel = encoderLabel,
            IsPassthrough = isPassthrough,
            BeginSizeGb = beginSizeGb,
            EndSizeGb = endSizeGb,
            Success = overallSuccess,
            DiskFull = diskFull,
            FailureReason = failureReason,
            ErrorCode = errorCode,
            DetailLogFile = detailLogFile,
            StartTime = startTime,
            EndTime = endTime,
            ArrStatus = arrStatus,
            PostProcessWarning = postProcessWarning,
            HomeLaneName = lane.DisplayName,
            RedirectedToLaneId = redirectedTo?.Id,
            RedirectedToLaneName = redirectedTo?.DisplayName
        };
    }


    public void RefreshResumeState(List<ResumeEntry> resumeState, string resumeFilePath) =>
        QueueRules.MergeUserEdits(resumeState, _resumeStore.Load(resumeFilePath));

    public void PruneOrphanedLaneEntries(CompressarrConfig config, List<ResumeEntry> resumeState, string resumeFilePath)
    {
        var configuredLaneIds = config.Lanes.Select(l => l.Id).ToHashSet();
        var orphaned = resumeState.Where(e => !configuredLaneIds.Contains(e.LaneId)).ToList();
        if (orphaned.Count == 0) return;

        foreach (var entry in orphaned)
        {
            _logger.Log($"Resume entry for '{entry.FullName}' belongs to a lane that no longer exists - removing it.", LogSeverity.Info);
            resumeState.Remove(entry);
        }
        _resumeStore.Save(resumeState, resumeFilePath);
    }

    public void BackfillMissingOrder(CompressarrConfig config, List<ResumeEntry> resumeState, string resumeFilePath)
    {
        if (QueueRules.BackfillMissingOrder(config, resumeState, _pathExpander, _scanner, message => _logger.Log(message, LogSeverity.Error)))
        {
            _resumeStore.Save(resumeState, resumeFilePath);
        }
    }

    /// <summary>Persists this one file's own processing-owned fields (Status, EncodedFilePath,
    /// EncodedFileDesiredName, PendingCompanionSourceDirectory, PendingCompanionVideoDestPath,
    /// LastRetryFailureMessage, HeldAfterFailure, KeptSourceSize/ModifiedUtc - see ResumeResults) onto
    /// the freshest disk copy of resume.json, instead of blindly overwriting the whole file with
    /// ProcessOneFileAsync's own in-memory resumeState snapshot. Code-review finding: that snapshot
    /// is only fresh as of RefreshResumeState's own one-time call right after HandBrake finishes -
    /// routing, companion-file movement, and the Sonarr/Radarr rescan-confirmation wait (up to
    /// IArrUnmonitorService's own multi-minute poll window) all still run AFTER that refresh and
    /// BEFORE this save. A queue-control edit (reorder/skip/preset-override/remove) to ANY entry -
    /// not just this file's own - made anywhere in that window was previously silently lost the
    /// instant a blind Save(resumeState, ...) ran, even though the queue endpoint's own write to
    /// disk had already succeeded moments earlier. Never touches the UI-owned fields (Order/
    /// Skipped/PresetOverride/Removed/CreatedByQueueEdit) on the disk entry - whatever's freshest
    /// there for those wins, the same merge direction RefreshResumeState above already uses, just
    /// going the other way for the fields this method actually owns.</summary>
    private void SaveEntryResult(ResumeEntry resumeEntry, string resumeFilePath) => ResumeResults.Save(_resumeStore, resumeEntry, resumeFilePath);
}
