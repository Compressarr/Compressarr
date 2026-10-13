using System.Text.Json.Nodes;
using Compressarr.Core.Updates;

namespace Compressarr.Core.Announcements;

/// <summary>One announcement, already checked: the reader only produces these from entries that follow every rule, so nothing
/// downstream has to re-validate a field.</summary>
public sealed record Announcement(
    string Id, DateOnly Date, string Kind, string Title, string Text, string? Link, bool Important,
    string? MinAppVersion, string? MaxAppVersion, DateOnly? Expires, bool Welcome = false);

public enum AnnouncementFileStatus
{
    Ok,

    /// <summary>The file's format number is newer than this Compressarr understands: nothing in it is shown.</summary>
    UnknownSchema,

    /// <summary>Not JSON, over the size limit, or not shaped like an announcements file: nothing in it is shown.</summary>
    Invalid
}

public sealed record AnnouncementParseResult(AnnouncementFileStatus Status, IReadOnlyList<Announcement> Items);

/// <summary>
/// Reads announcements.json strictly. The file is untrusted text from the network: it is size-limited, an entry that breaks any rule is
/// skipped on its own (it never hides the good ones), text is only ever shown as plain text, and a link is kept only when it points at
/// the Compressarr GitHub page or compressarr.tv. The same rules are applied by the validator in the Compressarr-Announcements repo.
/// </summary>
public static class AnnouncementReader
{
    public const int SupportedSchema = 1;
    public const int MaxFileBytes = 64 * 1024;
    public const int MaxEntries = 50;
    public const int MaxIdLength = 80;
    public const int MaxTitleLength = 120;
    public const int MaxTextLength = 1000;

    /// <summary>The GitHub accounts whose pages a link may point at: the Compressarr organization, and the account the project moved from.</summary>
    private static readonly string[] GitHubOwners = { "Compressarr", "MrWizardCT" };

    private static readonly string[] Kinds = { "release", "beta", "catalog", "notice" };

    public static AnnouncementParseResult Parse(string? json)
    {
        var none = Array.Empty<Announcement>();
        if (string.IsNullOrWhiteSpace(json) || System.Text.Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
        {
            return new AnnouncementParseResult(AnnouncementFileStatus.Invalid, none);
        }

        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (System.Text.Json.JsonException) { return new AnnouncementParseResult(AnnouncementFileStatus.Invalid, none); }

        if (root is not JsonObject obj) return new AnnouncementParseResult(AnnouncementFileStatus.Invalid, none);

        var schema = Int(obj["schema"]);
        if (schema is null) return new AnnouncementParseResult(AnnouncementFileStatus.Invalid, none);
        if (schema != SupportedSchema) return new AnnouncementParseResult(AnnouncementFileStatus.UnknownSchema, none);
        if (obj["announcements"] is not JsonArray list) return new AnnouncementParseResult(AnnouncementFileStatus.Invalid, none);

        var items = new List<Announcement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in list.Take(MaxEntries))
        {
            if (node is not JsonObject entry) continue;
            var parsed = ParseEntry(entry);
            if (parsed is null || !seen.Add(parsed.Id)) continue; // a repeated id keeps the first one
            items.Add(parsed);
        }

        return new AnnouncementParseResult(AnnouncementFileStatus.Ok, items);
    }

    private static Announcement? ParseEntry(JsonObject entry)
    {
        var id = Str(entry["id"])?.Trim();
        var title = Str(entry["title"])?.Trim();
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength) return null;
        if (string.IsNullOrEmpty(title) || title.Length > MaxTitleLength) return null;
        if (!TryDate(entry["date"], out var date)) return null;

        var text = Str(entry["text"])?.Trim() ?? "";
        if (text.Length > MaxTextLength) return null;

        var kind = Str(entry["kind"])?.Trim().ToLowerInvariant() ?? "notice";
        if (!Kinds.Contains(kind)) return null;

