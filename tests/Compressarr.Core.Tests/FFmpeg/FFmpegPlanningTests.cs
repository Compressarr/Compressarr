using Compressarr.Core.FFmpeg;

namespace Compressarr.Core.Tests.FFmpeg;

public class FFmpegPlanningTests : Presets.AppDataTestBase
{
    private static FFmpegProfile Builtin(string name) => new FFmpegProfileStore().GetBuiltIns().Single(p => p.Name == name);

    // ---- ffprobe parsing ------------------------------------------------------------------------

    [Fact]
    public void Parse_ReadsStreamsDurationChaptersAndLanguages()
    {
        var probe = FFmpegFixtures.Uhd();

        Assert.Equal(7200.5, probe.DurationSeconds);
        Assert.Equal(2, probe.ChapterCount);
        Assert.Equal(9, probe.Streams.Count);
        Assert.Equal(0, probe.Video!.Index); // the cover art (index 8) is not "the video"
        Assert.Equal(3, probe.Audio.Count());
        Assert.Equal(new[] { "eng", "eng", "fra" }, probe.Audio.Select(a => a.Language).ToArray());
        Assert.Equal(8, probe.Audio.First().Channels);
        Assert.Equal(3, probe.Subtitles.Count());
        Assert.Single(probe.Attachments);
        Assert.True(probe.Streams.Single(s => s.Index == 8).IsAttachedPicture);
    }

    [Fact]
    public void Parse_ReadsHdr10StaticMetadata_InEncoderUnits()
    {
        var video = FFmpegFixtures.Uhd().Video!;

        Assert.True(video.IsHdr);
        Assert.False(video.HasDolbyVision);
        Assert.Equal(new MasteringDisplay(34000, 16000, 13250, 34500, 7500, 3000, 15635, 16450, 10000000, 50), video.Mastering);
        Assert.Equal(new ContentLight(1000, 400), video.ContentLight);
    }

    [Fact]
    public void Parse_UntaggedStreams_AreUndefined_AndFieldOrderIsRead()
    {
        var dvd = FFmpegFixtures.Dvd();

        Assert.Equal("und", dvd.Audio.Single().Language);
        Assert.True(dvd.Video!.IsInterlaced);
        Assert.Equal(5400, dvd.DurationSeconds);
    }

    [Fact]
    public void Parse_DolbyVision_IsDetectedFromTheConfigurationRecord()
    {
        var probe = FFprobeParser.Parse(FFmpegFixtures.DolbyVisionRip);

        Assert.True(probe.Video!.HasDolbyVision);
        Assert.True(probe.HasDynamicHdrMetadata);
        Assert.Equal("Dolby Vision", probe.DynamicHdrName);
    }

    [Fact]
    public void Parse_Hdr10Plus_IsDetectedFromTheFirstFrames()
    {
        var withPlus = FFprobeParser.Parse(FFmpegFixtures.UhdHdr10Rip, FFmpegFixtures.Hdr10PlusFrames);
        var without = FFprobeParser.Parse(FFmpegFixtures.UhdHdr10Rip, FFmpegFixtures.PlainFrames);

        Assert.True(withPlus.HasHdr10Plus);
        Assert.Equal("HDR10+", withPlus.DynamicHdrName);
        Assert.False(without.HasHdr10Plus);
        Assert.False(without.HasDynamicHdrMetadata); // static HDR10 is fine for ffmpeg
        Assert.False(FFprobeParser.Parse(FFmpegFixtures.UhdHdr10Rip, "not json").HasHdr10Plus);
    }

    [Fact]
    public void Parse_AFileWithNoDuration_HasNull()
    {
        var probe = FFprobeParser.Parse("""{ "streams": [ { "index": 0, "codec_type": "video", "codec_name": "h264" } ], "format": { } }""");

        Assert.Null(probe.DurationSeconds);
    }

    // ---- audio selection -----------------------------------------------------------------------

    [Fact]
    public void Audio_First_PicksTheFirstTrackOfTheFirstMatchingLanguage_AndDropsTheRest()
    {
        var plan = FFmpegPlanner.Plan(Builtin("Compressarr SD-HD"), FFmpegFixtures.Uhd())!;

        var kept = Assert.Single(plan.Audio);
        Assert.Equal(1, kept.Stream.Index); // TrueHD, the first English track
        Assert.Equal(new[] { 2, 3 }, plan.Dropped.Where(d => d.Stream.Kind == MediaStreamKind.Audio).Select(d => d.Stream.Index).ToArray());
    }

    [Fact]
    public void Audio_All_KeepsEveryMatchingTrack_InLanguageOrder()
    {
        var plan = FFmpegPlanner.Plan(Builtin("Compressarr UHD AV1"), FFmpegFixtures.Uhd())!;

        Assert.Equal(new[] { 1, 2 }, plan.Audio.Select(a => a.Stream.Index).ToArray()); // both English, not the French one
        Assert.Single(plan.Dropped, d => d.Stream.Index == 3);
    }

