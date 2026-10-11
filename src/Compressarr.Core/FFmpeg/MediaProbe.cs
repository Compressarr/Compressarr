using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Compressarr.Core.FFmpeg;

public enum MediaStreamKind { Video, Audio, Subtitle, Attachment, Data }

/// <summary>HDR10 static metadata as ffprobe reports it, already in the integer units encoders expect
/// (chromaticity in 0.00002 steps, luminance in 0.0001 cd/m2 steps).</summary>
public sealed record MasteringDisplay(
    int RedX, int RedY, int GreenX, int GreenY, int BlueX, int BlueY,
    int WhiteX, int WhiteY, int MaxLuminance, int MinLuminance);

public sealed record ContentLight(int MaxContent, int MaxAverage);

/// <summary>One stream of a media file, as ffprobe reports it.</summary>
public sealed record MediaStream
{
    public int Index { get; init; }
    public MediaStreamKind Kind { get; init; }
    public string Codec { get; init; } = "";

    /// <summary>ffprobe's codec profile ("DTS-HD MA", "Main 10", ...), or "".</summary>
    public string CodecProfile { get; init; } = "";

    /// <summary>ISO 639-2 language tag as stored, lower-cased; "und" when the stream has none.</summary>
    public string Language { get; init; } = "und";
    public string Title { get; init; } = "";
    public int Channels { get; init; }
    public bool IsDefault { get; init; }
    public bool IsForced { get; init; }

    /// <summary>A video "stream" that is really cover art.</summary>
    public bool IsAttachedPicture { get; init; }

    // video
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>"progressive", "tt", "bb", "tb", "bt" or "unknown".</summary>
    public string FieldOrder { get; init; } = "unknown";
    public string PixFmt { get; init; } = "";
    public string ColorSpace { get; init; } = "";
    public string ColorTransfer { get; init; } = "";
    public string ColorPrimaries { get; init; } = "";
    public string ColorRange { get; init; } = "";
    public bool HasDolbyVision { get; init; }

    /// <summary>The Dolby Vision profile (5, 7, 8...) from ffprobe's "DOVI configuration record"; null when not reported.</summary>
    public int? DolbyVisionProfile { get; init; }

    /// <summary>The Dolby Vision base-layer signal compatibility id: 0 none (profile 5), 1 HDR10, 2 SDR, 4 HLG, 6 Blu-ray
    /// HDR10. Null when not reported.</summary>
    public int? DolbyVisionBaseLayerCompatibility { get; init; }

    /// <summary>True when the Dolby Vision file's base layer is something a player shows correctly without the Dolby Vision
    /// data (HDR10, SDR, HLG, Blu-ray HDR10). Profile 5 has no such layer: encoded without Dolby Vision it shows wrong colours.
    /// When the compatibility id isn't reported, profiles 7 and 8 (which always have one) count; anything else doesn't.</summary>
    public bool DolbyVisionHasUsableBaseLayer => !HasDolbyVision
        || (DolbyVisionBaseLayerCompatibility is { } id ? id is 1 or 2 or 4 or 6 : DolbyVisionProfile is 7 or 8);

    public MasteringDisplay? Mastering { get; init; }
    public ContentLight? ContentLight { get; init; }

    public bool IsInterlaced => FieldOrder is "tt" or "bb" or "tb" or "bt";

    /// <summary>True when the video is HDR (PQ or HLG transfer).</summary>
    public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";
}

/// <summary>What ffprobe found in a file - everything Compressarr decides from.</summary>
public sealed class MediaProbeResult
{
    public double? DurationSeconds { get; init; }
    public IReadOnlyList<MediaStream> Streams { get; init; } = Array.Empty<MediaStream>();
    public int ChapterCount { get; init; }

    /// <summary>True when the first frames carry HDR10+ dynamic metadata (SMPTE 2094-40).</summary>
    public bool HasHdr10Plus { get; init; }

    /// <summary>The video stream to encode: the first one that isn't cover art.</summary>
    public MediaStream? Video => Streams.FirstOrDefault(s => s.Kind == MediaStreamKind.Video && !s.IsAttachedPicture);

    public IEnumerable<MediaStream> Audio => Streams.Where(s => s.Kind == MediaStreamKind.Audio);
    public IEnumerable<MediaStream> Subtitles => Streams.Where(s => s.Kind == MediaStreamKind.Subtitle);
    public IEnumerable<MediaStream> Attachments => Streams.Where(s => s.Kind == MediaStreamKind.Attachment);

