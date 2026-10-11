using System.Text.Json.Nodes;
using Compressarr.Core.Presets;

namespace Compressarr.Core.FFmpeg;

public sealed record ProfileConversion(FFmpegProfile Profile, IReadOnlyList<string> Carried, IReadOnlyList<string> NotCarried);

/// <summary>
/// "Duplicate as ffmpeg...": turns a HandBrake profile into the closest ffmpeg profile, listing what it
/// carried over and - just as importantly - what ffmpeg can't express, so nothing is silently lost. The
/// result is only a starting point: it is saved as the user's own profile and opened for review.
/// </summary>
public static class HandBrakeToFFmpeg
{
    public static ProfileConversion Convert(string handBrakeName, JsonObject preset)
    {
        var form = HandBrakeProfileForm.From(preset);
        var carried = new List<string>();
        var lost = new List<string>();

        var profile = new FFmpegProfile
        {
            Name = handBrakeName,
            Description = form.Description,
            Chapters = form.ChapterMarkers,
            Optimize = form.Optimize,
            // None: a Dolby Vision / HDR10+ file is refused rather than re-encoded by a profile the user didn't choose. They can
            // name a HandBrake fallback in the editor.
            FallbackHandBrakePreset = "",
            AudioLanguages = SplitList(form.AudioLanguages),
            AudioTracks = form.AudioTracks == "all" ? "all" : "first",
            SubtitleLanguages = SplitList(form.SubtitleLanguages),
            SubtitleTracks = form.SubtitleTracks is "all" or "first" ? form.SubtitleTracks : "none"
        };

        // ---- container ----
        if (form.FileFormat.Contains("mp4", StringComparison.OrdinalIgnoreCase)) profile.Container = "mp4";
        else if (form.FileFormat.Contains("mkv", StringComparison.OrdinalIgnoreCase)) profile.Container = "mkv";
        else
        {
            profile.Container = "mkv";
            lost.Add($"The container '{form.FileFormat}' isn't available - MKV is used.");
        }

        // ---- video ----
        var encoder = form.VideoEncoder;
        string pixFmt = "";
        switch (encoder)
        {
            case "x264": profile.VideoCodec = "libx264"; pixFmt = "yuv420p"; break;
            case "x264_10bit": profile.VideoCodec = "libx264"; pixFmt = "yuv420p10le"; break;
            case "x265": profile.VideoCodec = "libx265"; pixFmt = "yuv420p"; break;
            case "x265_10bit": profile.VideoCodec = "libx265"; pixFmt = "yuv420p10le"; break;
            case "x265_12bit": profile.VideoCodec = "libx265"; pixFmt = "yuv420p12le"; break;
            case "svt_av1": profile.VideoCodec = "libsvtav1"; pixFmt = "yuv420p"; break;
            case "svt_av1_10bit": profile.VideoCodec = "libsvtav1"; pixFmt = "yuv420p10le"; break;
            case "nvenc_h264": profile.VideoCodec = "h264_nvenc"; pixFmt = "yuv420p"; break;
            case "nvenc_h265": profile.VideoCodec = "hevc_nvenc"; pixFmt = "yuv420p"; break;
            case "nvenc_h265_10bit": profile.VideoCodec = "hevc_nvenc"; pixFmt = "p010le"; break;
            case "nvenc_av1": profile.VideoCodec = "av1_nvenc"; pixFmt = "yuv420p"; break;
            case "nvenc_av1_10bit": profile.VideoCodec = "av1_nvenc"; pixFmt = "p010le"; break;
            default:
                profile.VideoCodec = "libx265";
                pixFmt = "yuv420p10le";
                lost.Add($"The video encoder '{encoder}' isn't available in ffmpeg profiles - x265 10-bit is used.");
                break;
        }
        profile.PixFmt = pixFmt;
        carried.Add($"Video encoder: {profile.VideoCodec}");

        profile.Preset = profile.VideoCodec.EndsWith("_nvenc", StringComparison.Ordinal) ? NvencPreset(form.VideoPreset) : form.VideoPreset;
        profile.Tune = form.VideoTune == "none" ? "" : form.VideoTune;
        profile.VideoProfile = form.VideoProfile == "auto" ? "" : form.VideoProfile;
        if (profile.VideoCodec == "libsvtav1") profile.VideoProfile = ""; // ffmpeg picks it

        if (form.QualityMode == "bitrate")
        {
            profile.QualityMode = "bitrate";
            profile.VideoBitrateKbps = form.VideoBitrate;
            carried.Add($"Average bitrate {form.VideoBitrate} kbps");
        }
        else
        {
            profile.Quality = form.Rf;
            carried.Add($"Quality {form.Rf:0.##} (HandBrake's RF is used as ffmpeg's {(profile.VideoCodec.EndsWith("_nvenc", StringComparison.Ordinal) ? "CQ" : "CRF")}; the scales are close, not identical)");
        }

        switch (form.FramerateMode)
        {
            case "cfr" when double.TryParse(form.Framerate, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var fps):
                profile.FramerateMode = "cfr";
                profile.Framerate = fps;
                carried.Add($"Constant frame rate {fps:0.###}");
                break;
            case "pfr":
                lost.Add("Peak frame rate isn't available in ffmpeg - the source's own timing is kept.");
                break;
        }

        // ---- picture ----
        switch (form.Deinterlace)
        {
            case "off":
                profile.Deinterlace = "off";
                break;
            case "decomb":
                profile.Deinterlace = "auto";
                lost.Add("Decomb (deinterlace only combed frames) has no ffmpeg equivalent: ffmpeg deinterlaces automatically only when the file is flagged interlaced.");
                break;
            default:
                profile.Deinterlace = "always";
                carried.Add($"Deinterlace: always ({form.Deinterlace} becomes ffmpeg's bwdif)");
                break;
        }

        profile.Crop = form.CropMode == 2 ? "off" : "auto";
        if (form.CropMode is not (0 or 2)) lost.Add("A custom or conditional crop isn't available - automatic cropping is used.");

        // ---- audio ----
        profile.AudioMode = form.AudioMode;
        profile.PassthroughCodecs = form.PassthroughCodecs.Select(c => c.Replace("copy:", "")).ToList();
        profile.AudioCodec = AudioCodec(form.AudioEncoder, lost);
        profile.AudioBitrateKbps = form.AudioBitrate > 0 ? form.AudioBitrate : 160;
        profile.AudioMixdown = Mixdown(form.AudioMixdown, lost);
        carried.Add($"Audio: {(form.AudioMode == "passthru" ? "pass-through where possible, otherwise " : "")}{profile.AudioCodec} {profile.AudioBitrateKbps} kbps");

        if (preset["AudioList"] is JsonArray { Count: > 1 }) lost.Add("Only the first audio output of the HandBrake profile is carried over (ffmpeg profiles describe one audio recipe).");

        // ---- things ffmpeg profiles don't cover ----
        if (Str(preset, "SubtitleBurnBehavior") is { } burn && burn != "none") lost.Add("Subtitle burn-in isn't available in ffmpeg profiles.");
        if (!string.IsNullOrWhiteSpace(form.ExtraVideoOptions)) lost.Add($"Extra encoder options ('{form.ExtraVideoOptions}') are HandBrake/x264 syntax and weren't translated - add ffmpeg's own under Extra arguments if you need them.");
        foreach (var (key, label) in new[]
        {
            ("PictureDenoiseFilter", "Denoise"), ("PictureSharpenFilter", "Sharpen"), ("PictureDeblockPreset", "Deblock"),
            ("PictureDetelecine", "Detelecine"), ("PictureChromaSmoothPreset", "Chroma smoothing"), ("PictureColorspacePreset", "Colorspace")
        })
        {
            if (Str(preset, key) is { } value && value != "off") lost.Add($"{label} filter ({value}) isn't carried over.");
        }
        if (Number(preset, "PictureForceWidth") > 0 || Number(preset, "PictureForceHeight") > 0 || Bool(preset, "PictureUseMaximumSize"))
        {
            lost.Add("Resizing/scaling isn't available in ffmpeg profiles.");
        }
        if (Str(preset, "PicturePadMode") is { } pad && pad != "none") lost.Add("Padding isn't available in ffmpeg profiles.");
        if (Bool(preset, "VideoMultiPass") && Str(preset, "VideoEncoder")?.StartsWith("nvenc") != true && form.QualityMode == "bitrate")
        {
            lost.Add("Multi-pass encoding isn't available in ffmpeg profiles.");
        }

        return new ProfileConversion(profile, carried, lost);
    }

