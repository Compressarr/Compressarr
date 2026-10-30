using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Compressarr.Core.Dependencies;

/// <param name="Sha256">The SHA-256 GitHub publishes for the asset (hex), or null when it published none; an install without one is refused.</param>
public sealed record HandBrakeReleaseInfo(string Version, string AssetName, string DownloadUrl, long SizeBytes, string ReleaseUrl, string? Sha256 = null);

public interface IHandBrakeInstaller
{
    /// <summary>Queries HandBrake's GitHub releases for the latest HandBrakeCLI build matching
    /// the current OS/architecture. Returns null on Linux (HandBrake doesn't publish a generic
    /// Linux CLI binary there - Linux users install via their distro's package manager/Flatpak
    /// instead) or if no matching asset is found.</summary>
    Task<HandBrakeReleaseInfo?> GetLatestReleaseAsync();

    /// <summary>Downloads and installs the given release into installDir, returning the full path
    /// to the resulting HandBrakeCLI executable. Callers are expected to have already confirmed
    /// this download with the user (name, version, size) before calling - this method itself
    /// performs no prompting. The download is checked against the SHA-256 GitHub publishes for the asset; with no
    /// checksum, or one that does not match, nothing is installed (InvalidDataException).</summary>
    Task<string> InstallAsync(HandBrakeReleaseInfo release, string installDir, IProgress<string>? progress = null);

    /// <summary>Runs the HandBrakeCLI binary at cliPath with --version and parses the version
    /// number out of its own output. Returns null (never throws) if the file doesn't exist, isn't
    /// runnable, or its output doesn't contain anything version-shaped - the caller (an About-page
    /// display) should treat that as "unknown", not fail.</summary>
    Task<string?> GetInstalledVersionAsync(string cliPath);
}

