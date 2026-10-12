using System.Text.Json.Nodes;

namespace Compressarr.Core.Announcements;

/// <summary>One announcement, already checked: the reader only produces these from entries that follow every rule, so nothing
/// downstream has to re-validate a field.</summary>
public sealed record Announcement(
    string Id, DateOnly Date, string Kind, string Title, string Text, string? Link, bool Important,
    string? MinAppVersion, string? MaxAppVersion, DateOnly? Expires);

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

        return new Announcement(id, date, kind, title, text, link, entry["important"] is JsonValue v && v.TryGetValue<bool>(out var b) && b,
            string.IsNullOrEmpty(minVersion) ? null : minVersion, string.IsNullOrEmpty(maxVersion) ? null : maxVersion, expires);
    }

    /// <summary>https only, and only the Compressarr GitHub page (github.com/MrWizardCT) or compressarr.tv.</summary>
    public static bool LinkAllowed(string? link)
    {
        if (string.IsNullOrWhiteSpace(link) || !Uri.TryCreate(link, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) return false;

        var host = uri.Host.ToLowerInvariant();
        if (host == "github.com")
        {
            return uri.AbsolutePath.Equals("/MrWizardCT", StringComparison.Ordinal)
                || uri.AbsolutePath.StartsWith("/MrWizardCT/", StringComparison.Ordinal);
        }

        return host == "compressarr.tv" || host.EndsWith(".compressarr.tv", StringComparison.Ordinal);
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static bool TryDate(JsonNode? node, out DateOnly date)
    {
        date = default;
        var text = Str(node)?.Trim();
        return text is { Length: 10 } && DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out date);
    }
}

/// <summary>A Compressarr version as announcements compare them: the numbers, and a pre-release tag that sorts below the release
/// ("2.2.0-beta.5" is older than "2.2.0"). Build metadata after '+' is ignored.</summary>
public sealed class AppVersion : IComparable<AppVersion>
{
    private readonly int[] _numbers;
    private readonly string[] _pre;

    private AppVersion(int[] numbers, string[] pre) { _numbers = numbers; _pre = pre; }

    public static AppVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var core = text.Trim().TrimStart('v', 'V');
        var plus = core.IndexOf('+');
        if (plus >= 0) core = core[..plus];

        var dash = core.IndexOf('-');
        var numbersText = dash >= 0 ? core[..dash] : core;
        var preText = dash >= 0 ? core[(dash + 1)..] : "";

        var parts = numbersText.Split('.');
        if (parts.Length is < 2 or > 4) return null;
        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out numbers[i])) return null;
        }

        if (dash >= 0 && preText.Length == 0) return null; // "2.2.0-" is not a version
        var pre = preText.Length == 0 ? Array.Empty<string>() : preText.Split('.');
        if (pre.Any(string.IsNullOrEmpty)) return null;
        return new AppVersion(numbers, pre);
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < 4; i++)
        {
            var c = _numbers[i].CompareTo(other._numbers[i]);
            if (c != 0) return c;
        }

        // Same numbers: a release is above any of its pre-releases.
        if (_pre.Length == 0 || other._pre.Length == 0) return other._pre.Length.CompareTo(_pre.Length);

        for (var i = 0; i < Math.Min(_pre.Length, other._pre.Length); i++)
        {
            var aIsNumber = int.TryParse(_pre[i], out var a);
            var bIsNumber = int.TryParse(other._pre[i], out var b);
            int c;
            if (aIsNumber && bIsNumber) c = a.CompareTo(b);
            else if (aIsNumber) c = -1; // a number is below a word, as in semantic versioning
            else if (bIsNumber) c = 1;
            else c = string.CompareOrdinal(_pre[i], other._pre[i]);
            if (c != 0) return c;
        }

        return _pre.Length.CompareTo(other._pre.Length);
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
    public static bool InWindow(Announcement a, DateOnly today) =>
        a.Date <= today
        && today.DayNumber - a.Date.DayNumber <= WindowDays
        && (a.Expires is null || today <= a.Expires);
}
