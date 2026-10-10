using System.IO.Compression;
using System.Text.RegularExpressions;
using Compressarr.Core.Config;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Compressarr.Core.FFmpeg;

/// <param name="Branch">The numbered release branch a Stable build is from ("9.0"); null for a Latest (master) build.</param>
public sealed record FFmpegReleaseInfo(string Name, string AssetName, string DownloadUrl, long SizeBytes, string ReleaseUrl, string? Sha256, string? Branch = null);

/// <summary>
/// Finds and installs a managed copy of ffmpeg (Windows: the GPL build from BtbN's FFmpeg-Builds on
/// GitHub - it includes x265, SVT-AV1 and NVENC) into Compressarr's own folder, so no administrator
/// rights are needed and nothing else on the machine is touched. The caller has already asked the user
/// (name, size, source); nothing here prompts. The download is verified against the SHA-256 GitHub
/// publishes for the asset, and only ffmpeg.exe, ffprobe.exe and the license file are kept.
/// </summary>
public interface IFFmpegInstaller
{
    /// <summary>The newest build on <paramref name="channel"/> for this OS/architecture, or null (Linux/macOS: use
    /// your package manager; or nothing suitable was published).</summary>
    Task<FFmpegReleaseInfo?> GetLatestReleaseAsync(FFmpegChannel channel);

    /// <summary>Downloads, verifies and unpacks the release into <paramref name="installDir"/>;
    /// returns the path to ffmpeg.exe. Throws InvalidDataException when the checksum doesn't match.</summary>
    Task<string> InstallAsync(FFmpegReleaseInfo release, string installDir, IProgress<string>? progress = null, CancellationToken cancellationToken = default);
}

public sealed class FFmpegInstaller : IFFmpegInstaller
{
    private const string ReleaseApiUrl = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest";
    private readonly IHttpClientFactory _httpClientFactory;

