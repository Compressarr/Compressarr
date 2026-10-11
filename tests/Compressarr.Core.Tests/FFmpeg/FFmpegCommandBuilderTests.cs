using Compressarr.Core.FFmpeg;

namespace Compressarr.Core.Tests.FFmpeg;

/// <summary>Golden tests: the ffmpeg command line is the one thing that must never change by accident.</summary>
public class FFmpegCommandBuilderTests
{
    private static FFmpegProfile Builtin(string name) => new FFmpegProfileStore().GetBuiltIns().Single(p => p.Name == name);

    private static string Command(FFmpegProfile profile, MediaProbeResult probe, Action<FFmpegPlan>? tweakPlan = null, string? extra = null)
    {
        var plan = FFmpegPlanner.Plan(profile, probe)!;
        tweakPlan?.Invoke(plan);
        var args = FFmpegCommandBuilder.Build(profile, probe, plan, "in.mkv", "out.mkv", extra);
        // one argument per line is easier to read in a failure than one long string
        return string.Join("\n", args);
    }

    private static string Flat(string text) => text.Replace("\n", " ");

    private const string Preamble = "-hide_banner -nostdin -y -progress pipe:1 -nostats -i in.mkv";

    [Fact]
    public void SdHd_OnAnHdr10Rip_IsExactlyThisCommand()
    {
        var command = Flat(Command(Builtin("Compressarr SD-HD"), FFmpegFixtures.Uhd()));

        Assert.Equal(
            Preamble +
            " -map 0:0 -map 0:1 -map 0:4 -map 0:5 -map 0:7 -map_chapters 0 -map_metadata 0" +
            " -c:v libx265 -preset veryfast -crf 24 -profile:v main10 -pix_fmt yuv420p10le" +
            " -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc -color_range tv" +
            " -x265-params master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,50):max-cll=1000,400" +
            " -fps_mode vfr" +
            " -c:a:0 eac3 -b:a:0 512k -ac:a:0 6" +
            " -c:s:0 copy -c:s:1 copy" +
            " -c:t copy" +
            " out.mkv",
            command);
    }

    [Fact]
    public void UhdAv1_OnAnHdr10Rip_IsExactlyThisCommand()
    {
        var command = Flat(Command(Builtin("Compressarr UHD AV1"), FFmpegFixtures.Uhd()));

        Assert.Equal(
            Preamble +
            " -map 0:0 -map 0:1 -map 0:2 -map 0:4 -map 0:5 -map 0:7 -map_chapters 0 -map_metadata 0" +
            " -c:v libsvtav1 -preset 4 -crf 30 -pix_fmt yuv420p10le" +
            " -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc -color_range tv" +
            " -svtav1-params tune=1:mastering-display=G(0.265,0.69)B(0.15,0.06)R(0.68,0.32)WP(0.3127,0.329)L(1000,0.005):content-light=1000,400" +
            " -fps_mode vfr" +
            " -c:a:0 copy -c:a:1 copy" +
            " -c:s:0 copy -c:s:1 copy" +
            " -c:t copy" +
            " out.mkv",
            command);
    }

    [Fact]
    public void Nvenc_UsesConstantQualityRateControl()
    {
        var command = Flat(Command(Builtin("HEVC NVENC (fast)"), FFmpegFixtures.Uhd()));

        Assert.Contains("-c:v hevc_nvenc -preset p5 -rc vbr -cq 28 -b:v 0 -profile:v main10 -pix_fmt p010le", command);
        Assert.DoesNotContain("-crf", command);
    }

    [Fact]
    public void AnInterlacedDvdRip_GetsDeinterlacedAndKeepsItsPictureSubtitle_AndNoChapters()
    {
        var command = Flat(Command(Builtin("Compressarr SD-HD"), FFmpegFixtures.Dvd()));

        Assert.Equal(
            Preamble +
            " -map 0:0 -map 0:1 -map 0:2 -map_chapters -1 -map_metadata 0" +
            " -c:v libx265 -preset veryfast -crf 24 -profile:v main10 -pix_fmt yuv420p10le" +
            " -color_primaries smpte170m -color_trc smpte170m -colorspace smpte170m" +
            " -vf bwdif=mode=send_frame:parity=auto:deint=interlaced" +
            " -fps_mode vfr" +
            " -c:a:0 eac3 -b:a:0 512k" +
            " -c:s:0 copy" +
            " out.mkv",
            command);
    }

    [Fact]
    public void Crop_IsAddedAfterTheDeinterlaceFilter()
    {
        var command = Flat(Command(Builtin("Compressarr SD-HD"), FFmpegFixtures.Dvd(), plan => plan.Crop = new CropRect(704, 464, 8, 8)));

        Assert.Contains("-vf bwdif=mode=send_frame:parity=auto:deint=interlaced,crop=704:464:8:8", command);
    }

    [Fact]
    public void DeinterlaceAlways_DeinterlacesAllFrames()
    {
        var profile = Builtin("Compressarr SD-HD");
        profile.Deinterlace = "always";

        Assert.Contains("deint=all", Command(profile, FFmpegFixtures.Uhd()));
    }

