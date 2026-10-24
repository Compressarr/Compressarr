using Compressarr.Core.Conversion;

namespace Compressarr.Core.Tests.Conversion;

public class HandBrakeProcessRunnerTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("compressarr-hb-tests-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string WriteFile(string name, string? content)
    {
        var path = Path.Combine(_tempDir, name);
        if (content is null)
        {
            // Represents "file does not exist" - caller just gets the path back unwritten.
        }
        else
        {
            File.WriteAllText(path, content);
        }
        return path;
    }

    // ---- The average speed ---------------------------------------------------------------------

    [Theory]
    [InlineData("Encoding: task 1 of 1, 42.10 % (23.45 fps, avg 20.12 fps, ETA 00h05m32s)", 20.12)]
    [InlineData("Encoding: task 1 of 2, 99.99 % (30.00 fps, avg 28.5 fps, ETA 00h00m01s)", 28.5)]
    [InlineData("Encoding: task 1 of 1, 0.00 % (0.00 fps, avg 0.00 fps, ETA 99h59m59s)", null)]      // nothing measured yet is not a speed
    [InlineData("Encoding: task 1 of 1, 12.00 %", null)]                                              // a line with no speed in it
    [InlineData("Finished work at Sat Jan  1 00:00:00 2026", null)]
    public void TryParseAverageFps_ReadsTheAvgFigure_AndNothingElse(string line, double? expected)
    {
        Assert.Equal(expected, HandBrakeProgressParser.TryParseAverageFps(line));
    }

    [Fact]
    public void TheLiveProgressReading_IsUnchangedByTheAverage()
    {
        var reading = HandBrakeProgressParser.TryParse("Encoding: task 1 of 1, 42.10 % (23.45 fps, avg 20.12 fps, ETA 00h05m32s)");

        Assert.Equal(new EncodeProgress(42.10, 23.45, "00h05m32s"), reading);
    }

    // ---- The HandBrakeCLI command line (golden) ------------------------------------------------
    // The command line is the one thing the encoder abstraction must never change by accident: every
    // user's existing presets and Extra CLI options depend on it being exactly this.

    private static EncodeRequest Request(string? extraOptions = null) => new(
        ToolPath: @"C:\Program Files\HandBrake\HandBrakeCLI.exe",
        SourcePath: @"D:\Media\Media Landing\Show - S01E01.mkv",
        OutputPath: @"D:\Media\Temp Processing\Show - S01E01.compressarr-1a2b3c4d.mkv",
        PresetSource: @"C:\Users\me\AppData\Roaming\HandBrake\presets.json",
        PresetName: "Compressarr SD-HD",
        ExtraOptions: extraOptions,
        DetailLogFile: @"C:\Logs\detail.txt");

    [Fact]
    public void BuildArguments_NoExtraOptions_IsExactlyTheOriginalCommandLine()
    {
        Assert.Equal(
            new[]
            {
                "-i", @"D:\Media\Media Landing\Show - S01E01.mkv",
                "-t", "1",
                "-o", @"D:\Media\Temp Processing\Show - S01E01.compressarr-1a2b3c4d.mkv",
                "--preset-import-file", @"C:\Users\me\AppData\Roaming\HandBrake\presets.json",
                "--preset", "Compressarr SD-HD"
            },
            HandBrakeProcessRunner.BuildArguments(Request()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildArguments_BlankExtraOptions_AddsNothing(string? extra)
    {
        Assert.Equal(10, HandBrakeProcessRunner.BuildArguments(Request(extra)).Count);
    }

    [Fact]
    public void BuildArguments_ExtraOptions_AreSplitIntoSeparateArgumentsAfterThePreset()
    {
        var args = HandBrakeProcessRunner.BuildArguments(Request("--two-pass --optimize --custom-anamorphic \"16:9 wide\""));

        Assert.Equal(
            new[] { "--preset", "Compressarr SD-HD", "--two-pass", "--optimize", "--custom-anamorphic", "16:9 wide" },
            args.Skip(8).ToArray());
    }

    [Fact]
    public async Task RunAsync_WithAStandInCli_PassesTheGoldenArguments_ReportsProgress_AndSucceeds()
    {
        // A tiny batch file standing in for HandBrakeCLI: records the argument line, prints a real
        // HandBrake progress line to stdout and the completion banner to stderr, and writes the -o file.
        var cli = Path.Combine(_tempDir, "FakeHandBrakeCLI.cmd");
        var argsFile = Path.Combine(_tempDir, "args.txt");
        File.WriteAllText(cli,
            "@echo off\r\n" +
            $"echo %*> \"{argsFile}\"\r\n" +
            "echo Encoding: task 1 of 1, 42.10 %% (23.45 fps, avg 20.12 fps, ETA 00h05m32s)\r\n" +
            "echo Finished work at Sat Jan  1 00:00:00 2026 1>&2\r\n" +
            ":loop\r\n" +
            "if \"%~1\"==\"\" goto done\r\n" +
            "if \"%~1\"==\"-o\" echo encoded> \"%~2\"\r\n" +
            "shift\r\n" +
            "goto loop\r\n" +
            ":done\r\n" +
            "exit /b 0\r\n");
        var output = Path.Combine(_tempDir, "out file.mkv");
        var detail = Path.Combine(_tempDir, "detail.txt");
        var request = new EncodeRequest(cli, Path.Combine(_tempDir, "in file.mkv"), output, Path.Combine(_tempDir, "presets.json"), "My Preset", "--optimize", detail);
        var readings = new List<EncodeProgress>();

        var result = await new HandBrakeProcessRunner(new ActiveEncodeProcess()).RunAsync(request, readings.Add, CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(result.Cancelled);
        Assert.Equal(detail, result.DetailLogFile);
        Assert.Equal(new EncodeProgress(42.10, 23.45, "00h05m32s"), Assert.Single(readings));
        Assert.Equal(20.12, result.AvgFps);               // HandBrake's own running average, kept for the file's record
        var recorded = File.ReadAllText(argsFile).Trim();
        Assert.Equal(
            $"-i \"{request.SourcePath}\" -t 1 -o \"{output}\" --preset-import-file {request.PresetSource} --preset \"My Preset\" --optimize",
            recorded);
    }

    [Fact]
    public async Task RunAsync_CancelledMidEncode_ReturnsCancelledInsteadOfThrowing()
    {
        var cli = Path.Combine(_tempDir, "SlowHandBrakeCLI.cmd");
        File.WriteAllText(cli, "@echo off\r\nping -n 30 127.0.0.1 > nul\r\n");
        var request = new EncodeRequest(cli, "in.mkv", Path.Combine(_tempDir, "out.mkv"), "presets.json", "P", null, Path.Combine(_tempDir, "detail.txt"));
        using var cts = new CancellationTokenSource();

        var run = new HandBrakeProcessRunner(new ActiveEncodeProcess()).RunAsync(request, null, cts.Token);
        await Task.Delay(500);
        cts.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(result.Cancelled);
        Assert.False(result.Success);
    }

    [Fact]
    public void DetermineSuccess_OutputExistsAndNonEmpty_FinishedLinePresent_ExitCodeZero_IsSuccess()
    {
        var output = WriteFile("out.mp4", "some encoded bytes");
        var log = WriteFile("detail.log", "Encoding...\nFinished work at Sat Jan  1 00:00:00 2026\n");

        Assert.True(HandBrakeProcessRunner.DetermineSuccess(output, log, exitCode: 0));
    }

    [Fact]
    public void DetermineSuccess_NonEmptyFile_ButNoFinishedLine_IsFailure()
    {
        // Proves the AND is enforced: a plausible-looking output file alone isn't enough.
        var output = WriteFile("out.mp4", "some encoded bytes");
        var log = WriteFile("detail.log", "Encoding started...\nEncoding aborted.\n");

        Assert.False(HandBrakeProcessRunner.DetermineSuccess(output, log, exitCode: 0));
    }

    [Fact]
    public void DetermineSuccess_FinishedLinePresent_ButOutputIsZeroLength_IsFailure()
    {
        // Proves the AND is enforced the other direction: the completion banner alone isn't enough.
        var output = WriteFile("out.mp4", "");
        var log = WriteFile("detail.log", "Finished work at Sat Jan  1 00:00:00 2026\n");

        Assert.False(HandBrakeProcessRunner.DetermineSuccess(output, log, exitCode: 0));
    }

    [Fact]
    public void DetermineSuccess_OutputFileMissing_IsFailure()
    {
        var output = Path.Combine(_tempDir, "never-written.mp4");
        var log = WriteFile("detail.log", "Finished work at Sat Jan  1 00:00:00 2026\n");

        Assert.False(HandBrakeProcessRunner.DetermineSuccess(output, log, exitCode: 0));
    }

    [Fact]
    public void DetermineSuccess_NonZeroExitCode_IsFailure_EvenWithFinishedLineAndNonEmptyOutput()
    {
        // Confirmed live against a genuinely full disk: HandBrakeCLI still writes "Finished work
        // at" to its own log even when the encode actually failed (mux error, exit code 4,
        // "libhb: work result = 4") - a truncated/corrupt output file with the banner line
        // present is exactly what a disk-full failure looks like. Without checking the exit code,
        // this reports as a success, which would then move the corrupt file into place and
        // potentially delete/recycle the real source out from under it.
        var output = WriteFile("out.mp4", "partial encoded bytes, disk filled up mid-write");
        var log = WriteFile("detail.log",
            "ERROR: avformatMux: track 0, av_interleaved_write_frame failed with error 'No space left on device'\n" +
            "Finished work at Sat Jan  1 00:00:00 2026\n" +
            "libhb: work result = 4\n" +
            "Encode failed (error 4).\n");

        Assert.False(HandBrakeProcessRunner.DetermineSuccess(output, log, exitCode: 4));
    }

    [Fact]
    public void SplitExtraOptions_PlainFlags_SplitsOnWhitespace()
    {
        Assert.Equal(new[] { "--two-pass", "--optimize" }, HandBrakeProcessRunner.SplitExtraOptions("--two-pass --optimize"));
    }

    [Fact]
    public void SplitExtraOptions_QuotedValueWithSpace_StaysOneArgument()
    {
        // Same shell-style behavior a plain Arguments string used to get for free - a quoted
        // segment's own internal space must not split it into two arguments.
        Assert.Equal(new[] { "--custom-anamorphic", "16:9 storage" }, HandBrakeProcessRunner.SplitExtraOptions("--custom-anamorphic \"16:9 storage\""));
    }

    [Fact]
    public void SplitExtraOptions_ExtraWhitespace_IsIgnored()
    {
        Assert.Equal(new[] { "--two-pass" }, HandBrakeProcessRunner.SplitExtraOptions("   --two-pass   "));
    }

    [Fact]
    public void SplitExtraOptions_Empty_ReturnsNoArguments()
    {
        Assert.Empty(HandBrakeProcessRunner.SplitExtraOptions(""));
    }

    // Code-review finding (v2.1.4 review, #5 LOW-MED): SplitExtraOptions isn't a full Windows
    // command-line parser (no backslash-escaping) - an unterminated quote gets silently folded
    // into one final token rather than rejected. HasUnbalancedQuotes surfaces that same "still
    // inside a quote at end of string" condition so SettingsValidator can warn about it on the
    // Settings page, instead of letting it silently mis-tokenize at encode time.

    [Fact]
    public void HasUnbalancedQuotes_BalancedQuotes_ReturnsFalse()
    {
        Assert.False(HandBrakeProcessRunner.HasUnbalancedQuotes("--custom-anamorphic \"16:9\" --two-pass"));
    }

    [Fact]
    public void HasUnbalancedQuotes_NoQuotesAtAll_ReturnsFalse()
    {
        Assert.False(HandBrakeProcessRunner.HasUnbalancedQuotes("--encoder-preset slow --quality 20"));
    }

    [Fact]
    public void HasUnbalancedQuotes_SingleUnterminatedQuote_ReturnsTrue()
    {
        Assert.True(HandBrakeProcessRunner.HasUnbalancedQuotes("--custom-anamorphic \"16:9 --two-pass"));
    }

    [Fact]
    public void HasUnbalancedQuotes_TwoQuotedSegments_ReturnsFalse()
    {
        Assert.False(HandBrakeProcessRunner.HasUnbalancedQuotes("--a \"one\" --b \"two\""));
    }
}
