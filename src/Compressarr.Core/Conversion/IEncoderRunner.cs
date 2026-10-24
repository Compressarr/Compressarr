namespace Compressarr.Core.Conversion;

/// <summary>Everything an encoder needs to convert one file. Deliberately engine-neutral: any
/// encoder has an executable, a source and a destination, a named preset drawn from some catalog,
/// and some free-form extra options - what those mean (HandBrake's presets.json and CLI flags, or
/// another engine's own profile file and arguments) is the runner's business, not the engine's.</summary>
/// <param name="ToolPath">The encoder's executable, already path-expanded.</param>
/// <param name="SourcePath">The file to encode.</param>
/// <param name="OutputPath">Where to write the result (the engine's own collision-safe temp name).</param>
/// <param name="PresetSource">Where the preset catalog lives (HandBrake: the generated presets file).</param>
/// <param name="PresetName">The preset to encode with.</param>
/// <param name="ExtraOptions">Free-form extra command-line options, or null.</param>
/// <param name="DetailLogFile">Where the runner writes the encoder's own detail log.</param>
/// <param name="ProbePath">A second tool some engines need (ffmpeg: ffprobe, to read the source and verify
/// the result); HandBrake ignores it.</param>
public sealed record EncodeRequest(
    string ToolPath,
    string SourcePath,
    string OutputPath,
    string PresetSource,
    string PresetName,
    string? ExtraOptions,
    string DetailLogFile,
    string? ProbePath = null);

/// <summary>One live progress reading, already parsed from the encoder's own output.</summary>
public sealed record EncodeProgress(double Percent, double? Fps, string? Eta);

/// <summary>Why an encode failed, when the runner can say more than "it failed".</summary>
public enum EncodeFailureKind
{
    /// <summary>Not a failure, or no more specific reason than the encoder itself failing.</summary>
    None,

    /// <summary>The encoder exited normally but the file it wrote is much shorter/longer than the source
    /// (a corrupt source, a full disk): caught before the original is touched.</summary>
    LengthMismatch,

    /// <summary>The encode could not even start (profile missing, source unreadable, no video stream).</summary>
    NotStarted
}

/// <param name="AvgFps">The encoder's own running average frames per second at the end of a successful encode (HandBrake's "avg" reading, ffmpeg's
/// last "fps"), or null when it never gave one or the encode did not succeed. A rough figure by design; null is never read as 0.</param>
public sealed record EncodeResult(bool Success, string DetailLogFile, bool Cancelled = false, EncodeFailureKind Failure = EncodeFailureKind.None, string? FailureMessage = null, double? AvgFps = null);

public interface IEncoderRunner
{
    /// <summary>Encodes one file; it fully finishes before the caller moves on. Success is decided
    /// by the runner using whatever that encoder reliably reports (for HandBrake: the output file
    /// exists and is non-empty, the "Finished work at" banner is in the log, AND the exit code is
    /// 0 - see HandBrakeProcessRunner.DetermineSuccess for why all three are needed).
    ///
    /// Reports live progress through onProgress as the encoder emits it. Cancelling
    /// cancellationToken kills the encoder's process tree immediately and returns a result with
    /// Cancelled=true instead of throwing - the caller decides what "aborted mid-file" means for the
    /// rest of the run.</summary>
    Task<EncodeResult> RunAsync(EncodeRequest request, Action<EncodeProgress>? onProgress, CancellationToken cancellationToken);
}