    [Fact]
    public void Mp4_AddsTheContainerOptions_AndConvertsSubtitles()
    {
        var profile = Builtin("Compressarr SD-HD");
        profile.Container = "mp4";
        profile.Optimize = true;

        var command = Flat(Command(profile, FFmpegFixtures.Uhd()));

        Assert.Contains("-map 0:0 -map 0:1 -map 0:4 -map_chapters 0", command); // PGS and the font are gone
        Assert.Contains("-c:s:0 mov_text", command);
        Assert.Contains("-movflags +faststart", command);
        Assert.Contains("-tag:v hvc1", command);
        Assert.DoesNotContain("-c:t", command);
    }

    [Fact]
    public void ConstantFrameRate_AndBitrateMode()
    {
        var profile = Builtin("Compressarr SD-HD");
        profile.FramerateMode = "cfr";
        profile.Framerate = 23.976;
        profile.QualityMode = "bitrate";
        profile.VideoBitrateKbps = 4500;

        var command = Flat(Command(profile, FFmpegFixtures.Uhd()));

        Assert.Contains("-b:v 4500k", command);
        Assert.DoesNotContain("-crf", command);
        Assert.Contains("-fps_mode cfr -r 23.976", command);
    }

    [Fact]
    public void Tune_IsPassedToX265_ButInsideSvtParamsForAv1()
    {
        var x265 = Builtin("Compressarr SD-HD");
        x265.Tune = "animation";
        Assert.Contains("-tune animation", Flat(Command(x265, FFmpegFixtures.Dvd())));

        var av1 = Builtin("Compressarr UHD AV1");
        av1.Tune = "fastdecode";
        var command = Flat(Command(av1, FFmpegFixtures.Dvd()));
        Assert.DoesNotContain("-tune", command);
        Assert.Contains("-svtav1-params fast-decode=1", command);
    }

    [Fact]
    public void AudioEncode_Downmix_AddsAChannelLimit()
    {
        var profile = Builtin("Compressarr SD-HD");
        profile.AudioCodec = "aac";
        profile.AudioBitrateKbps = 160;
        profile.AudioMixdown = "stereo";

        Assert.Contains("-c:a:0 aac -b:a:0 160k -ac:a:0 2", Flat(Command(profile, FFmpegFixtures.Uhd())));
    }

    [Theory]
    [InlineData("opus", "libopus")]
    [InlineData("mp3", "libmp3lame")]
    [InlineData("ac3", "ac3")]
    public void AudioCodecNames_MapToFfmpegEncoders(string codec, string encoder)
    {
        var profile = Builtin("Compressarr SD-HD");
        profile.AudioCodec = codec;

        Assert.Contains($"-c:a:0 {encoder} ", Flat(Command(profile, FFmpegFixtures.Uhd())));
    }

    [Fact]
    public void Flac_HasNoBitrate()
    {
        var profile = Builtin("Compressarr SD-HD");
        profile.AudioCodec = "flac";

        Assert.DoesNotContain("-b:a:0", Command(profile, FFmpegFixtures.Uhd()));
    }

    [Fact]
    public void ExtraArguments_FromTheProfileAndTheSetting_GoJustBeforeTheOutput()
    {
        var profile = Builtin("Compressarr SD-HD");
        profile.ExtraArgs = "-metadata comment=\"made here\"";

        var args = FFmpegCommandBuilder.Build(profile, FFmpegFixtures.Uhd(), FFmpegPlanner.Plan(profile, FFmpegFixtures.Uhd())!, "in.mkv", "out.mkv", "-threads 6");

        Assert.Equal(new[] { "-metadata", "comment=made here", "-threads", "6", "out.mkv" }, args.TakeLast(5).ToArray());
    }

    [Fact]
    public void PathsWithSpaces_StayOneArgumentEach()
    {
        var profile = Builtin("Compressarr SD-HD");
        var probe = FFmpegFixtures.Uhd();

        var args = FFmpegCommandBuilder.Build(profile, probe, FFmpegPlanner.Plan(profile, probe)!, @"D:\My Media\Show - S01E01.mkv", @"D:\Temp Processing\out file.mkv", null);

        Assert.Contains(@"D:\My Media\Show - S01E01.mkv", args);
        Assert.Equal(@"D:\Temp Processing\out file.mkv", args[^1]);
    }

    [Fact]
    public void NoMapZero_EverAppears_SoUnwantedStreamsCannotSneakIn()
    {
        var args = FFmpegCommandBuilder.Build(Builtin("Compressarr SD-HD"), FFmpegFixtures.Uhd(), FFmpegPlanner.Plan(Builtin("Compressarr SD-HD"), FFmpegFixtures.Uhd())!, "in.mkv", "out.mkv", null);

        Assert.DoesNotContain("0", args.Where((a, i) => i > 0 && args[i - 1] == "-map" && a == "0"));
        Assert.DoesNotContain("0:8", args); // the cover art
        Assert.DoesNotContain("0:3", args); // the French audio
    }

    [Fact]
    public void SvtMasteringDisplay_IsInDecimals_AndX265InIntegers()
    {
        var m = new MasteringDisplay(34000, 16000, 13250, 34500, 7500, 3000, 15635, 16450, 10000000, 50);

        Assert.Equal("G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,50)", FFmpegCommandBuilder.X265MasterDisplay(m));
        Assert.Equal("G(0.265,0.69)B(0.15,0.06)R(0.68,0.32)WP(0.3127,0.329)L(1000,0.005)", FFmpegCommandBuilder.SvtMasteringDisplay(m));
    }
}
