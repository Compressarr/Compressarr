using Compressarr.Core.FFmpeg;

namespace Compressarr.Core.Tests.FFmpeg;

public class FFmpegProgressAndCropTests
{
    private static Compressarr.Core.Conversion.EncodeProgress? FeedBlock(FFmpegProgressParser parser, params string[] lines)
    {
        Compressarr.Core.Conversion.EncodeProgress? last = null;
        foreach (var line in lines) last = parser.Feed(line) ?? last;
        return last;
    }

    [Fact]
    public void Progress_ComputesPercentFpsAndEta_FromAnOutTimeBlock()
    {
        var parser = new FFmpegProgressParser(durationSeconds: 1000);

        var reading = FeedBlock(parser,
            "frame=1500", "fps=31.4", "stream_0_0_q=24.0", "bitrate=N/A", "total_size=1234",
            "out_time_us=250000000", "out_time_ms=250000000", "out_time=00:04:10.000000",
            "dup_frames=0", "drop_frames=0", "speed=2.5x", "progress=continue");

        Assert.NotNull(reading);
        Assert.Equal(25.0, reading!.Percent, 3);
        Assert.Equal(31.4, reading.Fps);
        Assert.Equal("00h05m00s", reading.Eta); // 750 s left at 2.5x = 300 s
    }

    [Fact]
    public void AverageFps_IsTheLastPositiveFpsFfmpegReported_AndSurvivesAZeroReadingAtTheEnd()
    {
        var parser = new FFmpegProgressParser(100);
        Assert.Null(parser.AverageFps);

        FeedBlock(parser, "fps=0.00", "out_time_us=1000000", "progress=continue");
        Assert.Null(parser.AverageFps);                 // 0 is "not measured yet", not a speed

        FeedBlock(parser, "fps=18.5", "out_time_us=20000000", "progress=continue");
        FeedBlock(parser, "fps=24.25", "out_time_us=90000000", "progress=continue");
        FeedBlock(parser, "fps=0.00", "progress=end");

        Assert.Equal(24.25, parser.AverageFps);
    }

    [Fact]
    public void Progress_OnlyAnswersAtTheEndOfABlock()
    {
        var parser = new FFmpegProgressParser(100);

        Assert.Null(parser.Feed("out_time_us=1000000"));
        Assert.Null(parser.Feed("fps=24"));
        Assert.NotNull(parser.Feed("progress=continue"));
    }

    [Fact]
    public void Progress_TheEndBlock_Is100Percent()
    {
        var parser = new FFmpegProgressParser(100);
        FeedBlock(parser, "out_time_us=99000000", "progress=continue");

        var reading = FeedBlock(parser, "progress=end");

        Assert.Equal(100, reading!.Percent);
    }

    [Fact]
    public void Progress_NeverReaches100BeforeTheEnd()
    {
        var parser = new FFmpegProgressParser(100);

        var reading = FeedBlock(parser, "out_time_us=120000000", "speed=1x", "progress=continue");

        Assert.Equal(99.9, reading!.Percent, 3);
        Assert.Null(reading.Eta);
    }

    [Fact]
    public void Progress_FallsBackToOutTime_WhenNoMicrosecondFieldExists()
    {
        var parser = new FFmpegProgressParser(100);

        var reading = FeedBlock(parser, "out_time=00:00:10.500000", "progress=continue");

        Assert.Equal(10.5, reading!.Percent, 3);
    }

    [Fact]
    public void Progress_WithoutASourceLength_ReportsNothingUntilTheEnd()
    {
        var parser = new FFmpegProgressParser(null);

        Assert.Null(FeedBlock(parser, "out_time_us=5000000", "progress=continue"));
        Assert.NotNull(FeedBlock(parser, "progress=end"));
    }

    [Fact]
    public void Progress_IgnoresGarbageAndNotAvailableValues()
    {
        var parser = new FFmpegProgressParser(100);

        var reading = FeedBlock(parser, "no equals sign", "fps=N/A", "speed=N/A", "out_time_us=N/A", "out_time_us=5000000", "progress=continue");

        Assert.Equal(5.0, reading!.Percent, 3);
        Assert.Null(reading.Fps);
        Assert.Null(reading.Eta);
    }

    // ---- crop detection -------------------------------------------------------------------------

    [Fact]
    public void ParseLast_TakesTheLastCropLine()
    {
        const string log = """
            [Parsed_cropdetect_0 @ 000001] x1:0 x2:1919 y1:138 y2:941 w:1920 h:804 x:0 y:138 pts:100 t:4.17 crop=1920:804:0:138
            [Parsed_cropdetect_0 @ 000001] x1:0 x2:1919 y1:140 y2:939 w:1920 h:800 x:0 y:140 pts:200 t:8.33 crop=1920:800:0:140
            """;

        Assert.Equal(new CropRect(1920, 800, 0, 140), CropDetection.ParseLast(log));
        Assert.Null(CropDetection.ParseLast("nothing here"));
    }

    [Fact]
    public void Combine_IsTheBoxContainingEverySample()
    {
        // a dark scene sees a narrower picture; the result must not cut real picture from the others
        var samples = new[] { new CropRect(1920, 800, 0, 140), new CropRect(1920, 804, 0, 138), new CropRect(1920, 700, 0, 190) };

        Assert.Equal(new CropRect(1920, 804, 0, 138), CropDetection.Combine(samples, 1920, 1080));
    }

    [Fact]
    public void Combine_RoundsToEvenNumbers()
    {
        var crop = CropDetection.Combine(new[] { new CropRect(1919, 805, 1, 137) }, 1920, 1080)!;

        Assert.Equal(0, crop.X % 2);
        Assert.Equal(0, crop.Y % 2);
        Assert.Equal(0, crop.Width % 2);
        Assert.Equal(0, crop.Height % 2);
    }

    [Fact]
    public void Combine_NoBlackBars_OrNoSamples_MeansNoCrop()
    {
        Assert.Null(CropDetection.Combine(new[] { new CropRect(1920, 1080, 0, 0) }, 1920, 1080));
        Assert.Null(CropDetection.Combine(Array.Empty<CropRect>(), 1920, 1080));
    }

    [Fact]
    public void Combine_ASampleOutsideTheFrame_IsNotTrusted()
    {
        Assert.Null(CropDetection.Combine(new[] { new CropRect(1920, 1200, 0, 140) }, 1920, 1080));
    }
}
