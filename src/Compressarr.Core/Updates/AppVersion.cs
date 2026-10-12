namespace Compressarr.Core.Updates;

/// <summary>A Compressarr version as announcements and the update check compare them: the numbers, and a pre-release tag that sorts below the release
/// ("2.2.0-beta.5" is older than "2.2.0"). Build metadata after '+' is ignored.</summary>
public sealed class AppVersion : IComparable<AppVersion>
{
    private readonly int[] _numbers;
    private readonly string[] _pre;

    private AppVersion(int[] numbers, string[] pre) { _numbers = numbers; _pre = pre; }

    /// <summary>The running version including any pre-release tag ("2.2.0-beta.5"), which the numeric assembly version drops.</summary>
    public static AppVersion? Installed()
    {
        var assembly = typeof(AppVersion).Assembly;
        var informational = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .Select(a => a.InformationalVersion)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        return Parse(informational) ?? Parse(assembly.GetName().Version?.ToString());
    }

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
