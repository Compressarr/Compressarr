using System.Text;
using Compressarr.Core.Config;

namespace Compressarr.Core.Notifications;

/// <summary>The optional "machine name" that tells notifications apart when several Compressarr
/// installs report to the same place. Off by default (NotificationSettings.IncludeMachineName), and
/// never taken from the computer on its own - a name only leaves the machine after the user ticked
/// the box and saved a name (the settings page pre-fills the field with the computer name as a
/// convenience, but that is still an explicit choice). One place cleans the text, so every notifier,
/// digest and the {machine} template token see the same value.</summary>
public static class MachineLabel
{
    public const int MaxLength = 40;

    /// <summary>Trimmed, control characters dropped, runs of whitespace collapsed, square brackets
    /// removed (the title prefix is [Name], so a bracket inside would read as two), capped at
    /// MaxLength. Empty when nothing usable is left.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var c in text)
        {
            if (c == '[' || c == ']') continue;
            if (char.IsWhiteSpace(c))
            {
                if (builder.Length > 0 && !lastWasSpace) builder.Append(' ');
                lastWasSpace = true;
                continue;
            }
            if (char.IsControl(c)) continue;
            builder.Append(c);
            lastWasSpace = false;
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length > MaxLength ? cleaned[..MaxLength].TrimEnd() : cleaned;
    }

    /// <summary>The name to put in notifications right now: the cleaned name when the box is
    /// ticked, otherwise null. Ticked with a blank name is also null - nothing is added, and the
    /// computer's own name is never substituted.</summary>
    public static string? For(NotificationSettings settings)
    {
        if (!settings.IncludeMachineName) return null;
        var cleaned = Clean(settings.MachineName);
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>"[Name] Title", or the title unchanged when there is no name.</summary>
    public static string Prefix(string title, string? machineName) =>
        string.IsNullOrEmpty(machineName) ? title : $"[{machineName}] {title}";
}