    /// <summary>Dolby Vision or HDR10+: dynamic metadata stock ffmpeg + libx265 would silently drop.</summary>
    public bool HasDynamicHdrMetadata => (Video?.HasDolbyVision ?? false) || HasHdr10Plus;

    /// <summary>"Dolby Vision", "HDR10+" or "Dolby Vision and HDR10+" - for messages.</summary>
    /// <summary>True when the Dolby Vision (if any) can be dropped without wrecking the picture. HDR10+ alone always can: it
    /// rides on a normal HDR10 stream.</summary>
    public bool DynamicHdrCanBeDropped => Video?.DolbyVisionHasUsableBaseLayer ?? true;

    /// <summary>"profile 5" or "" - for messages.</summary>
    public string DolbyVisionProfileLabel => Video?.DolbyVisionProfile is { } p ? $"profile {p}" : "";

    public string DynamicHdrName => (Video?.HasDolbyVision ?? false) && HasHdr10Plus ? "Dolby Vision and HDR10+"
        : (Video?.HasDolbyVision ?? false) ? "Dolby Vision" : HasHdr10Plus ? "HDR10+" : "";
}

/// <summary>Parses ffprobe's JSON output. Pure, so it is tested against real-shaped fixtures.</summary>
public static class FFprobeParser
{
    public static MediaProbeResult Parse(string json, string? framesJson = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        double? duration = null;
        if (root.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d))
        {
            duration = ParseDouble(d);
        }

        var streams = new List<MediaStream>();
        if (root.TryGetProperty("streams", out var streamArray) && streamArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streamArray.EnumerateArray())
            {
                streams.Add(ParseStream(s));
                duration ??= s.TryGetProperty("duration", out var sd) ? ParseDouble(sd) : null;
            }
        }

        var chapters = root.TryGetProperty("chapters", out var c) && c.ValueKind == JsonValueKind.Array ? c.GetArrayLength() : 0;

        return new MediaProbeResult
        {
            DurationSeconds = duration is > 0 ? duration : null,
            Streams = streams,
            ChapterCount = chapters,
            HasHdr10Plus = framesJson is not null && FramesHaveHdr10Plus(framesJson)
        };
    }

    private static MediaStream ParseStream(JsonElement s)
    {
        var kind = Str(s, "codec_type") switch
        {
            "video" => MediaStreamKind.Video,
            "audio" => MediaStreamKind.Audio,
            "subtitle" => MediaStreamKind.Subtitle,
            "attachment" => MediaStreamKind.Attachment,
            _ => MediaStreamKind.Data
        };

        var disposition = s.TryGetProperty("disposition", out var disp) ? disp : default;
        var tags = s.TryGetProperty("tags", out var t) ? t : default;
        var language = (TagValue(tags, "language") ?? "").Trim().ToLowerInvariant();

        MasteringDisplay? mastering = null;
        ContentLight? light = null;
        var dolbyVision = false;
        int? dvProfile = null;
        int? dvCompatibility = null;
        if (s.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var side in sideData.EnumerateArray())
            {
                var type = Str(side, "side_data_type");
                if (type.StartsWith("DOVI", StringComparison.OrdinalIgnoreCase) || type.StartsWith("Dolby Vision", StringComparison.OrdinalIgnoreCase))
                {
                    dolbyVision = true;
                    dvProfile = IntOrNull(side, "dv_profile");
                    dvCompatibility = IntOrNull(side, "dv_bl_signal_compatibility_id");
                }
                else if (type == "Mastering display metadata")
                {
                    mastering = ParseMastering(side);
                }
                else if (type == "Content light level metadata")
                {
                    light = new ContentLight((int)(Num(side, "max_content") ?? 0), (int)(Num(side, "max_average") ?? 0));
                }
            }
        }

        return new MediaStream
        {
            Index = (int)(Num(s, "index") ?? 0),
            Kind = kind,
            Codec = Str(s, "codec_name"),
            CodecProfile = Str(s, "profile"),
            Language = string.IsNullOrEmpty(language) ? "und" : language,
            Title = TagValue(tags, "title") ?? "",
            Channels = (int)(Num(s, "channels") ?? 0),
            IsDefault = Flag(disposition, "default"),
            IsForced = Flag(disposition, "forced"),
            IsAttachedPicture = Flag(disposition, "attached_pic"),
            Width = (int)(Num(s, "width") ?? 0),
            Height = (int)(Num(s, "height") ?? 0),
            FieldOrder = Str(s, "field_order") is { Length: > 0 } fo ? fo : "unknown",
            PixFmt = Str(s, "pix_fmt"),
            ColorSpace = Str(s, "color_space"),
            ColorTransfer = Str(s, "color_transfer"),
            ColorPrimaries = Str(s, "color_primaries"),
            ColorRange = Str(s, "color_range"),
            HasDolbyVision = dolbyVision,
            DolbyVisionProfile = dvProfile,
            DolbyVisionBaseLayerCompatibility = dvCompatibility,
            Mastering = mastering,
            ContentLight = light
        };
    }

    private static int? IntOrNull(JsonElement e, string name) => Num(e, name) is { } n ? (int)n : null;

    private static MasteringDisplay? ParseMastering(JsonElement side)
    {
        // ffprobe reports each value as a fraction ("35400/50000"); the numerator is the integer an
        // encoder wants when the denominator is the standard 50000 (chromaticity) / 10000 (luminance).
        int? Scaled(string key, double unit)
        {
            if (!side.TryGetProperty(key, out var v)) return null;
            var text = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
            var parts = text.Split('/');
            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)) return null;
            if (parts.Length == 2 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) && denominator != 0)
            {
                return (int)Math.Round(numerator / denominator * unit);
            }
            return (int)Math.Round(numerator * unit);
        }

        var values = new[]
        {
            Scaled("red_x", 50000), Scaled("red_y", 50000), Scaled("green_x", 50000), Scaled("green_y", 50000),
            Scaled("blue_x", 50000), Scaled("blue_y", 50000), Scaled("white_point_x", 50000), Scaled("white_point_y", 50000),
            Scaled("max_luminance", 10000), Scaled("min_luminance", 10000)
        };
        if (values.Any(v => v is null)) return null;

        return new MasteringDisplay(values[0]!.Value, values[1]!.Value, values[2]!.Value, values[3]!.Value, values[4]!.Value,
            values[5]!.Value, values[6]!.Value, values[7]!.Value, values[8]!.Value, values[9]!.Value);
    }

    private static bool FramesHaveHdr10Plus(string framesJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(framesJson);
            if (!doc.RootElement.TryGetProperty("frames", out var frames) || frames.ValueKind != JsonValueKind.Array) return false;

            foreach (var frame in frames.EnumerateArray())
            {
                if (!frame.TryGetProperty("side_data_list", out var list) || list.ValueKind != JsonValueKind.Array) continue;
                foreach (var side in list.EnumerateArray())
                {
                    var type = Str(side, "side_data_type");
                    if (type.Contains("2094-40", StringComparison.Ordinal) || type.Contains("HDR10+", StringComparison.Ordinal)) return true;
                }
            }
        }
        catch (JsonException)
        {
            // Frame data is a bonus; an unreadable answer just means "no HDR10+ found".
        }
        return false;
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? ParseDouble(v) : null;

    private static double? ParseDouble(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number => v.GetDouble(),
        JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null
    };

    private static bool Flag(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() != 0;

    /// <summary>Tag names are case-insensitive in practice (MKV uses "language", some muxers "LANGUAGE").</summary>
    private static string? TagValue(JsonElement tags, string name)
    {
        if (tags.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in tags.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String) return p.Value.GetString();
        }
        return null;
    }
}

