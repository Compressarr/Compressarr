using Compressarr.Core.Conversion;
using Compressarr.Core.FFmpeg;

namespace Compressarr.Core.Tests.FFmpeg;

internal sealed class FakeFFprobe : IMediaProbe
{
    public Dictionary<string, MediaProbeResult?> ByPath { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Probe, string File)> Calls { get; } = new();

    public Task<MediaProbeResult?> ProbeAsync(string probePath, string filePath, CancellationToken cancellationToken)
    {
        Calls.Add((probePath, filePath));
        return Task.FromResult(ByPath.TryGetValue(filePath, out var r) ? r : null);
    }
}

/// <summary>The ffmpeg runner against a stand-in ffmpeg (a batch file) and a fake ffprobe: the real
/// process plumbing, progress, cancellation, and - the part HandBrake can't offer - the check that
/// the finished file is as long as the source.</summary>
public class FFmpegRunnerTests : Presets.AppDataTestBase, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("compressarr-ff-tests-").FullName;
    private readonly FakeFFprobe _probe = new();
    private readonly string _args;
    private readonly string _ffmpeg;
    private readonly string _source;
    private readonly string _output;
    private readonly string _detail;

    public FFmpegRunnerTests()
    {
        _args = Path.Combine(_dir, "args.txt");
        _ffmpeg = Path.Combine(_dir, "FakeFFmpeg.cmd");
        _source = Path.Combine(_dir, "in file.mkv");
        _output = Path.Combine(_dir, "out file.mkv");
        _detail = Path.Combine(_dir, "detail.txt");
    }

    public new void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        base.Dispose();
    }

    /// <summary>A stand-in ffmpeg: appends its argument line to a file, prints a real "-progress" block
    /// (halfway through) to stdout and a cropdetect line to stderr, and writes the last argument as the output.</summary>
    private void WriteFakeFfmpeg(int exitCode = 0, bool writeOutput = true)
    {
        File.WriteAllText(_ffmpeg,
            "@echo off\r\n" +
            $"echo %*>> \"{_args}\"\r\n" +
            "echo frame=1500\r\n" +
            "echo fps=30.0\r\n" +
            "echo out_time_us=300000000\r\n" +
            "echo speed=2.0x\r\n" +
            "echo progress=continue\r\n" +
            "echo [Parsed_cropdetect_0 @ 1] crop=1920:800:0:140 1>&2\r\n" +
            "set \"LAST=\"\r\n" +
            ":loop\r\n" +
            "if \"%~1\"==\"\" goto done\r\n" +
            "set \"LAST=%~1\"\r\n" +
            "shift\r\n" +
            "goto loop\r\n" +
            ":done\r\n" +
            (writeOutput ? "if not \"%LAST%\"==\"-\" if not \"%LAST%\"==\"\" echo encoded> \"%LAST%\"\r\n" : "") +
            $"exit /b {exitCode}\r\n");
    }

    private static MediaProbeResult Probe(double? seconds, bool hd = true)
    {
        var uhd = FFmpegFixtures.Uhd();
        return new MediaProbeResult { DurationSeconds = seconds, Streams = uhd.Streams, ChapterCount = uhd.ChapterCount };
    }

    private FFmpegRunner Runner(FFmpegProfileStore? store = null) => new(new ActiveEncodeProcess(), store ?? new FFmpegProfileStore(), _probe);

    private EncodeRequest Request(string preset = "Compressarr SD-HD", string? extra = null) =>
        new(_ffmpeg, _source, _output, "", preset, extra, _detail, ProbePath: @"C:\tools\ffprobe.exe");

    private static FFmpegProfileStore StoreWithNoCrop()
    {
        var store = new FFmpegProfileStore();
        var profile = store.GetBuiltIns().Single(p => p.Name == "Compressarr SD-HD");
        profile.Name = "No crop";
        profile.Crop = "off";
        store.AddUserProfiles(new[] { profile });
        return store;
    }

    [Fact]
    public async Task ASuccessfulEncode_PassesTheBuiltCommand_ReportsProgress_AndVerifiesTheLength()
    {
        WriteFakeFfmpeg();
        _probe.ByPath[_source] = Probe(600);
        _probe.ByPath[_output] = Probe(600.4);
        var readings = new List<EncodeProgress>();

        var result = await Runner(StoreWithNoCrop()).RunAsync(Request("No crop", "-threads 6"), readings.Add, CancellationToken.None);

        Assert.True(result.Success, result.FailureMessage);
        Assert.Equal(EncodeFailureKind.None, result.Failure);
        Assert.Equal(_detail, result.DetailLogFile);

        var reading = Assert.Single(readings);
        Assert.Equal(50.0, reading.Percent, 3);   // 300 s of 600 s
        Assert.Equal(30.0, reading.Fps);
        Assert.Equal("00h02m30s", reading.Eta);    // 300 s left at 2x

        var line = File.ReadAllLines(_args).Single();
        Assert.StartsWith($"-hide_banner -nostdin -y -progress pipe:1 -nostats -i \"{_source}\" -map 0:0 -map 0:1", line);
        Assert.Contains("-c:v libx265", line);
        Assert.EndsWith($"-threads 6 \"{_output}\"", line);
        Assert.True(File.Exists(_output));

        var log = File.ReadAllText(_detail);
        Assert.Contains("ffmpeg profile: No crop", log);
        Assert.Contains("Length verified", log);
        Assert.Contains("Command:", log);
        Assert.Contains("Audio stream 1", log);
        Assert.Equal(@"C:\tools\ffprobe.exe", _probe.Calls.First().Probe);
    }

    [Fact]
    public async Task ATruncatedOutput_IsCaughtByTheLengthCheck_EvenThoughFfmpegExitedCleanly()
    {
        WriteFakeFfmpeg(exitCode: 0);
        _probe.ByPath[_source] = Probe(707);       // 11:47
        _probe.ByPath[_output] = Probe(192);       // 3:12

        var result = await Runner(StoreWithNoCrop()).RunAsync(Request("No crop"), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(EncodeFailureKind.LengthMismatch, result.Failure);
        Assert.Contains("3:12", result.FailureMessage);
        Assert.Contains("11:47", result.FailureMessage);
        Assert.Contains("original was left alone", File.ReadAllText(_detail));
    }

    [Theory]
    [InlineData(600, 602.5, true)]   // within 3 s
    [InlineData(600, 597.0, true)]
    [InlineData(600, 590.0, false)]  // 10 s short
    [InlineData(7200, 7260, true)]   // 1% of a long film is 72 s
    [InlineData(7200, 7000, false)]
    [InlineData(30, 10, false)]
    public void LengthsMatch_UsesTheLargerOfThreeSecondsAndOnePercent(double source, double output, bool expected)
    {
        Assert.Equal(expected, FFmpegRunner.LengthsMatch(source, output));
    }

    [Fact]
    public async Task ANonZeroExit_IsAFailure_WithTheCodeInTheLog()
    {
        WriteFakeFfmpeg(exitCode: 1, writeOutput: false);
        _probe.ByPath[_source] = Probe(600);

        var result = await Runner(StoreWithNoCrop()).RunAsync(Request("No crop"), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(EncodeFailureKind.None, result.Failure);
        Assert.Contains("exited with code 1", File.ReadAllText(_detail));
    }

    [Fact]
    public async Task NoOutputFile_IsAFailure()
    {
        WriteFakeFfmpeg(exitCode: 0, writeOutput: false);
        _probe.ByPath[_source] = Probe(600);

        var result = await Runner(StoreWithNoCrop()).RunAsync(Request("No crop"), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("wrote no output", File.ReadAllText(_detail));
    }

    [Fact]
    public async Task AnOutputFfprobeCannotRead_IsNotTrusted()
    {
        WriteFakeFfmpeg();
        _probe.ByPath[_source] = Probe(600);
        _probe.ByPath[_output] = null;

        var result = await Runner(StoreWithNoCrop()).RunAsync(Request("No crop"), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("could not be read back", File.ReadAllText(_detail));
    }

    [Fact]
    public async Task ASourceWithNoKnownLength_StillSucceeds_WithoutALengthCheck()
    {
        WriteFakeFfmpeg();
        _probe.ByPath[_source] = Probe(null);
        _probe.ByPath[_output] = Probe(null);
        var readings = new List<EncodeProgress>();

        var result = await Runner(StoreWithNoCrop()).RunAsync(Request("No crop"), readings.Add, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(readings); // nothing to measure progress against
        Assert.DoesNotContain("Length verified", File.ReadAllText(_detail));
    }

    [Fact]
    public async Task AMissingProfile_NeverStartsFfmpeg()
    {
        WriteFakeFfmpeg();

        var result = await Runner().RunAsync(Request("Nope"), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(EncodeFailureKind.NotStarted, result.Failure);
        Assert.Contains("'Nope' was not found", result.FailureMessage);
        Assert.False(File.Exists(_args));
    }

    [Fact]
    public async Task AFileFfprobeCannotRead_WhenFfprobeIsPresent_BlamesTheFileNotTheTool()
    {
        WriteFakeFfmpeg();
        var probe = Path.Combine(_dir, "ffprobe.exe");
        File.WriteAllText(probe, "");

        var request = Request("No crop") with { ProbePath = probe };
        var result = await Runner(StoreWithNoCrop()).RunAsync(request, null, CancellationToken.None);

        Assert.Equal(EncodeFailureKind.NotStarted, result.Failure);
        Assert.Contains("may be damaged or not a video", result.FailureMessage);
        Assert.DoesNotContain("is ffprobe at", result.FailureMessage);
    }

    [Fact]
    public async Task AFileFfprobeCannotRead_NeverStartsFfmpeg()
    {
        WriteFakeFfmpeg();

        var result = await Runner(StoreWithNoCrop()).RunAsync(Request("No crop"), null, CancellationToken.None);

        Assert.Equal(EncodeFailureKind.NotStarted, result.Failure);
        Assert.Contains("ffprobe could not read", result.FailureMessage);
        Assert.False(File.Exists(_args));
    }

    [Fact]
    public async Task AFileWithNoVideo_NeverStartsFfmpeg()
    {
        WriteFakeFfmpeg();
        _probe.ByPath[_source] = new MediaProbeResult { DurationSeconds = 60, Streams = new[] { new MediaStream { Index = 0, Kind = MediaStreamKind.Audio, Codec = "aac" } } };

        var result = await Runner(StoreWithNoCrop()).RunAsync(Request("No crop"), null, CancellationToken.None);

        Assert.Equal(EncodeFailureKind.NotStarted, result.Failure);
        Assert.Contains("no video stream", result.FailureMessage);
    }

    [Fact]
    public async Task AMissingFfmpeg_IsReportedNotThrown()
    {
        _probe.ByPath[_source] = Probe(600);
        var request = Request("No crop") with { ToolPath = Path.Combine(_dir, "no-such-ffmpeg.exe") };

        var result = await Runner(StoreWithNoCrop()).RunAsync(request, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(EncodeFailureKind.NotStarted, result.Failure);
    }

    [Fact]
    public async Task AutoCrop_SamplesTheFile_AndAddsTheCropFilter()
    {
        WriteFakeFfmpeg();
        _probe.ByPath[_source] = Probe(7200);
        _probe.ByPath[_output] = Probe(7200);

        var result = await Runner().RunAsync(Request(), null, CancellationToken.None);

        Assert.True(result.Success, result.FailureMessage);
        var lines = File.ReadAllLines(_args);
        Assert.Equal(4, lines.Length);                               // three samples + the encode
        Assert.All(lines.Take(3), l => Assert.Contains("cropdetect=limit=24:round=2:reset=0", l));
        Assert.Contains("-vf crop=1920:800:0:140", lines[3]);
        Assert.Contains("cropping to 1920x800", File.ReadAllText(_detail));
    }

    [Fact]
    public async Task Cancelling_KillsFfmpeg_AndReturnsCancelled()
    {
        File.WriteAllText(_ffmpeg, "@echo off\r\nping -n 30 127.0.0.1 > nul\r\n");
        _probe.ByPath[_source] = Probe(600);
        using var cts = new CancellationTokenSource();

        var run = Runner(StoreWithNoCrop()).RunAsync(Request("No crop"), null, cts.Token);
        await Task.Delay(1000);
        cts.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(result.Cancelled);
        Assert.False(result.Success);
    }
}
