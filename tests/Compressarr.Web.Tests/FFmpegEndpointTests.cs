using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Compressarr.Core.Config;
using Compressarr.Core.FFmpeg;
using Microsoft.Extensions.DependencyInjection;

namespace Compressarr.Web.Tests;

/// <summary>The ffmpeg side of the Encoder, Profiles and Lanes pages through the real HTTP surface:
/// per-lane engine choice, the ffmpeg catalog and its profile editor endpoints, "Duplicate as ffmpeg",
/// "Preview decisions" against a stand-in ffprobe, and the Encoder page's ffmpeg settings.</summary>
public class FFmpegEndpointTests
{
    private static readonly LaneSpec HbLane = new("hb", "HB Lane");
    private static readonly LaneSpec FfLane = new("ff", "FF Lane");

    private static string Url(string name) => "/api/profiles/ffmpeg/" + Uri.EscapeDataString(name);

    private static async Task SetEngineAsync(QueueHost host, string laneId, string engine, string? preset = null)
    {
        var lanes = (await host.Client.GetFromJsonAsync<JsonArray>("/api/lanes"))!;
        var lane = lanes.Select(l => l!.AsObject()).Single(l => l["id"]!.GetValue<string>() == laneId);
        lane["engine"] = engine;
        if (preset is not null) { lane["tvPreset"] = preset; lane["moviePreset"] = preset; }
        (await host.Client.PutAsJsonAsync("/api/lanes/" + laneId, lane)).EnsureSuccessStatusCode();
    }

    private static async Task<JsonObject> GetProfileAsync(QueueHost host, string name) =>
        (await host.Client.GetFromJsonAsync<JsonObject>(Url(name)))!;

    private static async Task<HttpResponseMessage> CreateAsync(QueueHost host, string name, Action<JsonObject>? tweak = null)
    {
        var profile = (JsonObject)(await GetProfileAsync(host, "Compressarr SD-HD"))["profile"]!.DeepClone();
        profile["name"] = name;
        tweak?.Invoke(profile);
        return await host.Client.PostAsJsonAsync("/api/profiles/ffmpeg", profile);
    }

    // ---- lanes ----------------------------------------------------------------------------------

    [Fact]
    public async Task ALaneDefaultsToHandBrake_AndItsEngineCanBeChanged()
    {
        await using var host = await QueueHost.StartAsync(HbLane);

        var before = (await host.Client.GetFromJsonAsync<JsonArray>("/api/lanes"))![0]!;
        Assert.Equal("HandBrake", before["engine"]!.GetValue<string>());

        await SetEngineAsync(host, "hb", "FFmpeg");

        var after = (await host.Client.GetFromJsonAsync<JsonArray>("/api/lanes"))![0]!;
        Assert.Equal("FFmpeg", after["engine"]!.GetValue<string>());
        Assert.Equal(EncoderEngine.FFmpeg, host.Services.GetRequiredService<IConfigStore>().Load(AppPaths.GetConfigFilePath()).Lanes[0].Engine);
    }

    [Fact]
    public async Task AClientThatDoesNotSendTheEngine_LeavesItAlone()
    {
        await using var host = await QueueHost.StartAsync(HbLane);
        await SetEngineAsync(host, "hb", "FFmpeg");
        var lane = (await host.Client.GetFromJsonAsync<JsonArray>("/api/lanes"))![0]!.AsObject();
        lane.Remove("engine");

        (await host.Client.PutAsJsonAsync("/api/lanes/hb", lane)).EnsureSuccessStatusCode();

        Assert.Equal("FFmpeg", (await host.Client.GetFromJsonAsync<JsonArray>("/api/lanes"))![0]!["engine"]!.GetValue<string>());
    }

