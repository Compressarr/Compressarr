using System.Text.Json;
using Compressarr.Core.Config;

namespace Compressarr.Core.Announcements;

/// <summary>What Compressarr remembers about announcements on this machine: which ones were read, the day it first looked (messages from
/// before that day are never "unread" - a new install is not greeted with old news), and the last good copy of the file so the bell
/// still shows after an offline start. Losing this file only means old messages can show as unread once.</summary>
public sealed class AnnouncementState
{
    public List<string> Read { get; set; } = new();

    /// <summary>The day of the first successful check (yyyy-MM-dd). Null until then.</summary>
    public string? Baseline { get; set; }

    public string? ETag { get; set; }

    /// <summary>The last announcements.json that parsed, kept as received.</summary>
    public string? CachedJson { get; set; }

    public DateTimeOffset? LastCheckedUtc { get; set; }

    /// <summary>True when the last file seen had a newer format than this version understands.</summary>
    public bool NeedsNewerApp { get; set; }
}

public interface IAnnouncementStateStore
{
    AnnouncementState Load();

    /// <summary>Loads, changes and saves under one lock, so a check and a "mark read" can't overwrite each other.</summary>
    T Update<T>(Func<AnnouncementState, T> change);
}

public sealed class JsonAnnouncementStateStore : IAnnouncementStateStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();

    public AnnouncementState Load()
    {
        lock (_gate) return LoadUnlocked();
    }

    public T Update<T>(Func<AnnouncementState, T> change)
    {
        lock (_gate)
        {
            var state = LoadUnlocked();
            var result = change(state);
            Save(state);
            return result;
        }
    }

    private static AnnouncementState LoadUnlocked()
    {
        var path = AppPaths.GetAnnouncementsFilePath();
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AnnouncementState>(File.ReadAllText(path), Options) ?? new AnnouncementState()
                : new AnnouncementState();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new AnnouncementState(); // a damaged file only costs the read marks
        }
    }

    private static void Save(AnnouncementState state)
    {
        var path = AppPaths.GetAnnouncementsFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
        File.Move(temp, path, overwrite: true);
    }
}
