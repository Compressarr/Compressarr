using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Presets;
using Compressarr.Core.Reporting;

namespace Compressarr.Core.Tests.Conversion;

public class PassthroughPresetTests
{
    [Theory]
    [InlineData("Passthrough (no re-encode)", true)]
    [InlineData("  passthrough (NO RE-ENCODE)  ", true)]
    [InlineData("Compressarr HD Quality v1", false)]
    [InlineData("Passthrough", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheName_IsRecognisedWithoutFussAboutCaseOrSpaces(string? name, bool expected) =>
        Assert.Equal(expected, PassthroughPreset.Is(name));

    [Theory]
    [InlineData("Passthrough (no re-encode)", "Passthrough (no re-encode)", true)]
    [InlineData("Passthrough (no re-encode)", "", true)]          // only one preset set, and it is passthrough
    [InlineData("", "Passthrough (no re-encode)", true)]
    [InlineData("Passthrough (no re-encode)", "Compressarr HD Quality v1", false)]
    [InlineData("Compressarr HD Quality v1", "", false)]
    [InlineData("", "", false)]
    public void ALaneNeedsNoEncoder_OnlyWhenEveryPresetItHasIsPassthrough(string tv, string movie, bool expected) =>
        Assert.Equal(expected, PassthroughPreset.OnlyPassthrough(tv, movie));
}

public class PassthroughRunnerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("compressarr-passthrough-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string MakeSource(int bytes)
    {
        var path = Path.Combine(_dir, "source.mkv");
        var data = new byte[bytes];
        new Random(7).NextBytes(data);
        File.WriteAllBytes(path, data);
        File.SetLastWriteTimeUtc(path, new DateTime(2020, 5, 17, 8, 30, 0, DateTimeKind.Utc));
        return path;
    }

    private EncodeRequest Request(string source, string? output = null) =>
        new("tool", source, output ?? Path.Combine(_dir, "out", "copy.mkv"), "", PassthroughPreset.Name, null, Path.Combine(_dir, "logs", "detail.txt"));

    [Fact]
    public async Task ItCopiesTheFileByteForByte_ReportsProgress_AndKeepsTheModifiedTime()
    {
        var source = MakeSource(5 * 1024 * 1024 + 123);
        var request = Request(source);
        var progress = new List<double>();

        var result = await new PassthroughRunner().RunAsync(request, p => progress.Add(p.Percent), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(await File.ReadAllBytesAsync(source), await File.ReadAllBytesAsync(request.OutputPath));
        Assert.Equal(new DateTime(2020, 5, 17, 8, 30, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(request.OutputPath));
        Assert.Equal(100, progress.Last());
        Assert.Equal(progress.OrderBy(p => p).ToList(), progress);          // never goes backwards
        Assert.True(File.Exists(source));                                    // the source is untouched: whether it goes is a separate setting
    }

    [Fact]
    public async Task AMissingSource_FailsCleanly_AndSaysWhyInTheDetailLog()
    {
        var request = Request(Path.Combine(_dir, "nope.mkv"));

        var result = await new PassthroughRunner().RunAsync(request, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.Cancelled);
        Assert.Contains("Passthrough copy failed", result.FailureMessage);
        Assert.Contains("Passthrough copy failed", await File.ReadAllTextAsync(request.DetailLogFile));
        Assert.False(File.Exists(request.OutputPath));
    }

    [Fact]
    public async Task Aborting_LeavesNoPartialFileBehind_AndSaysItWasCancelled()
    {
        var source = MakeSource(8 * 1024 * 1024);
        var request = Request(source);
        using var cts = new CancellationTokenSource();
        cts.Cancel();                                                       // aborted before the copy gets going: the file was already created by then

        var result = await new PassthroughRunner().RunAsync(request, null, cts.Token);

        Assert.False(result.Success);
        Assert.True(result.Cancelled);
        Assert.False(File.Exists(request.OutputPath));
    }

    [Fact]
    public async Task AFileAlreadyAtTheDestination_IsNeverOverwrittenOrDeleted()
    {
        var source = MakeSource(1024);
        var existing = Path.Combine(_dir, "already-here.mkv");
        await File.WriteAllTextAsync(existing, "someone else's file");

        var result = await new PassthroughRunner().RunAsync(Request(source, existing), null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("someone else's file", await File.ReadAllTextAsync(existing));
    }
}

public class PassthroughValidationAndReportTests
{
    private sealed class NothingExists : IEncoderPresetService
    {
        public IReadOnlyList<string> GetPresetNames() => Array.Empty<string>();
        public bool PresetExists(string presetName) => false;
        public string GetOutputExtension(string presetName, out string? warning) { warning = null; return ".mkv"; }
        public string PreparePresetSource() => "";
    }

    private sealed class SameExpander : IPathExpander
    {
        public string Expand(string value) => value;
        public bool PathExists(string value) => true;
    }

    [Fact]
    public void ALaneMayUsePassthrough_EvenWhenNoProfileByThatNameExists()
    {
        var lane = new LaneConfig { Id = "l", DisplayName = "L", Input = "in", Output = "out", TvPreset = PassthroughPreset.Name, MoviePreset = PassthroughPreset.Name };

        var issues = LaneValidator.Validate(lane, new CompressarrConfig(), new SameExpander(), new NothingExists());

        Assert.DoesNotContain(issues, i => i.Field is "tvPreset" or "moviePreset");

        lane.MoviePreset = "Some Missing Preset";
        Assert.Contains(LaneValidator.Validate(lane, new CompressarrConfig(), new SameExpander(), new NothingExists()), i => i.Field == "moviePreset");
    }

    private static ConversionResult Result(string name, double before, double after, bool passthrough) => new()
    {
        LaneId = "l", FileName = name, FullName = @"C:\" + name, ContentType = "Movie", PresetName = passthrough ? PassthroughPreset.Name : "Compressarr HD Quality v1",
        EncoderLabel = passthrough ? "passthrough" : null, IsPassthrough = passthrough, BeginSizeGb = before, EndSizeGb = after, Success = true,
        StartTime = new DateTime(2026, 10, 14, 9, 0, 0), EndTime = new DateTime(2026, 10, 14, 9, 5, 0)
    };

    private static ReportModel Model(params ConversionResult[] results) => new()
    {
        GeneratedAt = new DateTime(2026, 10, 14, 9, 10, 0), RunTime = TimeSpan.FromMinutes(10), RunNumber = 3,
        Lanes = new[] { new LaneReportSection { LaneDisplayName = "Movies", Results = results } }
    };

    [Fact]
    public void AReportShowsATealPassthroughBadge_NotAPresetName_AndLeavesPassthroughBytesOutOfTheTotals()
    {
        var model = Model(Result("Encoded.mkv", 10.0, 4.0, passthrough: false), Result("Copied.mkv", 8.0, 8.0, passthrough: true));

        var html = new HtmlReportGenerator().Generate(model);

        Assert.Contains("<span class=\"pt-tag\">Passthrough</span>", html);
        Assert.Contains(".pt-tag", html);                                       // the teal style is in the report
        Assert.DoesNotContain(PassthroughPreset.Name, html);                    // the badge replaces the preset name
        Assert.Contains("(1 passed through, not counted in the sizes)", html);
        Assert.Contains("2 file(s)", html);                                     // it still counts as a file
        Assert.Equal(10.0, model.TotalBeforeGb);                                // but not its bytes
        Assert.Equal(4.0, model.TotalAfterGb);
    }
}