    [Fact]
    public void Audio_LanguageListOrder_DecidesBeforeFilePosition()
    {
        var profile = Builtin("Compressarr SD-HD");
        profile.AudioLanguages = new() { "fra", "eng" };

        var plan = FFmpegPlanner.Plan(profile, FFmpegFixtures.Uhd())!;

        Assert.Equal(3, Assert.Single(plan.Audio).Stream.Index);
    }

    [Fact]
    public void Audio_AnyAndUnd_MatchUntaggedTracks()
    {
        var profile = Builtin("Compressarr SD-HD");
        profile.AudioLanguages = new() { "jpn", "und" };

        var plan = FFmpegPlanner.Plan(profile, FFmpegFixtures.Dvd())!;

        Assert.Equal(1, Assert.Single(plan.Audio).Stream.Index);
    }

    [Fact]
    public void Audio_NothingMatches_KeepsTheFirstTrack_RatherThanSilence()
    {
        var profile = Builtin("Compressarr SD-HD");
        profile.AudioLanguages = new() { "jpn" };

        var plan = FFmpegPlanner.Plan(profile, FFmpegFixtures.Uhd())!;

        Assert.Equal(1, Assert.Single(plan.Audio).Stream.Index);
        Assert.Contains(plan.Notes, n => n.Contains("first audio track was kept"));
    }

    [Fact]
    public void Audio_PassThrough_CopiesAListedFormat_ElseEncodes()
    {
        var profile = Builtin("Compressarr UHD AV1");
        profile.PassthroughCodecs = new() { "ac3" }; // TrueHD is no longer allowed through

        var plan = FFmpegPlanner.Plan(profile, FFmpegFixtures.Uhd())!;

        Assert.Equal("encode", plan.Audio[0].Action);
        Assert.Equal("eac3", plan.Audio[0].Codec);
        Assert.Contains("truehd isn't on the pass-through list", plan.Audio[0].Reason);
        Assert.Equal("copy", plan.Audio[1].Action);
    }

    [Fact]
    public void Audio_Mixdown_IsALimit_NeverAnUpmix()
    {
        var profile = Builtin("Compressarr SD-HD");
        profile.AudioMixdown = "stereo";
        var down = FFmpegPlanner.Plan(profile, FFmpegFixtures.Uhd())!;      // 8ch source
        var same = FFmpegPlanner.Plan(profile, FFmpegFixtures.Dvd())!;      // 2ch source
        profile.AudioMixdown = "7.1";
        var noUpmix = FFmpegPlanner.Plan(profile, FFmpegFixtures.Dvd())!;

        Assert.Equal(2, down.Audio[0].Channels);
        Assert.Null(same.Audio[0].Channels);
        Assert.Null(noUpmix.Audio[0].Channels);
    }

    [Fact]
    public void Audio_ACodecThatCannotHoldTheChannels_MixesDownToItsLimit_AndSaysSo()
    {
        var profile = Builtin("Compressarr SD-HD");
        profile.AudioMixdown = "7.1";                                       // the profile allows 7.1, E-AC3 cannot carry it
        var eac3 = FFmpegPlanner.Plan(profile, FFmpegFixtures.Uhd())!;       // 8ch source
        profile.AudioCodec = "aac";
        var aac = FFmpegPlanner.Plan(profile, FFmpegFixtures.Uhd())!;
        profile.AudioCodec = "mp3";
        var mp3 = FFmpegPlanner.Plan(profile, FFmpegFixtures.Uhd())!;

        Assert.Equal(6, eac3.Audio[0].Channels);
        Assert.Contains("holds at most 6 channels", eac3.Audio[0].Reason);
        Assert.Null(aac.Audio[0].Channels);                                  // AAC carries 7.1 as it is
        Assert.DoesNotContain("holds at most", aac.Audio[0].Reason);
        Assert.Equal(2, mp3.Audio[0].Channels);
    }

    [Fact]
    public void Audio_DtsHdIsToldApartFromPlainDts()
    {
        var plain = new MediaStream { Codec = "dts", CodecProfile = "DTS" };
        var hd = new MediaStream { Codec = "dts", CodecProfile = "DTS-HD MA" };

        Assert.Equal("dts", FFmpegPlanner.MaskName(plain));
        Assert.Equal("dtshd", FFmpegPlanner.MaskName(hd));
    }

    [Fact]
    public void Audio_Mp4_CannotCopyWhatItCannotHold()
    {
        var profile = Builtin("Compressarr UHD AV1");
        profile.Container = "mp4";

        var plan = FFmpegPlanner.Plan(profile, FFmpegFixtures.Uhd())!;

        Assert.Equal("encode", plan.Audio[0].Action); // TrueHD can't live in MP4
        Assert.Contains("MP4 can't hold truehd", plan.Audio[0].Reason);
        Assert.Equal("copy", plan.Audio[1].Action);   // AC3 can
    }