    private static string NvencPreset(string handBrakePreset) => handBrakePreset switch
    {
        "fastest" => "p1",
        "faster" => "p2",
        "fast" => "p3",
        "medium" => "p4",
        "slow" => "p5",
        "slower" => "p6",
        "slowest" => "p7",
        _ => "p5"
    };

    private static string AudioCodec(string handBrakeEncoder, List<string> lost)
    {
        switch (handBrakeEncoder)
        {
            case "av_aac": case "aac": return "aac";
            case "ac3": return "ac3";
            case "eac3": return "eac3";
            case "opus": return "opus";
            case "mp3": return "mp3";
            case "flac16": case "flac24": return "flac";
            default:
                lost.Add($"The audio encoder '{handBrakeEncoder}' isn't available in ffmpeg profiles - AAC is used.");
                return "aac";
        }
    }

    private static string Mixdown(string handBrakeMixdown, List<string> lost)
    {
        switch (handBrakeMixdown)
        {
            case "mono": return "mono";
            case "stereo": return "stereo";
            case "5point1": return "5.1";
            case "7point1": return "7.1";
            case "dpl1": case "dpl2":
                lost.Add("Dolby Surround / Pro Logic downmixes aren't available - a plain stereo downmix is used.");
                return "stereo";
            default:
                lost.Add($"The mixdown '{handBrakeMixdown}' isn't available - the source's channel layout is kept.");
                return "same";
        }
    }

    private static List<string> SplitList(string value) =>
        value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double Number(JsonObject o, string key) =>
        o[key] is JsonValue v ? (v.TryGetValue<double>(out var d) ? d : v.TryGetValue<int>(out var i) ? i : 0) : 0;

    private static bool Bool(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
