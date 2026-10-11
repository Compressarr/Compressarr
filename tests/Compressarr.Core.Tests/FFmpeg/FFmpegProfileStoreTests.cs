using Compressarr.Core.Config;
using Compressarr.Core.FFmpeg;

namespace Compressarr.Core.Tests.FFmpeg;

public class FFmpegProfileStoreTests : Presets.AppDataTestBase
{
    private static FFmpegProfile Profile(string name, double quality = 26) => new() { Name = name, Quality = quality, SubtitleLanguages = new() { "eng" } };

    [Fact]
    public void BuiltIns_AreTheThreeLockedProfiles()
    {
        var all = new FFmpegProfileStore().GetAll();

        Assert.Equal(new[] { "Compressarr SD-HD", "Compressarr UHD AV1", "HEVC NVENC (fast)" }, all.Select(p => p.Name).ToArray());
        Assert.All(all, p => Assert.True(p.IsBuiltIn));
        // No built-in hands a Dolby Vision / HDR10+ file to a HandBrake profile on its own: such a file is refused and
        // reported, so it is never re-encoded (and the original replaced) with a profile the user didn't choose.
        Assert.All(all, p => Assert.True(string.IsNullOrWhiteSpace(p.FallbackHandBrakePreset)));
    }

    [Fact]
    public void BuiltIns_MirrorTheHandBrakeProfilesOfTheSameName()
    {
        var sdhd = new FFmpegProfileStore().Find("Compressarr SD-HD")!;
        var uhd = new FFmpegProfileStore().Find("Compressarr UHD AV1")!;

        Assert.Equal(("libx265", 24.0, "veryfast", "main10"), (sdhd.VideoCodec, sdhd.Quality, sdhd.Preset, sdhd.VideoProfile));
        Assert.Equal(("eac3", 512, "7.1", "first"), (sdhd.AudioCodec, sdhd.AudioBitrateKbps, sdhd.AudioMixdown, sdhd.AudioTracks));
        Assert.Equal(("libsvtav1", 30.0, "4", "psnr"), (uhd.VideoCodec, uhd.Quality, uhd.Preset, uhd.Tune));
        Assert.Equal(("passthru", "all"), (uhd.AudioMode, uhd.AudioTracks));
    }

    [Fact]
    public void AddedProfiles_ArePersisted_AndSurviveANewInstance()
    {
        new FFmpegProfileStore().AddUserProfiles(new[] { Profile("Cartoons x265") });

        var found = new FFmpegProfileStore().Find("cartoons X265")!;

        Assert.False(found.IsBuiltIn);
        Assert.Equal(26, found.Quality);
        Assert.Equal(new[] { "eng" }, found.SubtitleLanguages);
        Assert.True(File.Exists(AppPaths.GetFFmpegProfilesFilePath()));
    }

    [Fact]
    public void Names_AreUniqueWithinFfmpegsOwnCatalog()
    {
        var store = new FFmpegProfileStore();
        store.AddUserProfiles(new[] { Profile("Mine") });

        Assert.Throws<InvalidOperationException>(() => store.AddUserProfiles(new[] { Profile("mine") }));
        Assert.Throws<InvalidOperationException>(() => store.AddUserProfiles(new[] { Profile("compressarr sd-hd") }));
        Assert.Throws<InvalidOperationException>(() => store.AddUserProfiles(new[] { Profile("  ") }));
        Assert.Throws<InvalidOperationException>(() => store.AddUserProfiles(new[] { Profile("A"), Profile("a") }));
        Assert.Equal(4, store.GetAll().Count);
    }

    [Fact]
    public void ReplaceAndRemove_WorkOnYours_AndBuiltInsAreLocked()
    {
        var store = new FFmpegProfileStore();
        store.AddUserProfiles(new[] { Profile("A"), Profile("B") });

        store.ReplaceUserProfile("A", Profile("A renamed", 18));
        store.RemoveUserProfile("B");

        Assert.Equal(new[] { "A renamed" }, store.GetAll().Where(p => !p.IsBuiltIn).Select(p => p.Name).ToArray());
        Assert.Equal(18, store.Find("A renamed")!.Quality);
        Assert.Throws<InvalidOperationException>(() => store.ReplaceUserProfile("Compressarr SD-HD", Profile("x")));
        Assert.Throws<InvalidOperationException>(() => store.RemoveUserProfile("Compressarr SD-HD"));
        Assert.Throws<InvalidOperationException>(() => store.RemoveUserProfile("Nope"));
        Assert.Throws<InvalidOperationException>(() => store.ReplaceUserProfile("A renamed", Profile("Compressarr UHD AV1")));
    }

    [Fact]
    public void ReturnedProfiles_AreCopies()
    {
        var store = new FFmpegProfileStore();

        store.Find("Compressarr SD-HD")!.Quality = 1;
        store.GetAll()[0].AudioLanguages.Clear();

        Assert.Equal(24, store.Find("Compressarr SD-HD")!.Quality);
        Assert.NotEmpty(store.Find("Compressarr SD-HD")!.AudioLanguages);
    }

    [Fact]
    public void AnUnreadableUserFile_OffersBuiltInsOnly_ReportsWhy_AndIsNeverOverwritten()
    {
        Directory.CreateDirectory(AppPaths.GetProfilesDirectory());
        File.WriteAllText(AppPaths.GetFFmpegProfilesFilePath(), "{ not json");
        var store = new FFmpegProfileStore();

        Assert.Equal(3, store.GetAll().Count);
        Assert.False(string.IsNullOrWhiteSpace(store.UserFileError));
        Assert.Throws<InvalidOperationException>(() => store.AddUserProfiles(new[] { Profile("Mine") }));
        Assert.Equal("{ not json", File.ReadAllText(AppPaths.GetFFmpegProfilesFilePath()));
    }

    [Fact]
    public void ExtensionFollowsTheContainer()
    {
        Assert.Equal(".mkv", new FFmpegProfile { Container = "mkv" }.Extension);
        Assert.Equal(".mp4", new FFmpegProfile { Container = "MP4" }.Extension);
        Assert.Equal(".mkv", new FFmpegProfile { Container = "weird" }.Extension);
    }
}