public interface IMediaProbe
{
    /// <summary>Reads a file's streams, duration and HDR metadata with ffprobe. Returns null (never
    /// throws) when ffprobe is missing, can't read the file, or answers with something unparseable.</summary>
    Task<MediaProbeResult?> ProbeAsync(string probePath, string filePath, CancellationToken cancellationToken);
}

public sealed class FFprobeMediaProbe : IMediaProbe
{
    public async Task<MediaProbeResult?> ProbeAsync(string probePath, string filePath, CancellationToken cancellationToken)
    {
        var main = await RunAsync(probePath, new[]
        {
            "-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters", filePath
        }, cancellationToken);
        if (main is null) return null;

        // The first few frames of the video stream: where HDR10+ announces itself (it is per-frame data).
        var frames = await RunAsync(probePath, new[]
        {
            "-v", "error", "-print_format", "json", "-select_streams", "v:0", "-show_frames",
            "-read_intervals", "%+#3", "-show_entries", "frame=side_data_list", filePath
        }, cancellationToken);

        try
        {
            return FFprobeParser.Parse(main, frames);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string?> RunAsync(string probePath, IEnumerable<string> args, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = probePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            foreach (var a in args) startInfo.ArgumentList.Add(a);

            using var process = Process.Start(startInfo);
            if (process is null) return null;

            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            await stderr;
            return process.ExitCode == 0 ? await stdout : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
