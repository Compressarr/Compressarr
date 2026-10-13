namespace Compressarr.Core.Notifications;

/// <summary>Describes one configurable field a channel type needs (a webhook URL, a bot token, a
/// chat ID, etc.) - the web UI fetches these via GET /api/notifications/types and renders whatever
/// each registered INotifier declares, so adding a new channel type never needs new frontend
/// code. InputType is "text"/"password"/"textarea"/"select" - Options is only meaningful (and
/// required) for "select". HelpText drives a hover/focus help bubble next to the field's label.
/// Placeholder is example-format grey hint text shown inside an empty field - use it for anything
/// whose exact shape is per-user/per-account (a Discord webhook URL, a self-hosted server address)
/// where there's no real value to pre-fill. DefaultValue is different: a REAL starting value
/// applied when a new channel of this type is created, appropriate only for a field with a genuine
/// shared public default (e.g. ntfy's Server field defaulting to the public ntfy.sh instance) -
/// never use it for something that merely looks similar across users but isn't actually shared.</summary>
public sealed record NotifierField(
    string Key,
    string Label,
    string InputType,
    bool Required,
    bool Secret = false,
    IReadOnlyList<string>? Options = null,
    string? HelpText = null,
    string? Placeholder = null,
    string? DefaultValue = null);

public sealed record NotifyResult(bool Success, string Message);

public enum NotificationOutcome
{
    Success,
    Warning,
    Error
}

/// <summary>What actually happened this run, in a form every notifier's SendAsync can lay out
/// however suits its own channel - deliberately structured rather than a pre-formatted string,
/// same reasoning INotificationService.RunCompletionSummary already uses.</summary>
public sealed record NotificationEvent(
    NotificationOutcome Outcome,
    string Title,
    string Body,
    int TotalFiles,
    double SavedGb,
    TimeSpan Duration,
    string? ReportPath,
    string? MachineName = null,
    bool PrefixMachineName = true)
{
    /// <summary>What a person-facing channel shows as the title: "[Machine] Title" when a machine
    /// name is set, otherwise the title as is. A Custom template places {machine} itself, so it
    /// sets PrefixMachineName false to avoid showing the name twice. The Generic Webhook ignores
    /// this and reports Title unchanged plus MachineName as its own field.</summary>
    public string DisplayTitle => PrefixMachineName ? MachineLabel.Prefix(Title, MachineName) : Title;
}

/// <summary>One pluggable notification channel type (Discord, Slack, a generic webhook, etc).
/// Each implementation is stateless and registered as a singleton in DI, resolved as
/// IEnumerable&lt;INotifier&gt; by NotificationDispatcher and indexed by Type against whichever
/// channels the user has configured.</summary>
public interface INotifier
{
    /// <summary>Stable id matching NotificationChannel.Type in config - "webhook", "discord", etc.
    /// Never shown to the user directly; DisplayName is.</summary>
    string Type { get; }

    string DisplayName { get; }

    /// <summary>The config fields this channel type needs, driving both the "Add Channel" field
    /// picker and each existing channel's own edit form.</summary>
    IReadOnlyList<NotifierField> Fields { get; }

    /// <summary>Sends a real notification for a completed run. Must never throw - a failed/
    /// misconfigured channel must never fail an otherwise-successful run or block another
    /// channel; NotificationDispatcher treats any thrown exception as a failed NotifyResult
    /// anyway, but implementations should still catch and report a real message where possible.</summary>
    Task<NotifyResult> SendAsync(IReadOnlyDictionary<string, string> settings, NotificationEvent evt, CancellationToken ct);

    /// <summary>Sends a short test message using whatever's currently typed into the channel's
    /// fields, even if not yet saved - same "test what's on screen, not what's on disk" behavior
    /// /api/arr/test already has for Sonarr/Radarr. machineName is the cleaned name to show in the
    /// test message's title (or null for none), so the test previews what a real one will look like.</summary>
    Task<NotifyResult> TestAsync(IReadOnlyDictionary<string, string> settings, CancellationToken ct, string? machineName = null);
}