    [Fact]
    public async Task ANewLane_StartsWithTheDefaultProfileForBothContentTypes_SoANoviceHasAWorkingChoice()
    {
        await using var host = await QueueHost.StartAsync();

        using var added = await host.Client.PostAsync("/api/lanes", null);
        var lane = (await added.Content.ReadFromJsonAsync<JsonObject>())!;

        Assert.Equal("Compressarr SD-HD", lane["tvPreset"]!.GetValue<string>());
        Assert.Equal("Compressarr SD-HD", lane["moviePreset"]!.GetValue<string>());
        Assert.DoesNotContain(lane["validationIssues"]!.AsArray(), i => i!["field"]!.GetValue<string>() is "tvPreset" or "moviePreset");
        Assert.Equal("HandBrake", lane["engine"]!.GetValue<string>());
        Assert.False(lane["allowHdrLoss"]!.GetValue<bool>());

        // "Reset lanes" starts over with the same kind of lane.
        (await host.Client.PostAsync("/api/maintenance/reset-lanes", null)).EnsureSuccessStatusCode();
        var lanes = (await host.Client.GetFromJsonAsync<JsonArray>("/api/lanes"))!;
        Assert.Equal("Compressarr SD-HD", Assert.Single(lanes)!["tvPreset"]!.GetValue<string>());
    }

    [Fact]
    public async Task TheLanesHdrChoice_DefaultsOff_IsSaved_AndAClientThatDoesNotSendItLeavesItAlone()
    {
        await using var host = await QueueHost.StartAsync(HbLane);
        var lane = (await host.Client.GetFromJsonAsync<JsonArray>("/api/lanes"))![0]!.AsObject();
        Assert.False(lane["allowHdrLoss"]!.GetValue<bool>());

        lane["allowHdrLoss"] = true;
        (await host.Client.PutAsJsonAsync("/api/lanes/hb", lane)).EnsureSuccessStatusCode();
        Assert.True((await host.Client.GetFromJsonAsync<JsonArray>("/api/lanes"))![0]!["allowHdrLoss"]!.GetValue<bool>());

        lane.Remove("allowHdrLoss"); // a cached older page
        (await host.Client.PutAsJsonAsync("/api/lanes/hb", lane)).EnsureSuccessStatusCode();
        Assert.True((await host.Client.GetFromJsonAsync<JsonArray>("/api/lanes"))![0]!["allowHdrLoss"]!.GetValue<bool>());
    }

    [Fact]
    public async Task AnFfmpegLane_IsCheckedAgainstTheFfmpegCatalog_AndFlagsAMissingFfmpeg()
    {
        await using var host = await QueueHost.StartAsync(FfLane);
        await SetEngineAsync(host, "ff", "FFmpeg", "Compressarr SD-HD");

        var issues = (await host.Client.GetFromJsonAsync<JsonArray>("/api/lanes"))![0]!["validationIssues"]!.AsArray();
        var fields = issues.Select(i => i!["field"]!.GetValue<string>()).ToList();

        Assert.DoesNotContain("tvPreset", fields);   // the ffmpeg built-in of that name exists
        Assert.Contains("engine", fields);            // but ffmpeg itself isn't installed in the sandbox

        await SetEngineAsync(host, "ff", "FFmpeg", "Cartoons only in HandBrake");
        var again = (await host.Client.GetFromJsonAsync<JsonArray>("/api/lanes"))![0]!["validationIssues"]!.AsArray();
        Assert.Contains(again, i => i!["field"]!.GetValue<string>() == "tvPreset");
    }

    [Fact]
    public async Task Presets_AreListedPerEngine()
    {
        await using var host = await QueueHost.StartAsync(HbLane);

        var hb = (await host.Client.GetFromJsonAsync<string[]>("/api/presets"))!;
        var ff = (await host.Client.GetFromJsonAsync<string[]>("/api/presets?engine=ffmpeg"))!;

        Assert.Equal(new[] { "Compressarr SD-HD", "Compressarr UHD AV1" }, hb);
        Assert.Equal(new[] { "Compressarr SD-HD", "Compressarr UHD AV1", "HEVC NVENC (fast)" }, ff);
    }

