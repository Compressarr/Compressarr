using Compressarr.Core.Dependencies;
using Compressarr.Core.Config;
using Compressarr.Core.FFmpeg;

namespace Compressarr.Core.Tests.FFmpeg;

public class FFmpegUpdateCheckTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("compressarr-ffupdate-tests-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static FFmpegReleaseInfo Release(string name, string? sha) =>
        new(name, "ffmpeg-master-latest-win64-gpl.zip", "https://example.invalid/ffmpeg.zip", 1, "https://example.invalid/release", sha);

    private static FFmpegInstallMarker Marker(string name, string? sha, string asset = "ffmpeg-master-latest-win64-gpl.zip") => new(name, asset, sha, DateTime.UtcNow);

    private const FFmpegChannel Latest = FFmpegChannel.Latest;

    [Fact]
    public void NoWorkingFfmpeg_IsNotInstalled()
    {
        Assert.Equal(FFmpegUpdateStatus.NotInstalled, FFmpegUpdateCheck.Compare(false, null, Release("b", "AA"), Latest));
    }

    [Fact]
    public void AnFfmpegCompressarrDidNotInstall_CannotBeCompared()
    {
        Assert.Equal(FFmpegUpdateStatus.Unknown, FFmpegUpdateCheck.Compare(true, null, Release("b", "AA"), Latest));
    }

    [Fact]
    public void TheRecordedBuild_IsComparedByItsPublishedChecksum_IgnoringCase()
    {
        Assert.Equal(FFmpegUpdateStatus.UpToDate, FFmpegUpdateCheck.Compare(true, Marker("Latest Auto-Build (1)", "abc123"), Release("Latest Auto-Build (1)", "ABC123"), Latest));
        Assert.Equal(FFmpegUpdateStatus.Newer, FFmpegUpdateCheck.Compare(true, Marker("Latest Auto-Build (1)", "abc123"), Release("Latest Auto-Build (2)", "def456"), Latest));
    }

    [Fact]
    public void WithoutAChecksum_TheReleaseNameDecides()
    {
        Assert.Equal(FFmpegUpdateStatus.UpToDate, FFmpegUpdateCheck.Compare(true, Marker("Build 7", null), Release("Build 7", null), Latest));
        Assert.Equal(FFmpegUpdateStatus.Newer, FFmpegUpdateCheck.Compare(true, Marker("Build 7", null), Release("Build 8", null), Latest));
    }

    [Fact]
    public void AnInstallFromTheOtherChannel_IsReportedAsThat_NotAsNewerOrUpToDate()
    {
        var master = Marker("Latest Auto-Build (1)", "abc");
        var stable = Marker("Latest Auto-Build (1)", "abc", "ffmpeg-n9.0-latest-win64-gpl-9.0.zip");

        // Even a byte-identical checksum can't make a Stable selection "up to date" with a master install.
        Assert.Equal(FFmpegUpdateStatus.OtherChannel, FFmpegUpdateCheck.Compare(true, master, Release("Latest Auto-Build (1)", "abc"), FFmpegChannel.Stable));
        Assert.Equal(FFmpegUpdateStatus.OtherChannel, FFmpegUpdateCheck.Compare(true, stable, Release("Latest Auto-Build (1)", "abc"), FFmpegChannel.Latest));
        Assert.Equal(FFmpegUpdateStatus.UpToDate, FFmpegUpdateCheck.Compare(true, stable, Release("Latest Auto-Build (1)", "abc"), FFmpegChannel.Stable));
    }

    [Fact]
    public void AnInstallFromBeta4Or5_WhichRecordedOnlyTheAssetName_IsALatestInstall()
    {
        Assert.Equal(FFmpegChannel.Latest, FFmpegUpdateCheck.ChannelOf("ffmpeg-master-latest-win64-gpl.zip"));
        Assert.Equal(FFmpegChannel.Latest, FFmpegUpdateCheck.ChannelOf("ffmpeg-master-latest-winarm64-gpl.zip"));
        Assert.Equal(FFmpegChannel.Stable, FFmpegUpdateCheck.ChannelOf("ffmpeg-n9.0-latest-win64-gpl-9.0.zip"));
    }

    [Fact]
    public void TheMarker_IsWrittenBesideFfmpeg_AndReadBackFromThere()
    {
        FFmpegInstallMarkerFile.Write(_dir, Release("Latest Auto-Build (3)", "abc"));

        var marker = FFmpegInstallMarkerFile.Read(Path.Combine(_dir, "ffmpeg.exe"));

        Assert.NotNull(marker);
        Assert.Equal("Latest Auto-Build (3)", marker!.Name);
        Assert.Equal("abc", marker.Sha256);
    }

    [Fact]
    public void AMissingOrDamagedMarker_IsSimplyNone()
    {
        Assert.Null(FFmpegInstallMarkerFile.Read(Path.Combine(_dir, "ffmpeg.exe")));
        Assert.Null(FFmpegInstallMarkerFile.Read(null));

        File.WriteAllText(Path.Combine(_dir, FFmpegInstallMarkerFile.FileName), "{ not json");
        Assert.Null(FFmpegInstallMarkerFile.Read(Path.Combine(_dir, "ffmpeg.exe")));
    }

    [Fact]
    public void HandBrakeInstall_LeavesALicenseNoteBesideTheProgram()
    {
        HandBrakeInstaller.WriteLicenseNotice(_dir);

        var note = File.ReadAllText(Path.Combine(_dir, "LICENSE-NOTICE.txt"));
        Assert.Contains("GNU General Public License", note);
        Assert.Contains("https://github.com/HandBrake/HandBrake", note);
    }
}
