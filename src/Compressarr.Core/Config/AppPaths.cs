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

    /// <summary>What Compressarr remembers about announcements on this machine (read marks, the first-look day, the last good copy).</summary>
    public static string GetAnnouncementsFilePath() => Path.Combine(GetAppDataDirectory(), "compressarr.announcements.json");

    public static string GetConfigFilePath() => Path.Combine(GetAppDataDirectory(), "compressarr.settings.json");

    public static string GetRunCountFilePath() => Path.Combine(GetAppDataDirectory(), "compressarr.runcount.json");

    public static string GetResumeFilePath() => Path.Combine(GetAppDataDirectory(), "compressarr.resume.json");
}
