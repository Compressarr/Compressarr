using System.Text.Json.Serialization;

namespace Compressarr.Core.FFmpeg;

/// <summary>
/// One ffmpeg encoding recipe - structured data, never raw arguments. <see cref="FFmpegCommandBuilder"/>
/// compiles it (plus what ffprobe found in the source file) into the actual command line, so users
/// pick options instead of writing ffmpeg syntax; an "Extra arguments" field is the escape hatch.
///
/// The defaults describe a sensible "x265 10-bit, CRF 24" encode so a partly-filled profile (an
/// older file, a hand-edited one) still means something.
/// </summary>
public sealed class FFmpegProfile
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>"mkv" or "mp4".</summary>
    public string Container { get; set; } = "mkv";

    // ---- video ----
    /// <summary>libx265, libx264, libsvtav1, hevc_nvenc, h264_nvenc or av1_nvenc.</summary>
    public string VideoCodec { get; set; } = "libx265";

    /// <summary>The encoder's own speed preset ("veryfast", "4", "p5", ...); blank = encoder default.</summary>
    public string Preset { get; set; } = "";

    /// <summary>The encoder's tune ("animation", "psnr", ...); blank = none.</summary>
    public string Tune { get; set; } = "";

    /// <summary>Encoder profile ("main10", "high", ...); blank = encoder default.</summary>
    public string VideoProfile { get; set; } = "";

    /// <summary>Pixel format ("yuv420p10le", "yuv420p", "p010le"); blank = encoder default.</summary>
    public string PixFmt { get; set; } = "";

    /// <summary>"quality" (CRF, or CQ for NVENC) or "bitrate" (average kbps).</summary>
    public string QualityMode { get; set; } = "quality";
    public double Quality { get; set; } = 24;
    public int VideoBitrateKbps { get; set; }

    /// <summary>"vfr" (keep the source's timing) or "cfr" (constant, at <see cref="Framerate"/>).</summary>
    public string FramerateMode { get; set; } = "vfr";
    public double Framerate { get; set; }

    // ---- picture ----
    /// <summary>"off", "auto" (only when ffprobe reports an interlaced source) or "always".</summary>
    public string Deinterlace { get; set; } = "auto";

    /// <summary>"off" or "auto" (black bars found by sampling the file).</summary>
    public string Crop { get; set; } = "auto";

    // ---- audio ----
    /// <summary>Languages, in order of preference ("eng", "und", "any").</summary>
    public List<string> AudioLanguages { get; set; } = new() { "eng", "und", "any" };

    /// <summary>"first" or "all" matching tracks.</summary>
    public string AudioTracks { get; set; } = "first";

    /// <summary>"passthru" (copy a track when its format is in <see cref="PassthroughCodecs"/>, else
    /// encode it) or "encode" (always encode).</summary>
    public string AudioMode { get; set; } = "encode";
    public List<string> PassthroughCodecs { get; set; } = new();

    /// <summary>aac, ac3, eac3, opus, mp3 or flac.</summary>
    public string AudioCodec { get; set; } = "aac";
    public int AudioBitrateKbps { get; set; } = 160;

    /// <summary>"same", "mono", "stereo", "5.1" or "7.1" - an upper limit: a track is never upmixed.</summary>
    public string AudioMixdown { get; set; } = "stereo";

    // ---- subtitles & chapters ----
    public List<string> SubtitleLanguages { get; set; } = new();

    /// <summary>"all", "first" or "none" of the matching tracks.</summary>
    public string SubtitleTracks { get; set; } = "none";
    public bool Chapters { get; set; } = true;

    /// <summary>MP4 only: move the index to the front so the file streams.</summary>
    public bool Optimize { get; set; }

    // ---- other ----
    /// <summary>The HandBrake profile that encodes a Dolby Vision / HDR10+ file instead (stock ffmpeg
    /// would silently drop the dynamic metadata). Blank = refuse such files.</summary>
    public string FallbackHandBrakePreset { get; set; } = "";

    /// <summary>Encode a Dolby Vision / HDR10+ file with ffmpeg anyway, dropping that dynamic metadata (the output keeps
    /// the file's HDR10 base layer). Off by default, and only used when no HandBrake fallback is named. A Dolby Vision file
    /// with no usable base layer (profile 5) is still refused: without the Dolby Vision data it would show wrong colours.
    /// The original is replaced by the lesser copy, so the editor asks for confirmation.</summary>
    public bool AllowDroppingDynamicHdr { get; set; }

    /// <summary>Free-form extra output arguments, added just before the output file.</summary>
    public string ExtraArgs { get; set; } = "";

    /// <summary>True for the profiles that ship inside the app. Not stored.</summary>
    [JsonIgnore]
    public bool IsBuiltIn { get; set; }

    public FFmpegProfile Clone()
    {
        var copy = (FFmpegProfile)MemberwiseClone();
        copy.AudioLanguages = new List<string>(AudioLanguages);
        copy.PassthroughCodecs = new List<string>(PassthroughCodecs);
        copy.SubtitleLanguages = new List<string>(SubtitleLanguages);
        return copy;
    }

    /// <summary>The output file extension this profile writes.</summary>
    public string Extension => string.Equals(Container, "mp4", StringComparison.OrdinalIgnoreCase) ? ".mp4" : ".mkv";
}