    [Fact]
    public async Task TheQueue_NamesEachRowsEngine_AndTheCurrentFileItsOwn()
    {
        await using var host = await QueueHost.StartAsync(HbLane, FfLane);
        await SetEngineAsync(host, "ff", "FFmpeg");
        host.Drop("hb", "a.mkv");
        host.Drop("ff", "b.mkv");

        var status = (await host.Client.GetFromJsonAsync<JsonObject>("/api/run/status"))!;
        var rows = status["upNext"]!.AsArray().Select(r => r!.AsObject()).ToList();

        Assert.Equal("HandBrake", rows.Single(r => r["laneId"]!.GetValue<string>() == "hb")["engine"]!.GetValue<string>());
        Assert.Equal("FFmpeg", rows.Single(r => r["laneId"]!.GetValue<string>() == "ff")["engine"]!.GetValue<string>());
        Assert.Equal("HandBrake", status["engine"]!.GetValue<string>());
    }

    // ---- the Encoder page -----------------------------------------------------------------------

    [Fact]
    public async Task Encoder_CarriesTheFfmpegSettings_AndOnlyFlagsMissingToolsOnceALaneUsesFfmpeg()
    {
        await using var host = await QueueHost.StartAsync(HbLane);

        var dto = (await host.Client.GetFromJsonAsync<JsonObject>("/api/encoder"))!;
        Assert.Contains("ffmpeg", dto["ffmpegPath"]!.GetValue<string>());
        Assert.Equal(3, dto["ffmpegProfileCount"]!.GetValue<int>());
        Assert.DoesNotContain(dto["validationIssues"]!.AsArray(), i => i!["field"]!.GetValue<string>().StartsWith("ffmpeg"));

        await SetEngineAsync(host, "hb", "FFmpeg");
        var withLane = (await host.Client.GetFromJsonAsync<JsonObject>("/api/encoder"))!;
        var fields = withLane["validationIssues"]!.AsArray().Select(i => i!["field"]!.GetValue<string>()).ToList();
        Assert.Contains("ffmpegPath", fields);
        Assert.Contains("ffmpegProbePath", fields);
        Assert.Equal(new[] { "HB Lane" }, withLane["ffmpegLanes"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.DoesNotContain("HB Lane", withLane["handBrakeLanes"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    // The page's JavaScript reads these keys case-sensitively; the tests' own JSON helpers don't, so the raw text is checked
    // (a property named FFmpegPath would be sent as "fFmpegPath").
    [Fact]
    public async Task EncoderAndProfilesJson_UsesTheKeysThePagesRead()
    {
        await using var host = await QueueHost.StartAsync(HbLane);

        var encoder = await host.Client.GetStringAsync("/api/encoder");
        var profiles = await host.Client.GetStringAsync("/api/profiles");

        foreach (var key in new[] { "\"ffmpegPath\"", "\"ffmpegProbePath\"", "\"ffmpegOptions\"", "\"ffmpegProfileCount\"", "\"ffmpegLanes\"" })
        {
            Assert.Contains(key, encoder);
        }
        Assert.Contains("\"ffmpegUserFilePath\"", profiles);
        Assert.DoesNotContain("fFmpeg", encoder + profiles);
    }

    [Fact]
    public async Task Encoder_SavesTheFfmpegSettings_AndAnOlderPageThatOmitsThemLeavesThemAlone()
    {
        await using var host = await QueueHost.StartAsync(HbLane);
        var exe = Path.Combine(host.Root, "ffmpeg.exe");
        var probe = Path.Combine(host.Root, "ffprobe.exe");
        File.WriteAllText(exe, ""); File.WriteAllText(probe, "");

        var dto = (await host.Client.GetFromJsonAsync<JsonObject>("/api/encoder"))!;
        dto["ffmpegPath"] = exe; dto["ffmpegProbePath"] = probe; dto["ffmpegOptions"] = "-threads 6";
        (await host.Client.PutAsJsonAsync("/api/encoder", dto)).EnsureSuccessStatusCode();

        var saved = (await host.Client.GetFromJsonAsync<JsonObject>("/api/encoder"))!;
        Assert.Equal(exe, saved["ffmpegPath"]!.GetValue<string>());
        Assert.Equal("-threads 6", saved["ffmpegOptions"]!.GetValue<string>());

        var old = new JsonObject { ["handBrakeCliPath"] = saved["handBrakeCliPath"]!.GetValue<string>(), ["handBrakeOptions"] = "", ["builtInProfileCount"] = 0, ["userProfileCount"] = 0, ["handBrakeLanes"] = new JsonArray(), ["validationIssues"] = new JsonArray() };
        (await host.Client.PutAsJsonAsync("/api/encoder", old)).EnsureSuccessStatusCode();
        Assert.Equal(exe, (await host.Client.GetFromJsonAsync<JsonObject>("/api/encoder"))!["ffmpegPath"]!.GetValue<string>());

        saved["ffmpegOptions"] = "-x \"broken";
        var bad = (await (await host.Client.PutAsJsonAsync("/api/encoder", saved)).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Contains(bad["validationIssues"]!.AsArray(), i => i!["field"]!.GetValue<string>() == "ffmpegOptions");
    }

    [Fact]
    public async Task FfmpegStatusCapabilitiesAndFind_AnswerEvenWhenFfmpegIsNotThere()
    {
        await using var host = await QueueHost.StartAsync(HbLane);
        var config = host.Services.GetRequiredService<IConfigStore>();
        config.Update(AppPaths.GetConfigFilePath(), c => { c.FFmpeg.Path = Path.Combine(host.Root, "nope.exe"); return true; });

        var status = (await host.Client.GetFromJsonAsync<JsonObject>("/api/ffmpeg/status"))!;
        var caps = (await host.Client.GetFromJsonAsync<JsonObject>("/api/ffmpeg/capabilities"))!;
        var find = (await host.Client.GetFromJsonAsync<JsonObject>("/api/ffmpeg/find"))!;

        Assert.False(status["exists"]!.GetValue<bool>());
        Assert.False(caps["found"]!.GetValue<bool>());
        Assert.NotNull(find["found"]);
    }

    // ---- profiles -------------------------------------------------------------------------------

    [Fact]
    public async Task TheProfilesList_HasBothEncoders_AndUsedByIsPerEngine()
    {
        await using var host = await QueueHost.StartAsync(HbLane, FfLane);
        await SetEngineAsync(host, "hb", "HandBrake", "Compressarr SD-HD");
        await SetEngineAsync(host, "ff", "FFmpeg", "Compressarr SD-HD");

        var rows = ((await host.Client.GetFromJsonAsync<JsonObject>("/api/profiles"))!["profiles"]!.AsArray()).Select(r => r!.AsObject()).ToList();
        var hb = rows.Single(r => r["engine"]!.GetValue<string>() == "handbrake" && r["name"]!.GetValue<string>() == "Compressarr SD-HD");
        var ff = rows.Single(r => r["engine"]!.GetValue<string>() == "ffmpeg" && r["name"]!.GetValue<string>() == "Compressarr SD-HD");

        Assert.Equal(new[] { "HB Lane" }, hb["usedBy"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(new[] { "FF Lane" }, ff["usedBy"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("x265 10-bit, CRF 24, veryfast", ff["video"]!.GetValue<string>());
        Assert.StartsWith("E-AC3 512k", ff["audio"]!.GetValue<string>());
        Assert.Equal(5, rows.Count); // 2 HandBrake + 3 ffmpeg built-ins
    }

    [Fact]
    public async Task CreateEditDuplicateDelete_AnFfmpegProfile()
    {
        await using var host = await QueueHost.StartAsync(HbLane);

        using var created = await CreateAsync(host, "Cartoons x265", p => { p["quality"] = 26; p["preset"] = "slow"; });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Contains("Cartoons x265", (await host.Client.GetFromJsonAsync<string[]>("/api/presets?engine=ffmpeg"))!);
        Assert.DoesNotContain("Cartoons x265", (await host.Client.GetFromJsonAsync<string[]>("/api/presets"))!); // not a HandBrake profile

        var dto = await GetProfileAsync(host, "Cartoons x265");
        Assert.False(dto["builtIn"]!.GetValue<bool>());
        var profile = (JsonObject)dto["profile"]!.DeepClone();
        profile["quality"] = 19;
        using var saved = await host.Client.PutAsJsonAsync(Url("Cartoons x265"), profile);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(19, (await GetProfileAsync(host, "Cartoons x265"))["profile"]!["quality"]!.GetValue<double>());

        var dup = (await (await host.Client.PostAsJsonAsync(Url("Cartoons x265") + "/duplicate", new JsonObject())).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("Cartoons x265 copy", dup["name"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.OK, (await host.Client.DeleteAsync(Url("Cartoons x265 copy"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(Url("Cartoons x265 copy"))).StatusCode);
    }

    [Fact]
    public async Task BuiltIns_AreLocked_AndBadInputIsRejectedWithFieldMessages()
    {
        await using var host = await QueueHost.StartAsync(HbLane);
        var builtIn = (JsonObject)(await GetProfileAsync(host, "Compressarr SD-HD"))["profile"]!.DeepClone();

        Assert.True((await GetProfileAsync(host, "Compressarr SD-HD"))["builtIn"]!.GetValue<bool>());
        using var put = await host.Client.PutAsJsonAsync(Url("Compressarr SD-HD"), builtIn);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.DeleteAsync(Url("Compressarr SD-HD"))).StatusCode);

        using var noName = await CreateAsync(host, " ");
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
        Assert.Equal("name", (await noName.Content.ReadFromJsonAsync<JsonObject>())!["validationIssues"]![0]!["field"]!.GetValue<string>());

        using var clash = await CreateAsync(host, "compressarr sd-hd");
        Assert.Equal(HttpStatusCode.BadRequest, clash.StatusCode);

        using var badQuality = await CreateAsync(host, "Fine", p => p["quality"] = 500);
        Assert.Equal(HttpStatusCode.BadRequest, badQuality.StatusCode);
    }

    [Fact]
    public async Task RenamingAnFfmpegProfile_RepointsOnlyFfmpegLanes_NotAHandBrakeLaneWithTheSameName()
    {
        await using var host = await QueueHost.StartAsync(HbLane, FfLane);
        (await CreateAsync(host, "Shared")).EnsureSuccessStatusCode();
        // a HandBrake profile with the very same name, used by the HandBrake lane
        host.Services.GetRequiredService<Compressarr.Core.Presets.IHandBrakeProfileStore>().AddUserProfiles(new[]
        {
            new JsonObject { ["PresetName"] = "Shared", ["FileFormat"] = "av_mkv", ["Folder"] = false }
        });
        await SetEngineAsync(host, "hb", "HandBrake", "Shared");
        await SetEngineAsync(host, "ff", "FFmpeg", "Shared");

        var profile = (JsonObject)(await GetProfileAsync(host, "Shared"))["profile"]!.DeepClone();
        profile["name"] = "Renamed";
        (await host.Client.PutAsJsonAsync(Url("Shared"), profile)).EnsureSuccessStatusCode();

        var lanes = (await host.Client.GetFromJsonAsync<JsonArray>("/api/lanes"))!.Select(l => l!.AsObject()).ToList();
        Assert.Equal("Renamed", lanes.Single(l => l["id"]!.GetValue<string>() == "ff")["moviePreset"]!.GetValue<string>());
        Assert.Equal("Shared", lanes.Single(l => l["id"]!.GetValue<string>() == "hb")["moviePreset"]!.GetValue<string>());
    }

    [Fact]
    public async Task DeleteIsRefusedWhileAnFfmpegLaneUsesTheProfile_ButNotForAHandBrakeLaneOfTheSameName()
    {
        await using var host = await QueueHost.StartAsync(HbLane, FfLane);
        (await CreateAsync(host, "Shared")).EnsureSuccessStatusCode();
        await SetEngineAsync(host, "hb", "HandBrake", "Shared"); // names a HandBrake profile that doesn't matter here

        Assert.Equal(HttpStatusCode.OK, (await host.Client.DeleteAsync(Url("Shared"))).StatusCode);

        (await CreateAsync(host, "Used")).EnsureSuccessStatusCode();
        await SetEngineAsync(host, "ff", "FFmpeg", "Used");
        using var refused = await host.Client.DeleteAsync(Url("Used"));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("FF Lane", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DuplicateAsFfmpeg_SavesTheClosestProfile_AndSaysWhatItCouldNotCarry()
    {
        await using var host = await QueueHost.StartAsync(HbLane);

        var result = (await (await host.Client.PostAsJsonAsync("/api/profiles/handbrake/Compressarr%20SD-HD/duplicate-as-ffmpeg", new JsonObject())).Content.ReadFromJsonAsync<JsonObject>())!;

        Assert.Equal("Compressarr SD-HD 2", result["name"]!.GetValue<string>()); // the ffmpeg built-in already has that name
        Assert.NotEmpty(result["carried"]!.AsArray());
        Assert.Contains(result["notCarried"]!.AsArray(), n => n!.GetValue<string>().Contains("Decomb"));
        var saved = await GetProfileAsync(host, "Compressarr SD-HD 2");
        Assert.False(saved["builtIn"]!.GetValue<bool>());
        Assert.Equal("libx265", saved["profile"]!["videoCodec"]!.GetValue<string>());
        Assert.Equal("", saved["profile"]!["fallbackHandBrakePreset"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PostAsJsonAsync("/api/profiles/handbrake/Nope/duplicate-as-ffmpeg", new JsonObject())).StatusCode);
    }

    // ---- preview decisions ----------------------------------------------------------------------

    /// <summary>A batch file standing in for ffprobe: prints the stream document for the main call and
    /// the frame document for the HDR10+ call.</summary>
    private static string FakeFfprobe(QueueHost host, string streamsJson, string framesJson)
    {
        var streams = Path.Combine(host.Root, "streams.json");
        var frames = Path.Combine(host.Root, "frames.json");
        File.WriteAllText(streams, streamsJson);
        File.WriteAllText(frames, framesJson);
        var cmd = Path.Combine(host.Root, "FakeFfprobe.cmd");
        File.WriteAllText(cmd,
            "@echo off\r\n" +
            "echo %* | findstr /C:\"-show_frames\" > nul\r\n" +
            $"if %errorlevel%==0 (type \"{frames}\") else (type \"{streams}\")\r\n");
        return cmd;
    }

    private static async Task<JsonObject> PreviewAsync(QueueHost host, JsonObject profile, string path)
    {
        using var response = await host.Client.PostAsJsonAsync("/api/profiles/ffmpeg/preview", new JsonObject { ["profile"] = profile.DeepClone(), ["path"] = path });
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private const string UhdProbe = """
        { "streams": [
            { "index": 0, "codec_name": "hevc", "codec_type": "video", "width": 3840, "height": 2160, "pix_fmt": "yuv420p10le", "field_order": "progressive", "color_transfer": "smpte2084", "color_primaries": "bt2020", "color_space": "bt2020nc", "disposition": { "default": 1 }, "tags": {} },
            { "index": 1, "codec_name": "truehd", "codec_type": "audio", "channels": 8, "disposition": { "default": 1 }, "tags": { "language": "eng" } },
            { "index": 2, "codec_name": "ac3", "codec_type": "audio", "channels": 6, "disposition": {}, "tags": { "language": "fra" } },
            { "index": 3, "codec_name": "hdmv_pgs_subtitle", "codec_type": "subtitle", "disposition": {}, "tags": { "language": "eng" } } ],
          "chapters": [ { "id": 1 } ], "format": { "duration": "7200.5" } }
        """;

    [Fact]
    public async Task PreviewDecisions_ExplainsWhatEachTrackGetsAndWhy_AndShowsTheCommand()
    {
        await using var host = await QueueHost.StartAsync(HbLane);
        var probe = FakeFfprobe(host, UhdProbe, """{ "frames": [ {} ] }""");
        host.Services.GetRequiredService<IConfigStore>().Update(AppPaths.GetConfigFilePath(), c => { c.FFmpeg.ProbePath = probe; return true; });
        var movie = host.Drop("hb", "Some Movie (2020).mkv");
        var profile = (JsonObject)(await GetProfileAsync(host, "Compressarr SD-HD"))["profile"]!.DeepClone();
        profile["crop"] = "off";

        var preview = await PreviewAsync(host, profile, movie);

        Assert.True(preview["ok"]!.GetValue<bool>(), preview.ToJsonString());
        Assert.Equal("3840x2160", preview["video"]!["size"]!.GetValue<string>());
        Assert.True(preview["video"]!["hdr"]!.GetValue<bool>());
        Assert.Null(preview["video"]!["dynamicHdr"]);
        Assert.Equal("2:00:00", preview["duration"]!.GetValue<string>());
        var audio = Assert.Single(preview["audio"]!.AsArray());
        Assert.Equal("encode", audio!["action"]!.GetValue<string>());
        Assert.Contains("always encodes audio", audio["reason"]!.GetValue<string>());
        Assert.Contains(preview["dropped"]!.AsArray(), d => d!["index"]!.GetValue<int>() == 2 && d["reason"]!.GetValue<string>().Contains("first matching"));
        Assert.Equal("copy", preview["subtitles"]![0]!["action"]!.GetValue<string>());
        Assert.False(preview["deinterlace"]!["apply"]!.GetValue<bool>());
        Assert.Contains("-c:v libx265", preview["command"]!.GetValue<string>());
        Assert.Contains("<output file>", preview["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task PreviewDecisions_FlagsDolbyVision_AndSaysWhetherAFallbackExists()
    {
        await using var host = await QueueHost.StartAsync(HbLane);
        var dv = UhdProbe.Replace("\"tags\": {} },", "\"tags\": {}, \"side_data_list\": [ { \"side_data_type\": \"DOVI configuration record\" } ] },");
        var probe = FakeFfprobe(host, dv, """{ "frames": [] }""");
        host.Services.GetRequiredService<IConfigStore>().Update(AppPaths.GetConfigFilePath(), c => { c.FFmpeg.ProbePath = probe; return true; });
        var movie = host.Drop("hb", "Some Movie (2020).mkv");
        var profile = (JsonObject)(await GetProfileAsync(host, "Compressarr SD-HD"))["profile"]!.DeepClone();
        profile["crop"] = "off";

        var without = await PreviewAsync(host, profile, movie); // the built-in has no fallback
        profile["fallbackHandBrakePreset"] = "Compressarr SD-HD";
        var withFallback = await PreviewAsync(host, profile, movie);

        Assert.Equal("Dolby Vision", withFallback["video"]!["dynamicHdr"]!.GetValue<string>());
        Assert.True(withFallback["video"]!["hasFallback"]!.GetValue<bool>());
        Assert.False(without["video"]!["hasFallback"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(8, 1, true)]    // an HDR10 base layer: can be dropped
    [InlineData(5, 0, false)]   // profile 5: no usable base layer, still refused
    public async Task PreviewDecisions_SaysWhetherEncodeAnywayWouldDropTheDolbyVision_OrStillRefuse(int dvProfile, int compatibility, bool canDrop)
    {
        await using var host = await QueueHost.StartAsync(HbLane);
        var dv = UhdProbe.Replace("\"tags\": {} },", $"\"tags\": {{}}, \"side_data_list\": [ {{ \"side_data_type\": \"DOVI configuration record\", \"dv_profile\": {dvProfile}, \"dv_bl_signal_compatibility_id\": {compatibility} }} ] }},");
        var probe = FakeFfprobe(host, dv, """{ "frames": [] }""");
        host.Services.GetRequiredService<IConfigStore>().Update(AppPaths.GetConfigFilePath(), c => { c.FFmpeg.ProbePath = probe; return true; });
        var movie = host.Drop("hb", "Some Movie (2020).mkv");
        var profile = (JsonObject)(await GetProfileAsync(host, "Compressarr SD-HD"))["profile"]!.DeepClone();
        profile["crop"] = "off";

        var refuses = await PreviewAsync(host, profile, movie);
        profile["allowDroppingDynamicHdr"] = true;
        var encodeAnyway = await PreviewAsync(host, profile, movie);

        Assert.False(refuses["video"]!["dropsDynamicHdr"]!.GetValue<bool>());
        Assert.True(encodeAnyway["video"]!["dropsDynamicHdr"]!.GetValue<bool>());
        Assert.Equal(canDrop, encodeAnyway["video"]!["canDropDynamicHdr"]!.GetValue<bool>());
        Assert.Equal($"profile {dvProfile}", encodeAnyway["video"]!["dolbyVisionProfile"]!.GetValue<string>());
    }

    [Fact]
    public async Task PreviewDecisions_ReportsProblemsPlainly()
    {
        await using var host = await QueueHost.StartAsync(HbLane);
        var profile = (JsonObject)(await GetProfileAsync(host, "Compressarr SD-HD"))["profile"]!.DeepClone();

        var missingFile = await PreviewAsync(host, profile, Path.Combine(host.Root, "nope.mkv"));
        Assert.False(missingFile["ok"]!.GetValue<bool>());
        Assert.Contains("doesn't exist", missingFile["error"]!.GetValue<string>());

        var movie = host.Drop("hb", "Some Movie (2020).mkv");
        host.Services.GetRequiredService<IConfigStore>().Update(AppPaths.GetConfigFilePath(), c => { c.FFmpeg.ProbePath = Path.Combine(host.Root, "no-ffprobe.exe"); return true; });
        var noProbe = await PreviewAsync(host, profile, movie);
        Assert.False(noProbe["ok"]!.GetValue<bool>());
        Assert.Contains("ffprobe was not found", noProbe["error"]!.GetValue<string>());
    }

    // ---- export / import ------------------------------------------------------------------------

    [Fact]
    public async Task ExportCarriesYourFfmpegProfiles_AndImportBringsThemBack()
    {
        string export;
        await using (var first = await QueueHost.StartAsync(HbLane))
        {
            (await CreateAsync(first, "Cartoons x265")).EnsureSuccessStatusCode();
            export = await first.Client.GetStringAsync("/api/settings/export");
        }

        Assert.Equal("Cartoons x265", JsonNode.Parse(export)!["ffmpegUserProfiles"]![0]!["name"]!.GetValue<string>());

        await using var second = await QueueHost.StartAsync(HbLane);
        Assert.DoesNotContain("Cartoons x265", (await second.Client.GetFromJsonAsync<string[]>("/api/presets?engine=ffmpeg"))!);

        using var imported = await second.Client.PostAsync("/api/settings/import", new StringContent(export, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        Assert.Contains("Cartoons x265", (await second.Client.GetFromJsonAsync<string[]>("/api/presets?engine=ffmpeg"))!);
    }

    [Theory]
    [InlineData("/api/ffmpeg/status")]
    [InlineData("/api/ffmpeg/find")]
    [InlineData("/api/ffmpeg/capabilities")]
    [InlineData("/api/profiles/ffmpeg/Compressarr%20SD-HD")]
    public async Task NewGetEndpoints_ReturnValidJson(string url)
    {
        await using var host = await QueueHost.StartAsync(HbLane);

        using var response = await host.Client.GetAsync(url);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{url} returned {(int)response.StatusCode}");
        JsonNode.Parse(await response.Content.ReadAsStringAsync());
    }
}
