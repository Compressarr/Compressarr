using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.FFmpeg;

namespace Compressarr.Core.Tests.FFmpeg;

public class FFmpegToolingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("compressarr-fftool-tests-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---- capabilities ---------------------------------------------------------------------------

    private const string VersionOutput = """
        ffmpeg version N-118000-gabcdef-20260101 Copyright (c) 2000-2026 the FFmpeg developers
        built with gcc 14.2.0 (crosstool-NG)
        configuration: --prefix=/ffbuild/prefix --enable-gpl --enable-version3 --enable-libx264 --enable-libx265 --enable-libsvtav1 --enable-nvenc
        libavutil      60.  8.100 / 60.  8.100
        """;

    private const string EncodersOutput = """
        Encoders:
         V..... = Video
         A..... = Audio
         S..... = Subtitle
         .F.... = Frame-level multithreading
         ------
         V....D libx264              libx264 H.264 / AVC / MPEG-4 AVC (codec h264)
         V....D libx265              libx265 H.265 / HEVC (codec hevc)
         V....D libsvtav1            SVT-AV1(Scalable Video Technology for AV1) encoder (codec av1)
         V....D hevc_nvenc           NVIDIA NVENC hevc encoder (codec hevc)
         A....D aac                  AAC (Advanced Audio Coding)
         A....D eac3                 ATSC A/52 E-AC-3
         A....D ac3                  ATSC A/52A (AC-3)
         A....D libopus              libopus Opus (codec opus)
         S..... srt                  SubRip subtitle (codec subrip)
        """;

    [Fact]
    public void ParseVersion_AndBuildNote()
    {
        Assert.Equal("N-118000-gabcdef-20260101", FFmpegCapabilityParser.ParseVersion(VersionOutput));
        Assert.Equal("GPL build", FFmpegCapabilityParser.ParseBuildNote(VersionOutput));
        Assert.Null(FFmpegCapabilityParser.ParseBuildNote("ffmpeg version 7.0 --enable-libx264"));
        Assert.Null(FFmpegCapabilityParser.ParseVersion("not ffmpeg"));
    }

    [Fact]
    public void ParseEncoders_ReadsNamesAfterTheLegend_NotTheLegendItself()
    {
        var encoders = FFmpegCapabilityParser.ParseEncoders(EncodersOutput);

        Assert.Contains("libx265", encoders);
        Assert.Contains("libsvtav1", encoders);
        Assert.Contains("hevc_nvenc", encoders);
        Assert.Contains("libopus", encoders);
        Assert.Contains("srt", encoders);
        Assert.DoesNotContain("Video", encoders);
        Assert.DoesNotContain("=", encoders);
        Assert.Equal(9, encoders.Count);
    }

    [Fact]
    public async Task Probe_RejectsAProgramThatIsNotFfmpeg_ButAcceptsOneThatSaysItIs()
    {
        if (!OperatingSystem.IsWindows()) return; // stand-ins are .cmd files

        var notFfmpeg = Path.Combine(_dir, "notffmpeg.cmd");
        File.WriteAllText(notFfmpeg, "@echo off\r\necho I am a text editor\r\n");
        Assert.Null(await new FFmpegCapabilityProbe().DetectAsync(notFfmpeg, CancellationToken.None));

        var fake = Path.Combine(_dir, "fakeffmpeg.cmd");
        File.WriteAllText(fake, "@echo off\r\nif \"%2\"==\"-version\" (echo ffmpeg version 7.1 Copyright\r\n) else (echo Encoders:\r\necho  ------\r\necho  V....D libx264 libx264)\r\n");
        var caps = await new FFmpegCapabilityProbe().DetectAsync(fake, CancellationToken.None);

        Assert.NotNull(caps);
        Assert.Equal("7.1", caps!.Version);
        Assert.True(caps.Has("libx264"));
    }

    private static FFmpegCapabilities Caps(bool? nvenc, params string[] encoders) =>
        new("7.1", new HashSet<string>(encoders, StringComparer.OrdinalIgnoreCase), nvenc, "GPL build");

    private static FFmpegCapabilities GpuCaps(bool hevc, bool av1, params string[] encoders) =>
        new("7.1", new HashSet<string>(encoders, StringComparer.OrdinalIgnoreCase), hevc, "GPL build",
            new Dictionary<string, bool> { ["hevc_nvenc"] = hevc, ["av1_nvenc"] = av1 });

    [Fact]
    public void AV1NvencHasItsOwnGpuSession_BecauseAnOlderGpuOpensH265ButNotAV1()
    {
        var olderGpu = GpuCaps(hevc: true, av1: false, "hevc_nvenc", "av1_nvenc", "h264_nvenc");
        var newerGpu = GpuCaps(hevc: true, av1: true, "hevc_nvenc", "av1_nvenc", "h264_nvenc");

        Assert.True(olderGpu.CanUse("hevc_nvenc"));
        Assert.False(olderGpu.CanUse("av1_nvenc"));        // listed in the build, H.265 works, AV1 does not
        Assert.True(olderGpu.CanUse("h264_nvenc"));        // no test of its own: it follows the H.265 one
        Assert.True(newerGpu.CanUse("av1_nvenc"));
        Assert.False(GpuCaps(false, false, "hevc_nvenc", "av1_nvenc").CanUse("av1_nvenc")); // no NVIDIA GPU at all
        Assert.False(GpuCaps(true, true, "hevc_nvenc").CanUse("av1_nvenc"));                // a build without the AV1 encoder
    }

    [Fact]
    public void AnAV1NvencProfile_OnAGpuWithoutAV1_IsFlaggedWithTheRealReason()
    {
        var profile = new FFmpegProfile { Name = "AV1 GPU", VideoCodec = "av1_nvenc", AudioCodec = "eac3" };
        var olderGpu = GpuCaps(hevc: true, av1: false, "hevc_nvenc", "av1_nvenc", "eac3");
        var newerGpu = GpuCaps(hevc: true, av1: true, "hevc_nvenc", "av1_nvenc", "eac3");

        var missing = FFmpegProfileRequirements.Missing(profile, olderGpu);

        Assert.Contains(missing, m => m.Contains("no NVIDIA GPU session") && m.Contains("RTX 40-series"));
        Assert.Empty(FFmpegProfileRequirements.Missing(profile, newerGpu));
        // An H.265 profile on the same older GPU is fine.
        Assert.Empty(FFmpegProfileRequirements.Missing(new FFmpegProfile { Name = "H265 GPU", VideoCodec = "hevc_nvenc", AudioCodec = "eac3" }, olderGpu));
    }

    [Fact]
    public void NvencNeedsARealGpuSession_NotJustTheEncoderInTheBuild()
    {
        Assert.True(Caps(true, "hevc_nvenc").CanUse("hevc_nvenc"));
        Assert.False(Caps(false, "hevc_nvenc").CanUse("hevc_nvenc"));   // listed, but no GPU
        Assert.False(Caps(null, "hevc_nvenc").CanUse("hevc_nvenc"));
        Assert.True(Caps(false, "libx265").CanUse("libx265"));          // software never needs a GPU
        Assert.False(Caps(true).CanUse("libx265"));
    }

    [Fact]
    public void MissingEncoders_AreReportedForTheProfile()
    {
        var store = new FFmpegProfileStore();
        var sdhd = store.Find("Compressarr SD-HD")!;
        var nvenc = store.Find("HEVC NVENC (fast)")!;

        Assert.Empty(FFmpegProfileRequirements.Missing(sdhd, Caps(false, "libx265", "eac3")));
        Assert.Equal(new[] { "libx265", "eac3" }, FFmpegProfileRequirements.Missing(sdhd, Caps(false)).ToArray());
        Assert.Contains(FFmpegProfileRequirements.Missing(nvenc, Caps(false, "hevc_nvenc", "eac3")), m => m.Contains("no NVIDIA GPU session"));
        Assert.Empty(FFmpegProfileRequirements.Missing(nvenc, Caps(true, "hevc_nvenc", "eac3")));
    }

    // ---- installer ------------------------------------------------------------------------------

    private const string ReleaseJson = """
        {
          "name": "Latest Auto-Build (2026-10-06 13:07)", "tag_name": "latest", "html_url": "https://github.com/BtbN/FFmpeg-Builds/releases/tag/latest",
          "assets": [
            { "name": "ffmpeg-master-latest-linux64-gpl.tar.xz", "size": 1, "browser_download_url": "https://x/linux", "digest": "sha256:aa" },
            { "name": "ffmpeg-master-latest-win64-gpl.zip", "size": 192500000, "browser_download_url": "https://x/win64", "digest": "sha256:ABCDEF01" },
            { "name": "ffmpeg-master-latest-winarm64-gpl.zip", "size": 180000000, "browser_download_url": "https://x/arm64" },
            { "name": "ffmpeg-n9.0-latest-win64-gpl-9.0.zip", "size": 194000000, "browser_download_url": "https://x/n9.0-win64", "digest": "sha256:90AA" },
            { "name": "ffmpeg-n9.0-latest-win64-lgpl-9.0.zip", "size": 171000000, "browser_download_url": "https://x/n9.0-lgpl", "digest": "sha256:90BB" },
            { "name": "ffmpeg-n9.0-latest-winarm64-gpl-9.0.zip", "size": 136000000, "browser_download_url": "https://x/n9.0-arm64", "digest": "sha256:90CC" },
            { "name": "ffmpeg-n8.1-latest-win64-gpl-8.1.zip", "size": 193000000, "browser_download_url": "https://x/n8.1-win64", "digest": "sha256:81AA" },
            { "name": "ffmpeg-n10.0-latest-win64-gpl-10.0.zip", "size": 195000000, "browser_download_url": "https://x/n10.0-win64", "digest": "sha256:100AA" }
          ]
        }
        """;

    [Fact]
    public void SelectAsset_Latest_PicksTheMasterGplBuildForTheArchitecture_WithItsChecksum()
    {
        var release = JsonNode.Parse(ReleaseJson)!.AsObject();

        var x64 = FFmpegInstaller.SelectAsset(release, Architecture.X64, FFmpegChannel.Latest)!;
        var arm = FFmpegInstaller.SelectAsset(release, Architecture.Arm64, FFmpegChannel.Latest)!;

        Assert.Equal("ffmpeg-master-latest-win64-gpl.zip", x64.AssetName);
        Assert.Equal("https://x/win64", x64.DownloadUrl);
        Assert.Equal(192500000, x64.SizeBytes);
        Assert.Equal("ABCDEF01", x64.Sha256);
        Assert.Equal("Latest Auto-Build (2026-10-06 13:07)", x64.Name);
        Assert.Equal("ffmpeg-master-latest-winarm64-gpl.zip", arm.AssetName);
        Assert.Null(arm.Sha256); // no checksum published for this one
        Assert.Null(x64.Branch);
    }

    [Fact]
    public void SelectAsset_Stable_PicksTheHighestNumberedReleaseBranch_NotTheMasterBuild_AndNotLgpl()
    {
        var release = JsonNode.Parse(ReleaseJson)!.AsObject();

        var x64 = FFmpegInstaller.SelectAsset(release, Architecture.X64, FFmpegChannel.Stable)!;

        // 10.0 beats 9.0 and 8.1 by number (a text sort would put "n9.0" above "n10.0").
        Assert.Equal("ffmpeg-n10.0-latest-win64-gpl-10.0.zip", x64.AssetName);
        Assert.Equal("10.0", x64.Branch);
        Assert.Equal("100AA", x64.Sha256);
        Assert.Equal("https://x/n10.0-win64", x64.DownloadUrl);
    }

    [Fact]
    public void SelectAsset_Stable_FollowsTheBranchesActuallyPublished()
    {
        var release = JsonNode.Parse(ReleaseJson)!.AsObject();
        foreach (var asset in release["assets"]!.AsArray().ToList())
        {
            if (asset!["name"]!.GetValue<string>().Contains("n10.0")) release["assets"]!.AsArray().Remove(asset);
        }

        var x64 = FFmpegInstaller.SelectAsset(release, Architecture.X64, FFmpegChannel.Stable)!;
        var arm = FFmpegInstaller.SelectAsset(release, Architecture.Arm64, FFmpegChannel.Stable)!;

        Assert.Equal("ffmpeg-n9.0-latest-win64-gpl-9.0.zip", x64.AssetName);
        Assert.Equal("9.0", x64.Branch);
        Assert.Equal("90AA", x64.Sha256);
        Assert.Equal("ffmpeg-n9.0-latest-winarm64-gpl-9.0.zip", arm.AssetName);
    }

    [Fact]
    public void SelectAsset_Stable_WithNoReleaseBranchPublished_IsNull_RatherThanQuietlyFallingBackToMaster()
    {
        var release = JsonNode.Parse("""{ "assets": [ { "name": "ffmpeg-master-latest-win64-gpl.zip", "size": 1, "browser_download_url": "https://x/m", "digest": "sha256:aa" } ] }""")!.AsObject();

        Assert.Null(FFmpegInstaller.SelectAsset(release, Architecture.X64, FFmpegChannel.Stable));
    }

    [Fact]
    public void SelectAsset_NothingSuitable_IsNull()
    {
        foreach (var channel in new[] { FFmpegChannel.Stable, FFmpegChannel.Latest })
        {
            Assert.Null(FFmpegInstaller.SelectAsset(JsonNode.Parse("""{ "assets": [ { "name": "other.zip" } ] }""")!.AsObject(), Architecture.X64, channel));
            Assert.Null(FFmpegInstaller.SelectAsset(JsonNode.Parse("""{ }""")!.AsObject(), Architecture.X64, channel));
        }
    }

    [Fact]
    public void TheChannel_DefaultsToStable_AndIsSavedAsText()
    {
        Assert.Equal(FFmpegChannel.Stable, new FFmpegSettings().Channel);

        var json = System.Text.Json.JsonSerializer.Serialize(new FFmpegSettings { Channel = FFmpegChannel.Latest });
        var back = System.Text.Json.JsonSerializer.Deserialize<FFmpegSettings>(json)!;

        Assert.Contains("\"Latest\"", json);
        Assert.Equal(FFmpegChannel.Latest, back.Channel);
        // A settings file from before the channel existed has no such field: it must read as Stable.
        Assert.Equal(FFmpegChannel.Stable, System.Text.Json.JsonSerializer.Deserialize<FFmpegSettings>("""{ "Path": "x" }""")!.Channel);
    }

    private string MakeZip(bool withProbe = true, bool withLicense = true)
    {
        var path = Path.Combine(_dir, "build.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Add(string name, string content)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(content);
            }
            Add("ffmpeg-master-latest-win64-gpl/bin/ffmpeg.exe", "ffmpeg bytes");
            if (withProbe) Add("ffmpeg-master-latest-win64-gpl/bin/ffprobe.exe", "ffprobe bytes");
            Add("ffmpeg-master-latest-win64-gpl/bin/ffplay.exe", "not wanted");
            Add("ffmpeg-master-latest-win64-gpl/doc/ffmpeg.html", "docs");
            if (withLicense) Add("ffmpeg-master-latest-win64-gpl/LICENSE.txt", "GPL text");
        }
        return path;
    }

    [Fact]
    public void VerifyAndExtract_KeepsOnlyFfmpegFfprobeAndTheLicense()
    {
        var install = Path.Combine(_dir, "install");

        var path = FFmpegInstaller.VerifyAndExtract(MakeZip(), "aBc", "ABC", install);

        Assert.Equal(Path.Combine(install, "ffmpeg.exe"), path);
        Assert.Equal(new[] { "ffmpeg.exe", "ffprobe.exe", "LICENSE.txt" }, Directory.GetFiles(install).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).Select(n => n!).OrderBy(n => n == "LICENSE.txt" ? 2 : n == "ffprobe.exe" ? 1 : 0).ToArray());
        Assert.Equal("ffmpeg bytes", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(install, "*.tmp"));
    }

    [Fact]
    public void VerifyAndExtract_AWrongChecksum_InstallsNothing()
    {
        var install = Path.Combine(_dir, "install");

        Assert.Throws<InvalidDataException>(() => FFmpegInstaller.VerifyAndExtract(MakeZip(), "AAAA", "BBBB", install));

        Assert.False(Directory.Exists(install) && Directory.GetFiles(install).Length > 0);
    }

    [Fact]
    public void VerifyAndExtract_AnArchiveMissingFfprobe_InstallsNothing_AndLeavesNoTempFiles()
    {
        var install = Path.Combine(_dir, "install");

        var ex = Assert.Throws<InvalidDataException>(() => FFmpegInstaller.VerifyAndExtract(MakeZip(withProbe: false), "X", "X", install));

        Assert.Contains("ffprobe.exe", ex.Message);
        Assert.Empty(Directory.GetFiles(install));
    }

    [Fact]
    public void VerifyAndExtract_ALicenseFileIsOptional()
    {
        var install = Path.Combine(_dir, "install");

        FFmpegInstaller.VerifyAndExtract(MakeZip(withLicense: false), "X", "X", install);

        Assert.True(File.Exists(Path.Combine(install, "ffprobe.exe")));
    }

    [Fact]
    public void VerifyAndExtract_ReplacesAnOlderInstall()
    {
        var install = Path.Combine(_dir, "install");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "ffmpeg.exe"), "old");

        FFmpegInstaller.VerifyAndExtract(MakeZip(), "X", "X", install);

        Assert.Equal("ffmpeg bytes", File.ReadAllText(Path.Combine(install, "ffmpeg.exe")));
    }

    [Fact]
    public async Task InstallAsync_WithNoPublishedChecksum_RefusesBeforeDownloadingAnything()
    {
        var installer = new FFmpegInstaller(new NoHttpFactory());
        var release = new FFmpegReleaseInfo("x", "a.zip", "https://x/a.zip", 1, "https://x", Sha256: null);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(release, Path.Combine(_dir, "i")));

        Assert.Contains("checksum", ex.Message);
    }

    private sealed class NoHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("must not be called");
    }

    // ---- which HandBrake encoders keep HDR --------------------------------------------------------

    [Theory]
    [InlineData("x265_10bit", HandBrakeHdrSupport.DynamicHdr)]
    [InlineData("x265_12bit", HandBrakeHdrSupport.DynamicHdr)]
    [InlineData("svt_av1_10bit", HandBrakeHdrSupport.DynamicHdr)]
    [InlineData("vt_h265_10bit", HandBrakeHdrSupport.DynamicHdr)]
    [InlineData("x264", HandBrakeHdrSupport.SdrOnly)]
    [InlineData("x264_10bit", HandBrakeHdrSupport.SdrOnly)]
    [InlineData("nvenc_h264", HandBrakeHdrSupport.SdrOnly)]
    [InlineData("qsv_h264", HandBrakeHdrSupport.SdrOnly)]
    [InlineData("mpeg4", HandBrakeHdrSupport.SdrOnly)]
    [InlineData("theora", HandBrakeHdrSupport.SdrOnly)]
    [InlineData("nvenc_h265", HandBrakeHdrSupport.StaticHdr)]
    [InlineData("nvenc_h265_10bit", HandBrakeHdrSupport.StaticHdr)]
    [InlineData("nvenc_av1_10bit", HandBrakeHdrSupport.StaticHdr)]
    [InlineData("qsv_h265_10bit", HandBrakeHdrSupport.StaticHdr)]
    [InlineData("vce_h265_10bit", HandBrakeHdrSupport.StaticHdr)]
    [InlineData("some_future_encoder", HandBrakeHdrSupport.StaticHdr)] // never refused just because it is unfamiliar
    [InlineData("", HandBrakeHdrSupport.StaticHdr)]
    public void HandBrakeEncoders_AreClassifiedByWhatTheyDoWithHdr(string encoder, HandBrakeHdrSupport expected)
    {
        Assert.Equal(expected, HandBrakeHdrGuard.SupportOf(encoder));
    }

    [Theory]
    [InlineData("x265", HandBrakeHdrSupport.StaticHdr)]
    [InlineData("svt_av1", HandBrakeHdrSupport.StaticHdr)]
    [InlineData("vt_h265", HandBrakeHdrSupport.StaticHdr)]
    [InlineData("x265_10bit", HandBrakeHdrSupport.DynamicHdr)]
    [InlineData("x265_12bit", HandBrakeHdrSupport.DynamicHdr)]
    [InlineData("svt_av1_10bit", HandBrakeHdrSupport.DynamicHdr)]
    [InlineData("vt_h265_10bit", HandBrakeHdrSupport.DynamicHdr)]
    public void DynamicHdrPassthrough_IsOnlyClaimedForTheDeepColourEncoders(string encoder, HandBrakeHdrSupport expected) =>
        Assert.Equal(expected, HandBrakeHdrGuard.SupportOf(encoder));

    [Fact]
    public void OnlyAProfileThatCannotBeRelied_OnForHdr_NeedsTheSourceRead()
    {
        Assert.False(HandBrakeHdrGuard.NeedsProbe(new HandBrakeProfileTraits("x265_10bit", true)));
        Assert.True(HandBrakeHdrGuard.NeedsProbe(new HandBrakeProfileTraits("x265_10bit", false)));  // switched off: Dolby Vision profile 5 still matters
        Assert.True(HandBrakeHdrGuard.NeedsProbe(new HandBrakeProfileTraits("x264", true)));
        Assert.True(HandBrakeHdrGuard.NeedsProbe(new HandBrakeProfileTraits("nvenc_h265", true)));
        Assert.True(HandBrakeHdrGuard.NeedsProbe(new HandBrakeProfileTraits("x265", true)));                       // 8-bit x265 cannot carry Dolby Vision / HDR10+
        Assert.True(HandBrakeHdrGuard.NeedsProbe(new HandBrakeProfileTraits("x265_10bit", true, KeepsDolbyVision: false)));  // "hdr10plus" only
        Assert.True(HandBrakeHdrGuard.NeedsProbe(new HandBrakeProfileTraits("x265_10bit", true, KeepsHdr10Plus: false)));    // "dolbyvision" only
    }

    // ---- Dolby Vision base layer (for "encode anyway") --------------------------------------------

    private static MediaProbeResult DvProbe(string record) => FFprobeParser.Parse(FFmpegFixtures.DolbyVisionRip.Replace("\"dv_profile\": 8, \"dv_level\": 6", record));

    [Theory]
    [InlineData("\"dv_profile\": 8, \"dv_bl_signal_compatibility_id\": 1", 8, 1, true)]
    [InlineData("\"dv_profile\": 8, \"dv_bl_signal_compatibility_id\": 2", 8, 2, true)]
    [InlineData("\"dv_profile\": 8, \"dv_bl_signal_compatibility_id\": 4", 8, 4, true)]
    [InlineData("\"dv_profile\": 7, \"dv_bl_signal_compatibility_id\": 6", 7, 6, true)]
    [InlineData("\"dv_profile\": 5, \"dv_bl_signal_compatibility_id\": 0", 5, 0, false)]
    [InlineData("\"dv_profile\": 8", 8, null, true)]
    [InlineData("\"dv_profile\": 5", 5, null, false)]
    [InlineData("\"dv_level\": 6", null, null, false)]
    public void TheDolbyVisionRecord_IsReadAndJudged(string record, int? profile, int? compatibility, bool usable)
    {
        var video = DvProbe(record).Video!;

        Assert.True(video.HasDolbyVision);
        Assert.Equal(profile, video.DolbyVisionProfile);
        Assert.Equal(compatibility, video.DolbyVisionBaseLayerCompatibility);
        Assert.Equal(usable, video.DolbyVisionHasUsableBaseLayer);
    }

    [Fact]
    public void WithoutDolbyVision_TheBaseLayerQuestionDoesNotArise_AndHdr10PlusCanAlwaysBeDropped()
    {
        var plain = FFmpegFixtures.Uhd();
        var hdr10Plus = FFprobeParser.Parse(FFmpegFixtures.UhdHdr10Rip, FFmpegFixtures.Hdr10PlusFrames);

        Assert.False(plain.Video!.HasDolbyVision);
        Assert.True(plain.Video.DolbyVisionHasUsableBaseLayer);
        Assert.True(hdr10Plus.HasDynamicHdrMetadata);
        Assert.True(hdr10Plus.DynamicHdrCanBeDropped);
    }

    [Fact]
    public void EncodeAnyway_IsOffByDefault_OnNoBuiltIn_AndRoundTripsThroughJson()
    {
        Assert.False(new FFmpegProfile().AllowDroppingDynamicHdr);
        Assert.All(new FFmpegProfileStore().GetBuiltIns(), p => Assert.False(p.AllowDroppingDynamicHdr));

        var json = System.Text.Json.JsonSerializer.Serialize(new FFmpegProfile { Name = "x", AllowDroppingDynamicHdr = true });
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<FFmpegProfile>(json)!.AllowDroppingDynamicHdr);
        // A profile saved before the option existed has no such field.
        Assert.False(System.Text.Json.JsonSerializer.Deserialize<FFmpegProfile>("""{ "name": "old" }""")!.AllowDroppingDynamicHdr);
    }

    // ---- Duplicate as ffmpeg --------------------------------------------------------------------

    private static JsonObject BuiltInHandBrake(string name) =>
        (JsonObject)new Compressarr.Core.Presets.HandBrakeProfileStore().GetBuiltIns().Single(p => p.Name == name).Definition.DeepClone();

    [Fact]
    public void Convert_SdHd_CarriesTheRecipe_AndLeavesTheFallbackNone()
    {
        var conversion = HandBrakeToFFmpeg.Convert("Compressarr SD-HD", BuiltInHandBrake("Compressarr SD-HD"));
        var p = conversion.Profile;

        Assert.Equal(("libx265", "yuv420p10le", "main10", "veryfast", 24.0), (p.VideoCodec, p.PixFmt, p.VideoProfile, p.Preset, p.Quality));
        Assert.Equal("mkv", p.Container);
        Assert.Equal(("encode", "eac3", 512, "7.1"), (p.AudioMode, p.AudioCodec, p.AudioBitrateKbps, p.AudioMixdown));
        Assert.Equal(new[] { "eng", "und", "any" }, p.AudioLanguages);
        Assert.Equal(("first", "all"), (p.AudioTracks, p.SubtitleTracks));
        Assert.Equal("auto", p.Crop);
        Assert.Equal("auto", p.Deinterlace);             // HandBrake's decomb
        Assert.Equal("", p.FallbackHandBrakePreset);     // Dolby Vision / HDR10+ files are refused unless the user picks one
        Assert.Contains(conversion.NotCarried, n => n.Contains("Decomb"));
        Assert.NotEmpty(conversion.Carried);
    }

    [Fact]
    public void Convert_UhdAv1_KeepsPassThroughAndTheAv1Settings()
    {
        var p = HandBrakeToFFmpeg.Convert("Compressarr UHD AV1", BuiltInHandBrake("Compressarr UHD AV1")).Profile;

        Assert.Equal(("libsvtav1", "4", "psnr", 30.0), (p.VideoCodec, p.Preset, p.Tune, p.Quality));
        Assert.Equal(("passthru", "all"), (p.AudioMode, p.AudioTracks));
        Assert.Contains("truehd", p.PassthroughCodecs);
        Assert.DoesNotContain(p.PassthroughCodecs, c => c.StartsWith("copy:"));
    }

    [Fact]
    public void Convert_ListsWhatFfmpegCannotExpress()
    {
        var preset = BuiltInHandBrake("Compressarr SD-HD");
        preset["PictureDenoiseFilter"] = "nlmeans";
        preset["PictureForceWidth"] = 1280;
        preset["SubtitleBurnBehavior"] = "foreign";
        preset["VideoOptionExtra"] = "aq-mode=3";
        preset["VideoFramerateMode"] = "pfr";
        preset["AudioList"]!.AsArray().Add(new JsonObject { ["AudioEncoder"] = "ac3" });
        preset["PictureDeinterlaceFilter"] = "bwdif";
        preset["VideoEncoder"] = "VP9";

        var lost = HandBrakeToFFmpeg.Convert("X", preset).NotCarried;

        Assert.Contains(lost, n => n.Contains("Denoise"));
        Assert.Contains(lost, n => n.Contains("Resizing"));
        Assert.Contains(lost, n => n.Contains("burn-in"));
        Assert.Contains(lost, n => n.Contains("aq-mode=3"));
        Assert.Contains(lost, n => n.Contains("Peak frame rate"));
        Assert.Contains(lost, n => n.Contains("first audio output"));
        Assert.Contains(lost, n => n.Contains("'VP9'"));
    }

    [Fact]
    public void Convert_MapsNvencPresetsAndTenBitFormats()
    {
        var preset = BuiltInHandBrake("Compressarr SD-HD");
        preset["VideoEncoder"] = "nvenc_h265_10bit";
        preset["VideoPreset"] = "slow";

        var p = HandBrakeToFFmpeg.Convert("N", preset).Profile;

        Assert.Equal(("hevc_nvenc", "p5", "p010le"), (p.VideoCodec, p.Preset, p.PixFmt));
    }

    [Fact]
    public void Convert_TheResultPassesTheFfmpegValidator_AndMakesACommand()
    {
        foreach (var name in new[] { "Compressarr SD-HD", "Compressarr UHD AV1" })
        {
            var p = HandBrakeToFFmpeg.Convert(name, BuiltInHandBrake(name)).Profile;

            Assert.Empty(FFmpegProfileValidator.Validate(p));
            Assert.NotNull(FFmpegPlanner.Plan(p, FFmpegFixtures.Uhd()));
        }
    }

    // ---- ffmpeg profile validation --------------------------------------------------------------

    [Fact]
    public void Validator_AcceptsABuiltIn_AndFlagsBrokenFields()
    {
        var good = new FFmpegProfileStore().Find("Compressarr SD-HD")!;
        Assert.Empty(FFmpegProfileValidator.Validate(good));

        var bad = good.Clone();
        bad.Name = "a/b";
        bad.Container = "avi";
        bad.VideoCodec = "libx266";
        bad.Quality = 99;
        bad.FramerateMode = "cfr"; bad.Framerate = 0;
        bad.AudioLanguages = new();
        bad.AudioMode = "passthru"; bad.PassthroughCodecs = new();
        bad.AudioCodec = "wma";
        bad.AudioBitrateKbps = 9999;
        bad.SubtitleTracks = "all"; bad.SubtitleLanguages = new();

        var fields = FFmpegProfileValidator.Validate(bad).Select(i => i.Field).ToList();

        foreach (var field in new[] { "name", "container", "videoCodec", "quality", "framerate", "audioLanguages", "passthrough", "audioCodec", "audioBitrate", "subtitleLanguages" })
        {
            Assert.Contains(field, fields);
        }
    }
}
