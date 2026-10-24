using System.Globalization;
using System.Text.RegularExpressions;
using Compressarr.Core.Conversion;

namespace Compressarr.Core.FFmpeg;

/// <summary>
/// Turns ffmpeg's "-progress pipe:1" output (blocks of key=value lines ending in progress=continue
/// or progress=end) into the engine-neutral <see cref="EncodeProgress"/>. Percent comes from how far
/// into the source the encoder has got (out_time) over the source's length (from ffprobe); the ETA
/// from the remaining length over the current encoding speed. Stateful per encode - feed it every
/// stdout line; it answers once per block.
/// </summary>
public sealed class FFmpegProgressParser
{
    private readonly double? _durationSeconds;
    private double? _outSeconds;
    private double? _fps;
    private double? _speed;

    /// <summary>The last positive "fps" ffmpeg reported. ffmpeg works it out as frames written over the time since it started, so it is already
    /// a running average of the whole encode; null until it has reported one.</summary>
    public double? AverageFps { get; private set; }

    public FFmpegProgressParser(double? durationSeconds) => _durationSeconds = durationSeconds is > 0 ? durationSeconds : null;

    /// <summary>Feeds one stdout line; returns a reading when it completes a block, else null.</summary>
    public EncodeProgress? Feed(string line)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0) return null;

        var key = line[..eq].Trim();
        var value = line[(eq + 1)..].Trim();

        switch (key)
        {
            case "out_time_us":
            case "out_time_ms": // despite its name, ffmpeg reports microseconds here too
                if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var micro) && micro >= 0) _outSeconds = micro / 1_000_000.0;
                break;
            case "out_time":
                if (_outSeconds is null && TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span) && span >= TimeSpan.Zero) _outSeconds = span.TotalSeconds;
                break;
            case "fps":
                _fps = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) && fps > 0 ? fps : null;
                if (_fps is { } positive) AverageFps = positive;
                break;
            case "speed":
                _speed = double.TryParse(value.TrimEnd('x', 'X', ' '), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) && speed > 0 ? speed : null;
                break;
            case "progress":
                return BuildReading(finished: value == "end");
        }

        return null;
    }

    private EncodeProgress? BuildReading(bool finished)
    {
        if (finished) return new EncodeProgress(100, _fps, null);
        if (_durationSeconds is not { } duration || _outSeconds is not { } done) return null; // no length to measure against

        var percent = Math.Clamp(done / duration * 100.0, 0, 99.9);
        string? eta = null;
        if (_speed is { } s && done < duration)
        {
            eta = FormatEta(TimeSpan.FromSeconds((duration - done) / s));
        }
        return new EncodeProgress(percent, _fps, eta);
    }

    /// <summary>"00h06m50s" - the same shape HandBrake's ETA has, so the Monitor shows both alike.</summary>
    internal static string FormatEta(TimeSpan remaining) =>
        $"{(int)remaining.TotalHours:00}h{remaining.Minutes:00}m{remaining.Seconds:00}s";
}

/// <summary>Reads the black bars out of cropdetect's log lines and combines several samples into one crop.</summary>
public static class CropDetection
{
    private static readonly Regex CropLine = new(@"crop=(?<w>\d+):(?<h>\d+):(?<x>\d+):(?<y>\d+)", RegexOptions.Compiled);

    /// <summary>The last crop=W:H:X:Y cropdetect printed (later frames are the most settled), or null.</summary>
    public static CropRect? ParseLast(string log)
    {
        CropRect? last = null;
        foreach (Match m in CropLine.Matches(log))
        {
            last = new CropRect(int.Parse(m.Groups["w"].Value), int.Parse(m.Groups["h"].Value), int.Parse(m.Groups["x"].Value), int.Parse(m.Groups["y"].Value));
        }
        return last;
    }

    /// <summary>The rectangle that contains every sample (the least aggressive crop that is right for all
    /// of them - a dark scene can't make it cut real picture), made even-sized, or null when it is the
    /// whole frame or there were no samples.</summary>
    public static CropRect? Combine(IReadOnlyCollection<CropRect> samples, int frameWidth, int frameHeight)
    {
        if (samples.Count == 0 || frameWidth <= 0 || frameHeight <= 0) return null;

        var left = samples.Min(s => s.X);
        var top = samples.Min(s => s.Y);
        var right = samples.Max(s => s.X + s.Width);
        var bottom = samples.Max(s => s.Y + s.Height);

        left -= left % 2;
        top -= top % 2;
        var width = (right - left) - ((right - left) % 2);
        var height = (bottom - top) - ((bottom - top) % 2);
        if (width <= 0 || height <= 0) return null;
        if (left == 0 && top == 0 && width >= frameWidth && height >= frameHeight) return null;
        if (left + width > frameWidth || top + height > frameHeight) return null; // inconsistent samples: don't guess

        return new CropRect(width, height, left, top);
    }
}
