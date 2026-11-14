using System.Text;
using System.Text.RegularExpressions;
using Compressarr.Core.Conversion;
using Compressarr.Core.Posters;

namespace Compressarr.Core.Queue;

/// <summary>Whether a rule moves a show's files toward the front or the back of the queue. A file no rule matches is "normal".</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum QueuePriority
{
    Lower,
    Higher
}

/// <summary>One queue-priority rule (3.1): files whose show or movie title contains <see cref="Match"/>, or that carry the TMDb id <see cref="TmdbId"/> in their name
/// or folder (<c>{tmdb-123}</c>, <c>[tmdbid-123]</c>), are placed higher or lower in the queue when they first arrive.</summary>
public sealed class PriorityRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Text the show or movie title must contain (ignoring case and punctuation). Blank when the rule matches by <see cref="TmdbId"/> only.</summary>
    public string Match { get; set; } = "";

    /// <summary>A TMDb id read from the file's name or folder names. Null when the rule matches by title only.</summary>
    public int? TmdbId { get; set; }

    public QueuePriority Priority { get; set; } = QueuePriority.Lower;
}

/// <summary>The rule list, saved with the configuration. Additive: a build without the feature ignores it and queues everything in arrival order.</summary>
public sealed class PrioritySettings
{
    public List<PriorityRule> Rules { get; set; } = new();
}

/// <summary>
/// Decides a file's tier from the rules: +1 (higher), 0 (normal), -1 (lower). The first rule in the list that matches wins. It works from the file's name and
/// folders only - nothing is looked up online, so it needs no TMDb key. Results are remembered per path, so asking about the same file many times in one pass is cheap.
/// </summary>
public sealed partial class QueuePriorityRules
{
    public static readonly QueuePriorityRules None = new(Array.Empty<PriorityRule>());

    private readonly IReadOnlyList<(string Match, int? Tmdb, int Tier)> _rules;
    private readonly Dictionary<string, int> _tiers = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"[\[\{\(]\s*tmdb(?:id)?\s*[-=]\s*(?<id>\d+)\s*[\]\}\)]", RegexOptions.IgnoreCase)]
    private static partial Regex TmdbTag();

    public QueuePriorityRules(IEnumerable<PriorityRule>? rules)
    {
        _rules = (rules ?? Array.Empty<PriorityRule>())
            .Select(r => (Match: Normalize(r.Match), r.TmdbId, Tier: r.Priority == QueuePriority.Higher ? 1 : -1))
            .Where(r => r.Match.Length > 0 || r.TmdbId.HasValue)
            .ToList();
    }

    /// <summary>True when there is nothing to apply, so every file is normal and callers can keep the plain "append at the end" behaviour.</summary>
    public bool IsEmpty => _rules.Count == 0;

    public int TierOf(string fullName)
    {
        if (IsEmpty) return 0;
        lock (_tiers)
        {
            if (_tiers.TryGetValue(fullName, out var cached)) return cached;
            return _tiers[fullName] = Compute(fullName);
        }
    }

    private int Compute(string fullName)
    {
        var fileName = Path.GetFileName(fullName);
        var title = Normalize(PosterNameParser.Parse(fileName)?.Title ?? Path.GetFileNameWithoutExtension(fileName));
        var tags = TmdbIdsIn(fullName).ToHashSet();

        foreach (var (match, tmdb, tier) in _rules)
        {
            if (match.Length > 0 && title.Contains(match, StringComparison.Ordinal)) return tier;
            if (tmdb is { } id && tags.Contains(id)) return tier;
        }
        return 0;
    }

    /// <summary>Lower-case letters and digits only, so "Doctor Who", "doctor.who" and "Doctor-Who!" are the same text.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>Whether this one rule matches the file (whatever its priority), for removing the rules that decide a file's tier.</summary>
    public static bool RuleMatches(PriorityRule rule, string fullName) =>
        new QueuePriorityRules(new[] { rule }).TierOf(fullName) != 0;

    /// <summary>The title a queue row's file goes by, used to make a rule from it.</summary>
    public static string TitleOf(string fileName) =>
        PosterNameParser.Parse(fileName)?.Title ?? Path.GetFileNameWithoutExtension(fileName);

    /// <summary>The first TMDb id tagged in a path, or null.</summary>
    public static int? TmdbIdIn(string fullName) => TmdbIdsIn(fullName).Cast<int?>().FirstOrDefault();

    /// <summary>Every TMDb id tagged in a path. A number too large to be an id (a name with {tmdb-99999999999} in it) is not one: it is ignored, never an error - this runs for every queued
    /// file, and one odd name must not stop the queue from being prepared or shown.</summary>
    private static IEnumerable<int> TmdbIdsIn(string fullName)
    {
        foreach (Match m in TmdbTag().Matches(fullName))
            if (int.TryParse(m.Groups["id"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)) yield return id;
    }
}
