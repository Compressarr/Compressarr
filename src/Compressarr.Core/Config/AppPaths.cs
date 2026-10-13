namespace Compressarr.Core.Config;

/// <summary>
/// Resolves the per-OS application-data directory Compressarr's config/logs/reports live in by
/// default. Roadmap requirement: preferences must live here, not in the app's install folder, so
/// an upgrade never overwrites user settings.
/// </summary>
public static class AppPaths
{
    private const string AppFolderName = "Compressarr";

    /// <summary>Test-only override for GetAppDataDirectory()'s result - when set, every path this
    /// class returns is rooted here instead of the real per-OS app-data directory. Exists solely so
    /// a test exercising a real end-to-end path through RunOrchestrator.RunOnceAsync (which reads/
    /// writes resume.json, settings.json, etc. via these paths internally - they aren't parameters
    /// it accepts) can never read or write this machine's actual production Compressarr data.
    /// Always null outside tests.</summary>
    public static string? TestOverrideAppDataDirectory { get; set; }

    public static string GetAppDataDirectory()
    {
        if (TestOverrideAppDataDirectory is not null) return TestOverrideAppDataDirectory;

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, AppFolderName);
        }

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "Application Support", AppFolderName);
        }

        // Linux and any other Unix-like platform: XDG Base Directory spec.
        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdgConfigHome))
        {
            return Path.Combine(xdgConfigHome, AppFolderName);
        }

        var fallbackHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(fallbackHome, ".config", AppFolderName);
    }

    public static string GetConfigFilePath() => Path.Combine(GetAppDataDirectory(), "compressarr.settings.json");

    public static string GetRunCountFilePath() => Path.Combine(GetAppDataDirectory(), "compressarr.runcount.json");

    public static string GetResumeFilePath() => Path.Combine(GetAppDataDirectory(), "compressarr.resume.json");

    /// <summary>The run-history file behind the History page's totals. It lives with the other permanent data, not in
    /// the Logs folder (a place people clear out, and one that moves when the log path is changed). The file keeps its
    /// 2.1.x name, so a 2.2 backup restores into a 2.1.x install's expectations and vice versa.</summary>
    public static string GetHistoryFilePath() => Path.Combine(GetAppDataDirectory(), "Compressarr_History.csv");

    /// <summary>Compressarr's own encoder profiles live here: the user's HandBrake profiles (backed
    /// up with the rest of the settings) and the generated file HandBrake is actually handed.</summary>
    public static string GetProfilesDirectory() => Path.Combine(GetAppDataDirectory(), "Profiles");

    /// <summary>The user's own HandBrake profiles (created, duplicated or imported). The built-ins are
    /// not in here - they ship inside the app.</summary>
    public static string GetHandBrakeProfilesFilePath() => Path.Combine(GetProfilesDirectory(), "handbrake-profiles.json");

    /// <summary>The single presets file Compressarr generates (built-ins plus the user's profiles) and
    /// passes to HandBrakeCLI with one --preset-import-file. Derived data: never backed up, always
    /// rebuilt from the two sources above.</summary>
    public static string GetHandBrakeActivePresetsFilePath() => Path.Combine(GetProfilesDirectory(), "handbrake-active.json");

    /// <summary>The user's own ffmpeg profiles. The built-ins ship inside the app.</summary>
    public static string GetFFmpegProfilesFilePath() => Path.Combine(GetProfilesDirectory(), "ffmpeg-profiles.json");

    /// <summary>The HandBrake profiles the user added from a catalog (the same file shape as handbrake-profiles.json). Locked like the
    /// built-ins, but deletable; backed up with the rest of the settings.</summary>
    public static string GetCatalogHandBrakeFilePath() => Path.Combine(GetProfilesDirectory(), "catalog-handbrake.json");

    /// <summary>The ffmpeg profiles the user added from a catalog (the same file shape as ffmpeg-profiles.json).</summary>
    public static string GetCatalogFFmpegFilePath() => Path.Combine(GetProfilesDirectory(), "catalog-ffmpeg.json");

    /// <summary>What Compressarr remembers about announcements on this machine (read marks, the first-look day, the last good copy).</summary>
    public static string GetAnnouncementsFilePath() => Path.Combine(GetAppDataDirectory(), "compressarr.announcements.json");

    /// <summary>Where each installed catalog profile came from and the fingerprint it must still match.</summary>
    public static string GetCatalogRecordsFilePath() => Path.Combine(GetProfilesDirectory(), "catalog-installed.json");

    /// <summary>The poster cache: what was looked up on TMDb (index.json) and the downloaded images, by size. Safe to delete.</summary>
    public static string GetPostersDirectory() => Path.Combine(GetAppDataDirectory(), "posters");

    /// <summary>Where Check/Install puts a managed copy of ffmpeg (and ffprobe) - per user, inside
    /// Compressarr's own folder, so no administrator rights are needed.</summary>
    public static string GetToolsDirectory() => Path.Combine(GetAppDataDirectory(), "tools");
}
