using Compressarr.Core.Conversion;
using Compressarr.Core.Logging;

namespace Compressarr.Core.Tests.Logging;

/// <summary>What a run's files were made of (engines, lanes, profiles) and the rough average speed, as recorded in the history.</summary>
public class RunBreakdownTests
{
    private static readonly DateTime T0 = new(2026, 10, 24, 8, 0, 0);

    private static ConversionResult Result(string lane = "Movies", string? preset = "HB 1080", string? label = null, bool passthrough = false, double? fps = null, int seconds = 60) => new()
    {
        LaneId = lane.ToLowerInvariant(),
        HomeLaneName = lane,
        FileName = "f.mkv",
        FullName = @"C:\f.mkv",
        ContentType = "Movie",
        PresetName = preset,
        EncoderLabel = label,
        IsPassthrough = passthrough,
        AvgFps = fps,
        Success = true,
        StartTime = T0,
        EndTime = T0.AddSeconds(seconds)
    };

    [Fact]
    public void TheEngine_IsPassthrough_FFmpeg_OrHandBrake_IncludingAFileAnFfmpegLaneHandedToHandBrake()
    {
        Assert.Equal(ResultEngine.HandBrake, Result().Engine);
        Assert.Equal(ResultEngine.FFmpeg, Result(label: "ffmpeg").Engine);
        Assert.Equal(ResultEngine.FFmpeg, Result(label: "ffmpeg (Dolby Vision dropped)").Engine);
        Assert.Equal(ResultEngine.HandBrake, Result(label: "HandBrake (Dolby Vision fallback)").Engine);
        Assert.Equal(ResultEngine.Passthrough, Result(label: "passthrough", passthrough: true).Engine);
    }

    [Fact]
    public void From_CountsEnginesLanesAndProfiles_AndLeavesACopyOutOfTheProfiles()
    {
        var breakdown = RunBreakdown.From(new[]
        {
            Result("Movies", "HB 1080"), Result("Movies", "HB 1080"), Result("TV", "FF 720", label: "ffmpeg"),
            Result("TV", "Passthrough", label: "passthrough", passthrough: true), Result("Kids", null)
        });

        Assert.Equal((3, 1, 1), (breakdown.HandBrake, breakdown.FFmpeg, breakdown.Passthrough));
        Assert.Equal(new Dictionary<string, int> { ["Movies"] = 2, ["TV"] = 2, ["Kids"] = 1 }, breakdown.Lanes);
        Assert.Equal(new Dictionary<string, int> { ["HB 1080"] = 2, ["FF 720"] = 1 }, breakdown.Presets);   // no profile for the copy or for a file with none
    }

    [Fact]
    public void AFileBelongsToTheLaneThatProcessedIt_NotTheOneItWasRedirectedTo()
    {
        var redirected = new ConversionResult
        {
            LaneId = "movies", HomeLaneName = "Movies", RedirectedToLaneId = "kids", RedirectedToLaneName = "Kids", FileName = "f.mkv", FullName = @"C:\f.mkv",
            ContentType = "Movie", PresetName = "HB 1080", Success = true, StartTime = T0, EndTime = T0
        };

        Assert.Equal(new Dictionary<string, int> { ["Movies"] = 1 }, RunBreakdown.From(new[] { redirected }).Lanes);
    }

    [Fact]
    public void TheAverageSpeed_IsEachFilesFpsWeightedByHowLongItTook_AndIgnoresFilesWithNoReading()
    {
        var results = new[]
        {
            Result(fps: 20, seconds: 300),     // 6000 frames
            Result(fps: 40, seconds: 100),     // 4000 frames
            Result(fps: null, seconds: 5000),  // no reading: must not drag the figure down
            Result(passthrough: true, fps: null, seconds: 10)
        };

        Assert.Equal(25, RunBreakdown.AverageFps(results));    // 10000 frames over 400 seconds
    }

    [Fact]
    public void AverageSpeed_IsNullWhenNoFileHasAReading_NeverZero()
    {
        Assert.Null(RunBreakdown.AverageFps(new[] { Result(fps: null), Result(passthrough: true) }));
        Assert.Null(RunBreakdown.AverageFps(Array.Empty<ConversionResult>()));
        Assert.Null(RunBreakdown.From(new[] { Result(fps: 0) }).AvgFps);
    }

    [Fact]
    public void TheTextForm_RoundTrips_EvenForNamesHoldingTheCharactersTheFormatUses_BiggestFirst()
    {
        var counts = new Dictionary<string, int> { ["Kids, 4K | HDR: the \"best\" 100%"] = 3, ["Movies"] = 7 };

        var text = RunBreakdown.Encode(counts);

        Assert.StartsWith("Movies:7|", text);
        Assert.DoesNotContain(',', text);                       // it sits inside a CSV field
        Assert.DoesNotContain('"', text);
        Assert.Equal(counts, RunBreakdown.Decode(text));
    }

    [Fact]
    public void ABlankIsUnknown_AKnownEmptyListIsDashAndRoundTrips()
    {
        Assert.Equal("", RunBreakdown.Encode(null));
        Assert.Null(RunBreakdown.Decode(""));
        Assert.Null(RunBreakdown.Decode(null));
        Assert.Equal("-", RunBreakdown.Encode(new Dictionary<string, int>()));
        Assert.Empty(RunBreakdown.Decode("-")!);
    }

    [Fact]
    public void Decode_SkipsBrokenPieces_RatherThanFailing()
    {
        var decoded = RunBreakdown.Decode("Movies:5|garbage|:3|TV:x|Kids:2")!;

        Assert.Equal(new Dictionary<string, int> { ["Movies"] = 5, ["Kids"] = 2 }, decoded);
    }
}
