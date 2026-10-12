namespace Compressarr.Core.Config;

public enum DeleteAfterConvertMode
{
    Maintain,
    Delete,
    Recycle
}

public enum OpenReportMode
{
    Always,
    OnError,
    Never
}

/// <summary>What FileRouter does when a converted file's destination path already has a file
/// there - e.g. the same title converted a second time. Overwrite matches Compressarr's original
/// behavior (silent overwrite, supports "re-run to get a better encode"); Skip leaves the newly
/// converted file sitting in the Output folder untouched (a warning is logged, not an error - it's
/// working as configured); Rename appends " (2)", " (3)", etc. until a free name is found.</summary>
public enum DestinationCollisionMode
{
    Overwrite,
    Skip,
    Rename
}

public sealed class CompressarrConfig
{
    public HandBrakeSettings HandBrake { get; set; } = new();

    /// <summary>The optional ffmpeg encoder's tool paths and extra options (2.2). Additive: a 2.1.x
    /// install ignores it.</summary>
    public FFmpegSettings FFmpeg { get; set; } = new();
    public List<LaneConfig> Lanes { get; set; } = new();
    public ProcessingSettings Processing { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public PostExecSettings PostExec { get; set; } = new();
    public ReportSettings Report { get; set; } = new();
    public RepeatSettings Repeat { get; set; } = new();
    public StartupSettings Startup { get; set; } = new();

    /// <summary>Short news items from the Compressarr project (2.2 and 2.1.10 on). On by default.</summary>
    public AnnouncementSettings Announcements { get; set; } = new();
    public ArrSettings Arrs { get; set; } = new();
    public WebSettings Web { get; set; } = new();
    public BackupSettings Backup { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
    public FileBotSettings FileBot { get; set; } = new();
    public UiStateSettings UiState { get; set; } = new();
    public ScheduleSettings Schedule { get; set; } = new();
}

/// <summary>Small, purely client-driven UI state that happens to need server-side persistence
/// (so it survives across browser sessions/devices) - not a user-facing setting, never shown on
/// the Settings page.</summary>
public sealed class UiStateSettings
{
    /// <summary>The highest IRunHistoryStore.GetRunCount value that existed the last time the
    /// History page was opened - drives the sidebar's error/warning badges, which only count runs
    /// newer than this. 0 before the History page has ever been visited, in which case the badges
    /// count every run in the retention window (same as before this field existed).</summary>
    public int HistoryViewedThroughRunNumber { get; set; }
}

/// <summary>The ffmpeg encoder's settings. Paths default to where the Encoder page's Check/Install
/// puts a managed copy, inside Compressarr's own folder.</summary>
public sealed class FFmpegSettings
{
    public string Path { get; set; } = "%CompressarrAppData%\\tools\\ffmpeg\\ffmpeg.exe";
    public string ProbePath { get; set; } = "%CompressarrAppData%\\tools\\ffmpeg\\ffprobe.exe";
    public string Options { get; set; } = "";

    /// <summary>Which ffmpeg build Check/Install downloads: Stable (default) or Latest. See <see cref="FFmpegChannel"/>.</summary>
    public FFmpegChannel Channel { get; set; } = FFmpegChannel.Stable;
}

/// <summary>Which of BtbN's ffmpeg builds the managed download comes from. Stable is the newest numbered release
/// branch (9.0 today, 9.1/10.0 when they appear): bug fixes only, so encodes behave the same from week to week.
/// Latest is the master (development) build, rebuilt daily, with the newest encoders and options but nothing
/// promised about behaviour. Shown under Advanced on the Encoder page.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum FFmpegChannel
{
    Stable,
    Latest
}

public sealed class HandBrakeSettings
{
    public string CliPath { get; set; } = "%ProgramFiles%\\HandBrake\\HandBrakeCLI.exe";

    /// <summary>Retired in 2.2: Compressarr always uses its own profiles now (see
    /// IHandBrakeProfileStore), so this is no longer shown or validated. Kept in the settings file
    /// (a 2.1.x install reads the same file) and used only to find the old presets.json when
    /// migrating the presets a lane used, and as Import's default location.</summary>
    public string PresetsPath { get; set; } = "%appdata%\\HandBrake\\presets.json";

    public string Options { get; set; } = "";
}

/// <summary>Optional pre-processing step that shells out to the third-party FileBot tool
/// (filebot.net) to clean up/rename files against TheTVDB/TMDB before Compressarr scans a lane's
/// Input folder - mainly useful for users who don't run Sonarr/Radarr, which already do this kind
/// of rename/organize themselves. Off by default; a real product decision, not a small addition -
/// see IFileBotRunner for the invocation itself.</summary>
public sealed class FileBotSettings
{
    public bool Enabled { get; set; } = false;
    public string CliPath { get; set; } = "%ProgramFiles%\\FileBot\\filebot.exe";

    /// <summary>Explicit opt-out for TV/movie handling independently - distinct from leaving the
    /// matching Args blank (which means "not configured yet," not "deliberately off"). Both
    /// default true so filling in Args for one type is enough to activate it, no second toggle to
    /// hunt for.</summary>
    public bool TvEnabled { get; set; } = true;

    /// <summary>Passed to FileBot verbatim for this lane's TV-classified files only (per
    /// ContentClassifier.IsTvFile), with "{files}" replaced by a quoted, space-separated list of
    /// just those files' full paths. Deliberately no pre-filled default (unlike HandBrake's
    /// CliPath) - the right flags/database/format choice depend on the user's own library
    /// conventions, best found in FileBot's own docs rather than guessed at here. The Settings
    /// page's "TV episode numbering" dropdown is a pure client-side convenience that edits this
    /// text directly (swapping "{s00e00}"/"{sxe}") rather than a separate stored value - this
    /// string is always the real, complete command.</summary>
    public string TvArgs { get; set; } = "";

    public bool MovieEnabled { get; set; } = true;

    /// <summary>Same as TvArgs but for this lane's non-TV (movie) files - "{files}" substitutes to
    /// just the movie files' paths. No "{format}" token here (movie naming has no season/episode
    /// numbering-style choice to make).</summary>
    public string MovieArgs { get; set; } = "";
}

public sealed class LaneConfig
{
    /// <summary>Stable, immutable once created — the join key used everywhere lanes are referenced.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string DisplayName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string Input { get; set; } = "";
    public string Output { get; set; } = "";
    public string TvPreset { get; set; } = "";
    public string MoviePreset { get; set; } = "";
    public string TvShowBasePath { get; set; } = "";
    public string MovieBasePath { get; set; } = "";

    /// <summary>The profile a brand-new lane starts with for both Movies and TV: the built-in, always-present
    /// general-purpose HandBrake profile, so a new user has a working choice without having to pick one first.</summary>
    public const string DefaultProfileName = "Compressarr SD-HD";

    /// <summary>A lane as the Lanes page's "Add lane" (and "Reset lanes") creates it: enabled, with the default profile for both
    /// content types. Input, Output and the library folders are still the user's to fill in.</summary>
    public static LaneConfig CreateNew(string displayName) => new()
    {
        DisplayName = displayName,
        Enabled = true,
        TvPreset = DefaultProfileName,
        MoviePreset = DefaultProfileName
    };

    /// <summary>Which encoder this lane uses (2.2). HandBrake unless chosen otherwise. A 2.1.x install
    /// ignores the field; it would then look for a HandBrake preset by an ffmpeg profile's name, find
    /// none, and skip the lane with the usual "preset not found" message - never encoding with the
    /// wrong tool.</summary>
    public EncoderEngine Engine { get; set; } = EncoderEngine.HandBrake;

    /// <summary>HandBrake lanes: encode an HDR / Dolby Vision file even when the profile's encoder would lose the HDR (an SDR-only
    /// H.264 profile, or a hardware encoder that can't keep Dolby Vision / HDR10+). Off by default: such a file is refused and
    /// reported (ERROR 114) rather than re-encoded and replaced. It never covers a Dolby Vision profile 5 file, which would show
    /// wrong colours. ffmpeg lanes have the equivalent setting on the profile.</summary>
    public bool AllowHdrLoss { get; set; }
}

/// <summary>The encoders a lane can use.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum EncoderEngine
{
    HandBrake,
    FFmpeg
}

public sealed class ProcessingSettings
{
    /// <summary>Extensions without a leading dot (matches v1's stored format — a dot is
    /// prepended at point-of-use when building glob patterns).</summary>
    public List<string> VidTypes { get; set; } = new() { "mkv", "avi", "mp4", "mpg", "ts", "m4v" };
    public bool OutSameAsIn { get; set; } = false;
    public DeleteAfterConvertMode DeleteAfterConvert { get; set; } = DeleteAfterConvertMode.Recycle;
    public bool MoveFiles { get; set; } = true;
    public int Limit { get; set; } = 0;

    /// <summary>Strips the embedded title tag from converted files via TagLib-Sharp so a media
    /// server reads the filename instead of stale/incorrect metadata. Defaults to true since v2
    /// bundles TagLib-Sharp unconditionally (v1.1 only did this if a taglib-sharp.dll happened to
    /// be present next to the script).</summary>
    public bool ClearTitleMetadata { get; set; } = true;

    /// <summary>Typed byte count. v1 stored this as a unit-suffix string ("0gb"/"500mb"); v2's
    /// own config file doesn't need to preserve that shape, so this is a plain long, with
    /// ByteSizeParser/ByteSizeFormatter available for UI display and for parsing a hand-edited
    /// unit-suffix string.</summary>
    public long MinSizeBytes { get; set; } = 0;

    /// <summary>Extensions (no leading dot, same convention as VidTypes) that get moved alongside
    /// a video when it's routed to its destination - subtitles, artwork, etc, matched against a
    /// stem-matched candidate's own extension. A stem-matched file whose extension ISN'T here is
    /// handled per UnmatchedCompanionAction instead of being assumed to be a companion.</summary>
    public List<string> CompanionExtensions { get; set; } = new() { "srt", "nfo", "jpg", "jpeg", "png", "tbn", "ass", "ssa", "idx", "sub", "vtt" };

    /// <summary>What happens to a stem-matched file whose extension isn't in CompanionExtensions,
    /// and to anything still left in a source folder once its last video is gone. Same three
    /// options and default (Recycle) as DeleteAfterConvert above, for consistency.</summary>
    public DeleteAfterConvertMode UnmatchedCompanionAction { get; set; } = DeleteAfterConvertMode.Recycle;

    public DestinationCollisionMode OnDestinationCollision { get; set; } = DestinationCollisionMode.Overwrite;
}

public sealed class LoggingSettings
{
    public string LogFilePath { get; set; } = "%CompressarrAppData%\\Logs";
    public int RetentionDays { get; set; } = 30;

    /// <summary>Off by default. A successful encode's own HBdetails.txt (HandBrake's raw output
    /// for that one file) is deleted as soon as all other processing of that file finishes, rather
    /// than left to age out via RetentionDays like every other log/report file - most of these are
    /// never opened, and by far most passes are successful, so they were the largest source of log
    /// clutter even after RetentionDays kicks in. A failed encode's detail log is always kept
    /// (linked from the report) regardless of this setting - only successful ones are affected.</summary>
    public bool KeepSuccessfulHandBrakeLogs { get; set; } = false;
}

public sealed class PostExecSettings
{
    public string Cmd { get; set; } = "";
    public string Args { get; set; } = "";
}

public sealed class ReportSettings
{
    public string ReportPath { get; set; } = "%CompressarrAppData%\\Reports";
    public OpenReportMode OpenAfterRun { get; set; } = OpenReportMode.OnError;
}

public sealed class RepeatSettings
{
    public int Count { get; set; } = 0;
    public bool Monitor { get; set; } = false;

    /// <summary>When true, the tray host opens the Monitor page in the OS default browser as soon
    /// as the web server is up - independent of Monitor above (monitoring can auto-start with no
    /// browser window, or a browser can open with monitoring left off, or both/neither).</summary>
    public bool LaunchMonitorAtStartup { get; set; } = false;

    /// <summary>Cadence for IRunLoopController's monitor-mode loop - matches v1's original 60s
    /// countdown between polls.</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>How the Monitor page's "Queue Completion" stat displays the estimated finish time
    /// it's already given (an ISO timestamp from RunEndpoints.ComputeQueueEtaText) - purely a
    /// client-side rendering choice, so this doesn't change what the server computes/sends, only
    /// how monitor.js formats it. Defaults to Duration for new installs (explicit user choice,
    /// 2026-09-07) - an existing install's own saved value in settings.json always wins over this
    /// default once a config file exists, so this only affects a genuinely fresh install.</summary>
    public QueueEtaDisplayFormat QueueEtaFormat { get; set; } = QueueEtaDisplayFormat.Duration;
}

/// <summary>DateTime: "Sep 06 2026, 1:45PM" (absolute). Duration: "2d 5h 36m" remaining from now
/// (relative, adaptive - drops the leading zero units), added per explicit user request as an
/// alternative to the absolute-time display.</summary>
public enum QueueEtaDisplayFormat
{
    DateTime,
    Duration
}

public sealed class AnnouncementSettings
{
    /// <summary>When false Compressarr never asks GitHub for announcements and the bell is hidden.</summary>
    public bool Enabled { get; set; } = true;
}

public sealed class StartupSettings
{
    public int CountdownSeconds { get; set; } = 10;

    /// <summary>When true, the tray host registers itself to launch at Windows login (a per-user
    /// registry Run-key entry - no admin rights needed). Applied by IStartupRegistrationService
    /// whenever settings are saved; no-op on non-Windows platforms.</summary>
    public bool RunAtLogin { get; set; } = false;
}

public sealed class ArrSettings
{
    public ArrServiceSettings Sonarr { get; set; } = new();
    public ArrServiceSettings Radarr { get; set; } = new();
}

public sealed class ArrServiceSettings
{
    public bool Enabled { get; set; } = false;
    public string Url { get; set; } = "";
    public string ApiKey { get; set; } = "";
}

public sealed class WebSettings
{
    /// <summary>Port the embedded web UI/API listens on, bound to all interfaces (0.0.0.0) so
    /// it's reachable from other devices on the LAN - the whole point of the web-first design.
    /// User-configurable from the settings page so it can be changed if it collides with
    /// something else already running on the machine. No authentication in v2.0.0 (matches
    /// Radarr/Sonarr's own default posture).</summary>
    public int Port { get; set; } = 1212;
}

/// <summary>Settings for the automatic backup feature (IBackupScheduler/IBackupService) - same
/// three fields Sonarr's own Backup settings page uses (folder, interval, retention), Compressarr
/// always runs this on a background loop once the app starts, no separate enable/disable switch,
/// matching Sonarr's own always-on posture.</summary>
public sealed class BackupSettings
{
    /// <summary>Local or UNC path. Accepts %CompressarrAppData% and other %VAR% tokens, expanded
    /// via IPathExpander at point of use - same convention as LoggingSettings.LogFilePath/
    /// ReportSettings.ReportPath.</summary>
    public string FolderPath { get; set; } = "%CompressarrAppData%\\Backups";

    public int IntervalDays { get; set; } = 7;
    public int RetentionDays { get; set; } = 28;

    /// <summary>Set by BackupService after each successful backup (scheduled or manual) - drives
    /// both "is a backup due yet" for IBackupScheduler and the "Last backup" display on Settings.</summary>
    public DateTimeOffset? LastRunUtc { get; set; }
}

/// <summary>When a configured notification channel actually fires - same Always/OnError/Never
/// vocabulary as ReportSettings.OpenAfterRun, rather than inventing new terms for the same idea.
/// OnError means the run had any error OR warning (NotificationOutcome != Success), not strictly
/// errors-only - a channel set to OnError still fires for a run that succeeded with a warning.</summary>
public enum NotificationTrigger
{
    Always,
    OnError,
    Never
}

/// <summary>Which Title/Body pair a per-run completion notification uses across the pluggable
/// Channels (Discord, Slack, Telegram, Pushover, ntfy, Gotify, Notifiarr) - deliberately NOT
/// consulted by the desktop toast (own fixed multi-line layout, local-only, the user is already at
/// the machine) or the Generic Webhook (its JSON body is structured data for another program to
/// read, not prose for a person). Custom means CustomTitleTemplate/CustomBodyTemplate below are
/// used instead of one of NotificationMessagePresets' built-in pairs.</summary>
public enum NotificationMessageStyle
{
    Minimal,
    Standard,
    Detailed,
    Custom
}

public sealed class NotificationSettings
{
    /// <summary>Gates the existing local-OS toast (INotificationService/WindowsNotificationService)
    /// - defaults off, since a headless/server install has no desktop to show one on, unlike the
    /// pluggable Channels below which are opt-in-by-adding-one anyway. A desktop user who wants
    /// toasts back turns this on from the first card on the Notifications page.</summary>
    public bool ToastEnabled { get; set; } = false;

    public NotificationMessageStyle MessageStyle { get; set; } = NotificationMessageStyle.Standard;

    /// <summary>Only read when MessageStyle == Custom - ignored otherwise, so switching back to a
    /// built-in style doesn't lose whatever the user last typed here. Same token vocabulary as the
    /// built-in presets (see NotificationMessagePresets/NotificationMessageRenderer).</summary>
    public string CustomTitleTemplate { get; set; } = "Compressarr run #{run_number} - {outcome}";
    public string CustomBodyTemplate { get; set; } = "{files} file(s) processed, {saved_gb} GB saved.";

    // Digest settings for the toast itself - toast has no Trigger/channel concept, so its own
    // digest schedule lives directly here rather than on a NotificationChannel. Same 7-field shape
    // as NotificationChannel's own digest fields below, see those doc comments for the firing rule.
    public bool ToastDigestDailyEnabled { get; set; } = false;
    public bool ToastDigestWeeklyEnabled { get; set; } = false;
    public string ToastDigestDailyTime { get; set; } = "09:00";
    public string ToastDigestWeeklyTime { get; set; } = "09:00";
    public DayOfWeek ToastDigestWeeklyDay { get; set; } = DayOfWeek.Monday;
    public DateOnly? ToastLastDailyDigestSentDate { get; set; }
    public DateOnly? ToastLastWeeklyDigestSentDate { get; set; }

    public List<NotificationChannel> Channels { get; set; } = new();
}

/// <summary>One configured destination (a Discord webhook, a Pushover account, etc). Settings is
/// a plain string dictionary rather than one C# type per channel Type, so a new channel type
/// (INotifier.Type) never needs a config-shape change - only the matching INotifier's own Fields
/// schema needs to know what keys it expects.</summary>
public sealed class NotificationChannel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public NotificationTrigger Trigger { get; set; } = NotificationTrigger.Always;
    public Dictionary<string, string> Settings { get; set; } = new();

    // Digest: an independent periodic summary, on top of (not instead of) Trigger above - a
    // channel with Trigger=Never and a digest enabled is "digest-only"; Trigger=Always plus a
    // digest gets both. DigestDailyTime/DigestWeeklyTime are each HH:mm, local time, and fully
    // independent - Weekly no longer inherits Daily's time (an earlier design; changed after the
    // user pointed out the digest row has room for its own selector).
    public bool DigestDailyEnabled { get; set; } = false;
    public bool DigestWeeklyEnabled { get; set; } = false;
    public string DigestDailyTime { get; set; } = "09:00";
    public string DigestWeeklyTime { get; set; } = "09:00";
    public DayOfWeek DigestWeeklyDay { get; set; } = DayOfWeek.Monday;

    /// <summary>Scheduler-internal dedupe stamp, not user-editable - excluded from
    /// NotificationChannelDto entirely so a Settings save can never accidentally reset it.</summary>
    public DateOnly? LastDailyDigestSentDate { get; set; }
    public DateOnly? LastWeeklyDigestSentDate { get; set; }
}

/// <summary>Process priority an encode can run at. Maps to the OS process priority classes
/// (Low = Windows "Idle"). Realtime needs administrator rights; without them Windows quietly treats
/// it as High - and either can make the PC sluggish for whoever is using it, which is why they're
/// only ever chosen deliberately.</summary>
public enum EncodePriority
{
    Low,
    BelowNormal,
    Normal,
    AboveNormal,
    High,
    Realtime
}

/// <summary>What "off-hours only" does to an encode that is already running when the daytime
/// window begins. FinishCurrentFile never interrupts it - the queue just stops starting new files.
/// SuspendEncode freezes it (the same Pause the Monitor page's Pause button uses) until off-hours
/// return.</summary>
public enum DayHoldBehavior
{
    FinishCurrentFile,
    SuspendEncode
}

/// <summary>How the daytime window is laid out across the week.</summary>
public enum ScheduleMode
{
    Everyday,
    WeekdaysAndWeekends,
    EachDay
}

/// <summary>One day's daytime window (HH:mm local time). Start equal to end means no daytime window that day.</summary>
public sealed class ScheduleDayWindow
{
    public string Start { get; set; } = "08:00";
    public string End { get; set; } = "22:00";
}

/// <summary>Optional day/night encoding schedule - OFF by default, so an install that never touches
/// it behaves exactly as before. Inside the daytime window encodes run at DayPriority (and, with
/// OnlyEncodeOffHours, don't run at all); outside it they run at NightPriority. All times are the
/// PC's local clock, so daylight saving follows automatically. A window may cross midnight (e.g.
/// 22:00 to 06:00); a window whose start equals its end means "no daytime window", i.e. always night.
/// A window belongs to the day it STARTS on.</summary>
public sealed class ScheduleSettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>Which of the three window layouts is in use: one window for every day, one for weekdays and
    /// one for weekends, or a window for each day of the week.</summary>
    public ScheduleMode Mode { get; set; } = ScheduleMode.Everyday;

    /// <summary>HH:mm. The window for Everyday mode, and the Monday-Friday window in
    /// WeekdaysAndWeekends mode.</summary>
    public string DayStart { get; set; } = "08:00";
    public string DayEnd { get; set; } = "22:00";

    /// <summary>The Saturday-Sunday window in WeekdaysAndWeekends mode.</summary>
    public string WeekendDayStart { get; set; } = "10:00";
    public string WeekendDayEnd { get; set; } = "20:00";

    /// <summary>EachDay mode: seven windows, indexed by DayOfWeek (0 = Sunday ... 6 = Saturday).</summary>
    public List<ScheduleDayWindow> Days { get; set; } = Enumerable.Range(0, 7).Select(_ => new ScheduleDayWindow()).ToList();

    public EncodePriority DayPriority { get; set; } = EncodePriority.BelowNormal;
    public EncodePriority NightPriority { get; set; } = EncodePriority.Normal;

    /// <summary>Hold the queue (start no new files) during the daytime window.</summary>
    public bool OnlyEncodeOffHours { get; set; } = false;

    public DayHoldBehavior WhenDayStarts { get; set; } = DayHoldBehavior.FinishCurrentFile;
}