public sealed class HandBrakeInstaller : IHandBrakeInstaller
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/HandBrake/HandBrake/releases/latest";
    private readonly IHttpClientFactory _httpClientFactory;

    public HandBrakeInstaller(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<HandBrakeReleaseInfo?> GetLatestReleaseAsync()
    {
        if (OperatingSystem.IsLinux()) return null;

        using var client = _httpClientFactory.CreateClient(nameof(HandBrakeInstaller));
        // The GitHub API requires a User-Agent header on every request or it responds 403.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Compressarr/2.0");

        var response = await client.GetAsync(ReleasesApiUrl);
        response.EnsureSuccessStatusCode();
        var release = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        var version = release["tag_name"]!.GetValue<string>();
        var releaseUrl = release["html_url"]!.GetValue<string>();
        var assets = release["assets"]!.AsArray();

        var assetNamePattern = GetExpectedAssetName(version);
        if (assetNamePattern is null) return null;

        foreach (var assetNode in assets)
        {
            var asset = assetNode!.AsObject();
            var name = asset["name"]!.GetValue<string>();
            if (name == assetNamePattern)
            {
                return new HandBrakeReleaseInfo(
                    Version: version,
                    AssetName: name,
                    DownloadUrl: asset["browser_download_url"]!.GetValue<string>(),
                    SizeBytes: asset["size"]!.GetValue<long>(),
                    ReleaseUrl: releaseUrl,
                    Sha256: DigestOf(asset));
            }
        }

        return null;
    }

    /// <summary>GitHub publishes each asset's checksum as "sha256:&lt;hex&gt;"; null when there is none (or it is some other algorithm).</summary>
    internal static string? DigestOf(JsonObject asset)
    {
        var digest = asset["digest"]?.GetValue<string>();
        return digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) && digest.Length > 7 ? digest[7..] : null;
    }

    /// <summary>Throws InvalidDataException when GitHub published no checksum for the download (nothing should be fetched then).</summary>
    internal static void RequireChecksum(string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            throw new InvalidDataException("GitHub published no checksum for this HandBrake download, so it can't be verified. Nothing was installed - download HandBrakeCLI yourself and set its path on the Encoder page.");
        }
    }

    /// <summary>Throws InvalidDataException unless the download's checksum is the one GitHub published.</summary>
    internal static void VerifyChecksum(string actualSha256, string? expectedSha256)
    {
        RequireChecksum(expectedSha256);
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The downloaded file does not match the checksum GitHub published for it, so it was thrown away and nothing was installed.");
        }
    }

    public async Task<string?> GetInstalledVersionAsync(string cliPath)
    {
        if (!File.Exists(cliPath)) return null;

        try
        {
            using var process = Process.Start(new ProcessStartInfo(cliPath, "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            })!;

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = (await stdoutTask) + (await stderrTask);

            // HandBrakeCLI prints something like "HandBrake 1.8.2 (...)" - pull out the first
            // dotted version number rather than assuming the exact surrounding wording.
            var match = System.Text.RegularExpressions.Regex.Match(output, @"\d+\.\d+(\.\d+)?");
            return match.Success ? match.Value : null;
        }
        catch
        {
            return null;
        }
    }

    internal static string? GetExpectedAssetName(string version)
    {
        if (OperatingSystem.IsWindows())
        {
            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "win-aarch64",
                _ => "win-x86_64"
            };
            return $"HandBrakeCLI-{version}-{arch}.zip";
        }

        if (OperatingSystem.IsMacOS())
        {
            return $"HandBrakeCLI-{version}.dmg";
        }

        return null;
    }

    public async Task<string> InstallAsync(HandBrakeReleaseInfo release, string installDir, IProgress<string>? progress = null)
    {
        // Refused before anything is downloaded: a program that is about to be run is not installed on trust.
        RequireChecksum(release.Sha256);

        Directory.CreateDirectory(installDir);

        var downloadPath = Path.Combine(Path.GetTempPath(), "compressarr-" + Guid.NewGuid().ToString("N")[..8] + "-" + release.AssetName);
        try
        {
            progress?.Report($"Downloading {release.AssetName} ({release.SizeBytes / 1024 / 1024} MB)...");

            string actual;
            using (var client = _httpClientFactory.CreateClient(nameof(HandBrakeInstaller)))
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Compressarr/2.0");
                using var sha = SHA256.Create();
                await using var httpStream = await client.GetStreamAsync(release.DownloadUrl);
                await using var fileStream = File.Create(downloadPath);
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = await httpStream.ReadAsync(buffer)) > 0)
                {
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                }

                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                actual = Convert.ToHexString(sha.Hash!);
            }

            progress?.Report("Verifying the download...");
            VerifyChecksum(actual, release.Sha256);

            if (OperatingSystem.IsWindows())
            {
                return InstallFromWindowsZip(downloadPath, installDir, progress);
            }
            if (OperatingSystem.IsMacOS())
            {
                return await InstallFromMacDmgAsync(downloadPath, installDir, progress);
            }

            throw new PlatformNotSupportedException("HandBrakeCLI auto-install is only supported on Windows and macOS.");
        }
        finally
        {
            try { File.Delete(downloadPath); } catch { /* a leftover temp file does no harm */ }
        }
    }

    private static string InstallFromWindowsZip(string zipPath, string installDir, IProgress<string>? progress)
    {
        progress?.Report("Extracting HandBrakeCLI.exe...");
        var extractDir = Path.Combine(Path.GetTempPath(), "compressarr-hb-extract-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            ZipFile.ExtractToDirectory(zipPath, extractDir);
            var exePath = Directory.EnumerateFiles(extractDir, "HandBrakeCLI.exe", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidOperationException("HandBrakeCLI.exe not found inside the downloaded archive.");

            var destPath = Path.Combine(installDir, "HandBrakeCLI.exe");
            File.Copy(exePath, destPath, overwrite: true);
            WriteLicenseNotice(installDir);
            progress?.Report("HandBrakeCLI installed.");
            return destPath;
        }
        finally
        {
            try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true); } catch { /* temp folder; the system clears it eventually */ }
        }
    }

    /// <summary>HandBrake is GPL software that Compressarr downloads on the user's behalf; a short note beside it says
    /// what it is, which license it is under and where its source is (the release archive carries no license file).</summary>
    internal static void WriteLicenseNotice(string installDir)
    {
        try
        {
            File.WriteAllLines(Path.Combine(installDir, "LICENSE-NOTICE.txt"), new[]
            {
                "HandBrakeCLI",
                "",
                "This program is HandBrake, downloaded by Compressarr from the official HandBrake release page. It is free software",
                "under the GNU General Public License, version 2. HandBrake is not part of Compressarr.",
                "",
                "Project and source code: https://github.com/HandBrake/HandBrake",
                "License: https://www.gnu.org/licenses/old-licenses/gpl-2.0.html"
            });
        }
        catch (IOException)
        {
            // A note beside the program is a courtesy; never fail an install over it.
        }
    }

    [SupportedOSPlatform("macos")]
    private static async Task<string> InstallFromMacDmgAsync(string dmgPath, string installDir, IProgress<string>? progress)
    {
        progress?.Report("Mounting disk image...");
        var mountPoint = Path.Combine(Path.GetTempPath(), "compressarr-hb-mount-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(mountPoint);

        await RunProcessAsync("hdiutil", $"attach \"{dmgPath}\" -nobrowse -mountpoint \"{mountPoint}\"");

        try
        {
            var cliPath = Directory.EnumerateFiles(mountPoint, "HandBrakeCLI", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new InvalidOperationException("HandBrakeCLI not found inside the downloaded disk image.");

            var destPath = Path.Combine(installDir, "HandBrakeCLI");
            File.Copy(cliPath, destPath, overwrite: true);
            File.SetUnixFileMode(destPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

            progress?.Report("HandBrakeCLI installed.");
            return destPath;
        }
        finally
        {
            await RunProcessAsync("hdiutil", $"detach \"{mountPoint}\"");
        }
    }

    private static async Task RunProcessAsync(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException($"'{fileName} {arguments}' failed with exit code {process.ExitCode}: {stderr}");
        }
    }
}