    public FFmpegInstaller(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public async Task<FFmpegReleaseInfo?> GetLatestReleaseAsync(FFmpegChannel channel)
    {
        if (!OperatingSystem.IsWindows()) return null;

        using var client = _httpClientFactory.CreateClient(nameof(FFmpegInstaller));
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Compressarr/2.2");

        var response = await client.GetAsync(ReleaseApiUrl);
        response.EnsureSuccessStatusCode();
        var release = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        return SelectAsset(release, RuntimeInformation.ProcessArchitecture, channel);
    }

    /// <summary>Picks the GPL build for the architecture and channel out of a GitHub release document. Latest is the
    /// master asset; Stable is the numbered release branch with the highest version (BtbN keeps a few, e.g. 8.1 and
    /// 9.0, so "newest" is chosen by number, not by whatever the list happens to order first). Pure, so it is tested
    /// against a real-shaped release without the network.</summary>
    internal static FFmpegReleaseInfo? SelectAsset(JsonObject release, Architecture architecture, FFmpegChannel channel)
    {
        var platform = architecture == Architecture.Arm64 ? "winarm64" : "win64";
        if (release["assets"] is not JsonArray assets) return null;

        JsonObject? chosen = null;
        string? branch = null;
        if (channel == FFmpegChannel.Latest)
        {
            var master = $"ffmpeg-master-latest-{platform}-gpl.zip";
            chosen = assets.OfType<JsonObject>().FirstOrDefault(a => a["name"]?.GetValue<string>() == master);
        }
        else
        {
            var pattern = new Regex($@"^ffmpeg-n(\d+)\.(\d+)-latest-{platform}-gpl-\d+\.\d+\.zip$", RegexOptions.CultureInvariant);
            (int Major, int Minor) best = (-1, -1);
            foreach (var node in assets)
            {
                if (node is not JsonObject candidate) continue;
                var match = pattern.Match(candidate["name"]?.GetValue<string>() ?? "");
                if (!match.Success) continue;

                var version = (Major: int.Parse(match.Groups[1].Value), Minor: int.Parse(match.Groups[2].Value));
                if (version.CompareTo(best) <= 0) continue;
                best = version;
                chosen = candidate;
                branch = $"{version.Major}.{version.Minor}";
            }
        }

        if (chosen is null) return null;

        // GitHub publishes each asset's checksum as "sha256:<hex>".
        var digest = chosen["digest"]?.GetValue<string>();
        var sha = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;

        return new FFmpegReleaseInfo(
            Name: release["name"]?.GetValue<string>() ?? release["tag_name"]?.GetValue<string>() ?? "latest build",
            AssetName: chosen["name"]!.GetValue<string>(),
            DownloadUrl: chosen["browser_download_url"]!.GetValue<string>(),
            SizeBytes: chosen["size"]?.GetValue<long>() ?? 0,
            ReleaseUrl: release["html_url"]?.GetValue<string>() ?? "https://github.com/BtbN/FFmpeg-Builds/releases",
            Sha256: sha,
            Branch: branch);
    }

    public async Task<string> InstallAsync(FFmpegReleaseInfo release, string installDir, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(release.Sha256))
        {
            throw new InvalidDataException("GitHub published no checksum for this download, so it can't be verified. Nothing was installed - download ffmpeg yourself and set its path on the Encoder page.");
        }

        var downloadPath = Path.Combine(Path.GetTempPath(), "compressarr-" + Guid.NewGuid().ToString("N")[..8] + "-" + release.AssetName);
        try
        {
            progress?.Report($"Downloading {release.AssetName} ({release.SizeBytes / 1024 / 1024} MB)...");
            string actual;
            using (var client = _httpClientFactory.CreateClient(nameof(FFmpegInstaller)))
            {
                client.Timeout = TimeSpan.FromMinutes(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Compressarr/2.2");
                using var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                using var sha = SHA256.Create();
                await using var http = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var file = File.Create(downloadPath);
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = await http.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                actual = Convert.ToHexString(sha.Hash!);
            }

            progress?.Report("Verifying the download...");
            var installed = VerifyAndExtract(downloadPath, actual, release.Sha256, installDir, progress);
            FFmpegInstallMarkerFile.Write(installDir, release); // which build this is, for the About page's update check
            return installed;
        }
        finally
        {
            try { File.Delete(downloadPath); } catch { }
        }
    }

    /// <summary>Checks the checksum, then unpacks ffmpeg.exe, ffprobe.exe and the license into
    /// <paramref name="installDir"/>. Each file is written to a temporary name and moved into place, so
    /// a half-written install can never be left behind. Returns the path to ffmpeg.exe.</summary>
    internal static string VerifyAndExtract(string zipPath, string actualSha256, string expectedSha256, string installDir, IProgress<string>? progress = null)
    {
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded file does not match the checksum GitHub published for it, so it was thrown away and nothing was installed.");
        }

        Directory.CreateDirectory(installDir);
        progress?.Report("Unpacking ffmpeg...");

        using var zip = ZipFile.OpenRead(zipPath);
        var wanted = new (string Suffix, string Target)[]
        {
            ("/bin/ffmpeg.exe", "ffmpeg.exe"),
            ("/bin/ffprobe.exe", "ffprobe.exe"),
            ("/LICENSE.txt", "LICENSE.txt")
        };

        var staged = new List<(string Temp, string Final)>();
        try
        {
            foreach (var (suffix, target) in wanted)
            {
                var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                if (entry is null)
                {
                    if (target == "LICENSE.txt") continue;
                    throw new InvalidDataException($"{target} was not found inside the downloaded archive.");
                }

                var final = Path.Combine(installDir, target);
                var temp = final + ".tmp";
                entry.ExtractToFile(temp, overwrite: true);
                staged.Add((temp, final));
            }

            foreach (var (temp, final) in staged) File.Move(temp, final, overwrite: true);
        }
        catch
        {
            foreach (var (temp, _) in staged) { try { File.Delete(temp); } catch { } }
            throw;
        }

        progress?.Report("ffmpeg installed.");
        return Path.Combine(installDir, "ffmpeg.exe");
    }
}
