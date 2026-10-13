namespace Compressarr.Web.Dtos;

public sealed record NotificationSettingsDto(
    bool ToastEnabled,
    bool ToastDigestDailyEnabled = false,
    bool ToastDigestWeeklyEnabled = false,
    string ToastDigestDailyTime = "09:00",
    string ToastDigestWeeklyTime = "09:00",
    string ToastDigestWeeklyDay = "Monday",
    string MessageStyle = "Standard",
    string CustomTitleTemplate = "",
    string CustomBodyTemplate = "",
    bool IncludeMachineName = false,
    string MachineName = "");

/// <summary>One built-in NotificationMessageStyle's canned Title/Body pair, as sent to the browser
/// so the Notifications page can preview it without hardcoding the actual wording twice (once in
/// NotificationMessagePresets, once in notifications.js) - the server stays the single source of
/// truth for what each style actually says.</summary>
public sealed record MessagePresetDto(string Title, string Body);

public sealed record NotifierFieldDto(string Key, string Label, string InputType, bool Required, bool Secret, IReadOnlyList<string>? Options, string? HelpText, string? Placeholder);

public sealed record NotifierTypeDto(string Type, string DisplayName, IReadOnlyList<NotifierFieldDto> Fields);

public sealed record NotificationChannelDto(
    string Id,
    string Type,
    string DisplayName,
    string Trigger,
    Dictionary<string, string> Settings,
    bool DigestDailyEnabled = false,
    bool DigestWeeklyEnabled = false,
    string DigestDailyTime = "09:00",
    string DigestWeeklyTime = "09:00",
    string DigestWeeklyDay = "Monday");

public sealed record CreateChannelRequest(string Type);

public sealed record TestNotifierRequest(string Type, Dictionary<string, string> Settings);

/// <summary>Weekly picks which of Daily/Weekly's own window+label the digest-test endpoints use -
/// the client sends one call per digest type currently checked on that card/toast's own row, so
/// testing with both Daily and Weekly enabled produces two distinctly-labeled test sends, not two
/// identical ones.</summary>
public sealed record DigestTestRequest(string Type, Dictionary<string, string> Settings, bool Weekly = false);

public sealed record DigestTestToastRequest(bool Weekly = false);