    // ---- subtitles & attachments ---------------------------------------------------------------

    [Fact]
    public void Subtitles_Mkv_KeepsMatchingTracks_AndTheFontsThatGoWithThem()
    {
        var plan = FFmpegPlanner.Plan(Builtin("Compressarr SD-HD"), FFmpegFixtures.Uhd())!;

        Assert.Equal(new[] { 4, 5 }, plan.Subtitles.Select(s => s.Stream.Index).ToArray());
        Assert.All(plan.Subtitles, s => Assert.Equal("copy", s.Action));
        Assert.Equal(7, Assert.Single(plan.Attachments).Index);
        Assert.Single(plan.Dropped, d => d.Stream.Index == 6); // French
    }

    [Fact]
    public void Subtitles_Mp4_ConvertsText_AndDropsPictureTracks_WithTheReason()
    {
        var profile = Builtin("Compressarr SD-HD");
        profile.Container = "mp4";

        var plan = FFmpegPlanner.Plan(profile, FFmpegFixtures.Uhd())!;

        var kept = Assert.Single(plan.Subtitles);
        Assert.Equal(4, kept.Stream.Index);
        Assert.Equal("mov_text", kept.Codec);
        Assert.Contains(plan.Dropped, d => d.Stream.Index == 5 && d.Reason.Contains("picture subtitle"));
        Assert.Empty(plan.Attachments);
        Assert.Contains(plan.Dropped, d => d.Stream.Index == 7 && d.Reason.Contains("MP4"));
    }

    [Fact]
    public void Subtitles_None_DropsThemAll()
    {
        var profile = Builtin("Compressarr SD-HD");
        profile.SubtitleTracks = "none";

        var plan = FFmpegPlanner.Plan(profile, FFmpegFixtures.Uhd())!;

        Assert.Empty(plan.Subtitles);
        Assert.Empty(plan.Attachments);
        Assert.Equal(3, plan.Dropped.Count(d => d.Stream.Kind == MediaStreamKind.Subtitle));
    }

    [Fact]
    public void Subtitles_First_KeepsOnlyTheFirstMatch()
    {
        var profile = Builtin("Compressarr SD-HD");
        profile.SubtitleTracks = "first";

        var plan = FFmpegPlanner.Plan(profile, FFmpegFixtures.Uhd())!;

        Assert.Equal(4, Assert.Single(plan.Subtitles).Stream.Index);
    }

    // ---- picture and the rest ------------------------------------------------------------------

    [Theory]
    [InlineData("auto", "tt", true, false)]
    [InlineData("auto", "progressive", false, false)]
    [InlineData("auto", "unknown", false, false)]
    [InlineData("always", "progressive", true, true)]
    [InlineData("off", "tt", false, false)]
    public void Deinterlace_FollowsTheProfileAndTheFieldOrder(string mode, string fieldOrder, bool expected, bool all)
    {
        var profile = Builtin("Compressarr SD-HD");
        profile.Deinterlace = mode;
        var probe = new MediaProbeResult
        {
            Streams = new[] { new MediaStream { Index = 0, Kind = MediaStreamKind.Video, Codec = "h264", FieldOrder = fieldOrder, Width = 100, Height = 100 } }
        };

        var plan = FFmpegPlanner.Plan(profile, probe)!;

        Assert.Equal(expected, plan.Deinterlace);
        Assert.Equal(all, plan.DeinterlaceAll);
        Assert.False(string.IsNullOrWhiteSpace(plan.DeinterlaceReason));
    }

    [Fact]
    public void Plan_AFileWithNoVideo_IsNull()
    {
        var probe = new MediaProbeResult { Streams = new[] { new MediaStream { Index = 0, Kind = MediaStreamKind.Audio, Codec = "aac" } } };

        Assert.Null(FFmpegPlanner.Plan(Builtin("Compressarr SD-HD"), probe));
    }

    [Fact]
    public void Chapters_AreKeptOnlyWhenWantedAndPresent()
    {
        var profile = Builtin("Compressarr SD-HD");

        Assert.True(FFmpegPlanner.Plan(profile, FFmpegFixtures.Uhd())!.KeepChapters);
        Assert.False(FFmpegPlanner.Plan(profile, FFmpegFixtures.Dvd())!.KeepChapters);
        profile.Chapters = false;
        Assert.False(FFmpegPlanner.Plan(profile, FFmpegFixtures.Uhd())!.KeepChapters);
    }

    [Fact]
    public void ExtraVideoStreams_AndDataStreams_AreDropped()
    {
        var plan = FFmpegPlanner.Plan(Builtin("Compressarr SD-HD"), FFmpegFixtures.Uhd())!;

        Assert.Contains(plan.Dropped, d => d.Stream.Index == 8 && d.Reason.Contains("cover art"));
    }
}
