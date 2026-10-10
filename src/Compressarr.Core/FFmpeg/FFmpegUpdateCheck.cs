using System.Text.Json;
using Compressarr.Core.Config;

namespace Compressarr.Core.FFmpeg;

/// <summary>What Compressarr recorded when it installed ffmpeg itself (Encoder page, Check/Install): which
/// published build it was. BtbN's builds all have the same file name and the version string inside ffmpeg is a
/// commit id, so "is there a newer build?" can only be answered by remembering which build was downloaded.</summary>
public sealed record FFmpegInstallMarker(string Name, string AssetName, string? Sha256, DateTime InstalledUtc);

public static class FFmpegInstallMarkerFile
{
    public const string FileName = "ffmpeg-release.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static void Write(string installDir, FFmpegReleaseInfo release)
    {
        var marker = new FFmpegInstallMarker(release.Name, release.AssetName, release.Sha256, DateTime.UtcNow);
        File.WriteAllText(Path.Combine(installDir, FileName), JsonSerializer.Serialize(marker, Json));
    }

    /// <summary>The marker next to this ffmpeg.exe, or null when there isn't one: an ffmpeg Compressarr did not
    /// install, or one installed before this was recorded. Never throws - a damaged file is the same as none.</summary>
    public static FFmpegInstallMarker? Read(string? ffmpegPath)
    {
        try
        {
            var folder = string.IsNullOrWhiteSpace(ffmpegPath) ? null : Path.GetDirectoryName(ffmpegPath);
            if (folder is null) return null;
            var file = Path.Combine(folder, FileName);
            return File.Exists(file) ? JsonSerializer.Deserialize<FFmpegInstallMarker>(File.ReadAllText(file), Json) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public enum FFmpegUpdateStatus
{
    /// <summary>No working ffmpeg at the configured path.</summary>
    NotInstalled,
    /// <summary>Compressarr installed it and it is the latest published build.</summary>
    UpToDate,
    /// <summary>Compressarr installed it and a newer build has been published.</summary>
    Newer,
    /// <summary>Compressarr installed it from the other channel than the one selected (Stable vs Latest), so "newer" has
    /// no meaning: installing from the selected channel switches over.</summary>
    OtherChannel,
    /// <summary>ffmpeg works, but Compressarr did not install it (or did so before builds were recorded), so there is
    /// nothing to compare the latest build with.</summary>
    Unknown
}

public static class FFmpegUpdateCheck
{
    /// <summary>The channel a recorded install came from, read off its asset name: BtbN names the master build
    /// ffmpeg-master-latest-..., a release branch ffmpeg-n9.0-latest-.... Beta 4 and 5 recorded no channel, and all
    /// of those installs were master builds, so this also classifies them correctly.</summary>
    public static FFmpegChannel ChannelOf(string assetName) =>
        assetName.StartsWith("ffmpeg-master", StringComparison.OrdinalIgnoreCase) ? FFmpegChannel.Latest : FFmpegChannel.Stable;

    public static FFmpegUpdateStatus Compare(bool installed, FFmpegInstallMarker? marker, FFmpegReleaseInfo latest, FFmpegChannel selected)
    {
        if (!installed) return FFmpegUpdateStatus.NotInstalled;
        if (marker is null) return FFmpegUpdateStatus.Unknown;
        if (ChannelOf(marker.AssetName) != selected) return FFmpegUpdateStatus.OtherChannel;

        // The checksum GitHub publishes identifies the exact build; fall back to the release's name when there is none.
        var same = !string.IsNullOrWhiteSpace(marker.Sha256) && !string.IsNullOrWhiteSpace(latest.Sha256)
            ? string.Equals(marker.Sha256, latest.Sha256, StringComparison.OrdinalIgnoreCase)
            : string.Equals(marker.Name, latest.Name, StringComparison.Ordinal);
        return same ? FFmpegUpdateStatus.UpToDate : FFmpegUpdateStatus.Newer;
    }
}
