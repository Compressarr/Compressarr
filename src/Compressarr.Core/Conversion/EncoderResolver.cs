using Compressarr.Core.Config;
using Compressarr.Core.FFmpeg;
using Compressarr.Core.Presets;

namespace Compressarr.Core.Conversion;

/// <summary>Everything needed to encode with one engine: its runner, its profile catalog, and where
/// its tools are (already path-expanded).</summary>
public sealed record EncoderBinding(
    EncoderEngine Engine,
    IEncoderRunner Runner,
    IEncoderPresetService Presets,
    string ToolPath,
    string? ProbePath,
    string? ExtraOptions);

/// <summary>
/// Picks the encoder for a lane (and for a single file, when a Dolby Vision / HDR10+ source has to
/// go to HandBrake instead). The orchestrators ask this instead of holding a HandBrake runner and
/// preset service directly - that is the seam that makes ffmpeg a second engine without touching
/// anything HandBrake-specific. When no resolver is supplied the orchestrators behave exactly as they
/// did before 2.2 (HandBrake only).
/// </summary>
public interface IEncoderResolver
{
    EncoderBinding For(EncoderEngine engine, CompressarrConfig config);

    /// <summary>The profile catalog for an engine - what a lane or queued file of that engine can pick.</summary>
    IEncoderPresetService PresetsFor(EncoderEngine engine);

    /// <summary>Null when the engine's tools are in place; otherwise a plain-English reason
    /// ("ffmpeg was not found at ...").</summary>
    string? NotReadyReason(EncoderEngine engine, CompressarrConfig config);

    /// <summary>Reads a file with ffprobe (null when ffprobe is missing or the file is unreadable).</summary>
    Task<MediaProbeResult?> ProbeAsync(CompressarrConfig config, string filePath, CancellationToken cancellationToken);

    /// <summary>The HandBrake profile that should encode a Dolby Vision / HDR10+ file for the named
    /// ffmpeg profile, or null when that profile has none.</summary>
    string? FallbackHandBrakePreset(string ffmpegProfileName);

    /// <summary>True when the named ffmpeg profile allows encoding a Dolby Vision / HDR10+ file with ffmpeg anyway, dropping
    /// that metadata (only consulted when it has no HandBrake fallback).</summary>
    bool AllowsDroppingDynamicHdr(string ffmpegProfileName);
}

public sealed class EncoderResolver : IEncoderResolver
{
    private readonly IEncoderRunner _handBrakeRunner;
    private readonly IEncoderPresetService _handBrakePresets;
    private readonly IEncoderRunner _ffmpegRunner;
    private readonly FFmpegPresetService _ffmpegPresets;
    private readonly IFFmpegProfileStore _ffmpegProfiles;
    private readonly IPathExpander _pathExpander;
    private readonly IMediaProbe _probe;

    public EncoderResolver(
        HandBrakeProcessRunner handBrakeRunner,
        HandBrakePresetService handBrakePresets,
        FFmpegRunner ffmpegRunner,
        FFmpegPresetService ffmpegPresets,
        IFFmpegProfileStore ffmpegProfiles,
        IPathExpander pathExpander,
        IMediaProbe probe)
    {
        _handBrakeRunner = handBrakeRunner;
        _handBrakePresets = handBrakePresets;
        _ffmpegRunner = ffmpegRunner;
        _ffmpegPresets = ffmpegPresets;
        _ffmpegProfiles = ffmpegProfiles;
        _pathExpander = pathExpander;
        _probe = probe;
    }

    public EncoderBinding For(EncoderEngine engine, CompressarrConfig config) => engine == EncoderEngine.FFmpeg
        ? new EncoderBinding(engine, _ffmpegRunner, _ffmpegPresets,
            _pathExpander.Expand(config.FFmpeg.Path), _pathExpander.Expand(config.FFmpeg.ProbePath), config.FFmpeg.Options)
        : new EncoderBinding(engine, _handBrakeRunner, _handBrakePresets,
            _pathExpander.Expand(config.HandBrake.CliPath), null, config.HandBrake.Options);

    public IEncoderPresetService PresetsFor(EncoderEngine engine) => engine == EncoderEngine.FFmpeg ? _ffmpegPresets : _handBrakePresets;

    public string? NotReadyReason(EncoderEngine engine, CompressarrConfig config) =>
        EncoderReadiness.Problem(engine, config, _pathExpander);

    public Task<MediaProbeResult?> ProbeAsync(CompressarrConfig config, string filePath, CancellationToken cancellationToken) =>
        _probe.ProbeAsync(_pathExpander.Expand(config.FFmpeg.ProbePath), filePath, cancellationToken);

    public string? FallbackHandBrakePreset(string ffmpegProfileName)
    {
        var fallback = _ffmpegProfiles.Find(ffmpegProfileName)?.FallbackHandBrakePreset;
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback;
    }

    public bool AllowsDroppingDynamicHdr(string ffmpegProfileName) =>
        _ffmpegProfiles.Find(ffmpegProfileName)?.AllowDroppingDynamicHdr ?? false;
}

/// <summary>"Is this engine's tooling in place" - the same answer for the run, the Lanes page and the
/// Encoder page.</summary>
public static class EncoderReadiness
{
    public static string? Problem(EncoderEngine engine, CompressarrConfig config, IPathExpander pathExpander)
    {
        if (engine == EncoderEngine.FFmpeg)
        {
            if (string.IsNullOrWhiteSpace(config.FFmpeg.Path) || !pathExpander.PathExists(config.FFmpeg.Path))
            {
                return $"ffmpeg was not found at '{pathExpander.Expand(config.FFmpeg.Path)}'. Use Check/Install on the Encoder page.";
            }
            if (string.IsNullOrWhiteSpace(config.FFmpeg.ProbePath) || !pathExpander.PathExists(config.FFmpeg.ProbePath))
            {
                return $"ffprobe was not found at '{pathExpander.Expand(config.FFmpeg.ProbePath)}'. ffprobe comes with ffmpeg - check the path on the Encoder page.";
            }
            return null;
        }

        return string.IsNullOrWhiteSpace(config.HandBrake.CliPath) || !pathExpander.PathExists(config.HandBrake.CliPath)
            ? $"HandBrakeCLI.exe was not found at '{pathExpander.Expand(config.HandBrake.CliPath)}'. Use Check/Install on the Encoder page."
            : null;
    }
}