        DateOnly? expires = null;
        if (entry["expires"] is { } e && Str(e) is { Length: > 0 })
        {
            if (!TryDate(e, out var exp)) return null;
            expires = exp;
        }

        var minVersion = Str(entry["minAppVersion"])?.Trim();
        var maxVersion = Str(entry["maxAppVersion"])?.Trim();
        if (!string.IsNullOrEmpty(minVersion) && AppVersion.Parse(minVersion) is null) return null;
        if (!string.IsNullOrEmpty(maxVersion) && AppVersion.Parse(maxVersion) is null) return null;

        // A link that is not allowed does not discard the message: it is shown as plain text without the link.
        var link = Str(entry["link"])?.Trim();
        if (!LinkAllowed(link)) link = null;

        return new Announcement(id, date, kind, title, text, link, Flag(entry["important"]),
            string.IsNullOrEmpty(minVersion) ? null : minVersion, string.IsNullOrEmpty(maxVersion) ? null : maxVersion, expires,
            Welcome: Flag(entry["welcome"]));
    }

    /// <summary>https only, and only the Compressarr GitHub page (github.com/Compressarr; github.com/MrWizardCT, where the project used to live,
    /// is still accepted so older announcements keep working) or compressarr.tv.</summary>
    public static bool LinkAllowed(string? link)
    {
        if (string.IsNullOrWhiteSpace(link) || !Uri.TryCreate(link, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) return false;

        var host = uri.Host.ToLowerInvariant();
        if (host == "github.com")
        {
            return GitHubOwners.Any(owner =>
                uri.AbsolutePath.Equals("/" + owner, StringComparison.Ordinal)
                || uri.AbsolutePath.StartsWith("/" + owner + "/", StringComparison.Ordinal));
        }

        return host == "compressarr.tv" || host.EndsWith(".compressarr.tv", StringComparison.Ordinal);
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Flag(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static bool TryDate(JsonNode? node, out DateOnly date)
    {
        date = default;
        var text = Str(node)?.Trim();
        return text is { Length: 10 } && DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date);
    }
}

/// <summary>Which announcements apply to this install and are still on the list.</summary>
public static class AnnouncementRules
{
    /// <summary>How long a message stays on the list, counted from its date.</summary>
    public const int WindowDays = 30;

    public static bool InVersionRange(Announcement a, AppVersion? current)
    {
        // An app version that cannot be read is shown everything rather than nothing.
        if (current is null) return true;
        if (a.MinAppVersion is not null && AppVersion.Parse(a.MinAppVersion) is { } min && current.CompareTo(min) < 0) return false;
        if (a.MaxAppVersion is not null && AppVersion.Parse(a.MaxAppVersion) is { } max && current.CompareTo(max) > 0) return false;
        return true;
    }

    /// <summary>On the list today: already dated (a future date is scheduled, not shown yet), within the window and not expired.</summary>
    public static bool InWindow(Announcement a, DateOnly today) => InWindow(a, today, keepPastWindow: false);

    /// <summary>As above, but keepPastWindow lets a message stay on the list after its window (still not before its date, and never past
    /// its own expiry). Used for the welcome message while it has not been read yet.</summary>
    public static bool InWindow(Announcement a, DateOnly today, bool keepPastWindow) =>
        a.Date <= today
        && (keepPastWindow || today.DayNumber - a.Date.DayNumber <= WindowDays)
        && (a.Expires is null || today <= a.Expires);

    /// <summary>The one welcome message that counts: the newest of those marked welcome that apply to this version and today. A file that
    /// marks several still only ever greets once; the others are ordinary messages.</summary>
    public static Announcement? CurrentWelcome(IEnumerable<Announcement> all, DateOnly today, AppVersion? current) =>
        all.Where(a => a.Welcome && InVersionRange(a, current) && InWindow(a, today, keepPastWindow: true))
           .OrderByDescending(a => a.Date)
           .FirstOrDefault();
}
