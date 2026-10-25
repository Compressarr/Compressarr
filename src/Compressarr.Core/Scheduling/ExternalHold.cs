using System.Globalization;
using Compressarr.Core.Config;
using Compressarr.Core.Conversion;

namespace Compressarr.Core.Scheduling;

/// <summary>A hold placed from outside (the Automation API: Home Assistant holding the queue while the TV is on). While it stands no new file starts, whether or not the
/// Scheduler page is switched on. <see cref="Until"/> is when it lapses by itself (null = it stands until released). <see cref="SuspendCurrent"/> also freezes the
/// file being encoded; otherwise that file finishes.</summary>
public sealed record HoldInfo(string? Reason, DateTimeOffset Since, DateTimeOffset? Until, bool SuspendCurrent, string? SetBy);

/// <summary>The one external hold. Persisted, so a restart in the middle of movie night keeps it. See docs/design-api.md.</summary>
public interface IExternalHold
{
    /// <summary>The hold in force right now, or null. One that has passed its <see cref="HoldInfo.Until"/> is removed here and reads as none.</summary>
    HoldInfo? Current();

    /// <summary>Places the hold, or changes the one in force (its <see cref="HoldInfo.Since"/> is kept, so an automation that asserts the hold again every few
    /// minutes does not keep moving it).</summary>
    HoldInfo Set(string? reason, DateTimeOffset? until, bool suspendCurrent, string? setBy);

    /// <summary>Removes the hold. False when there was none.</summary>
    bool Release();
}

/// <summary>The hold in the queue's SQLite file (<c>compressarr.state.db</c>, table <c>holds</c>, always row 1). If that file cannot be opened the hold lives in
/// memory for the session, so an automation still works; a restart forgets it.</summary>
public sealed class SqliteExternalHold : IExternalHold
{
    private readonly SqliteResumeStateStore _store;
    private readonly Func<string> _resumeFilePath;
    private readonly TimeProvider _time;
    private readonly object _memoryLock = new();
    private HoldInfo? _memory;

    public SqliteExternalHold(SqliteResumeStateStore store) : this(store, AppPaths.GetResumeFilePath, TimeProvider.System) { }

    /// <summary>For tests that keep the state somewhere other than the app data folder, with their own clock.</summary>
    internal SqliteExternalHold(SqliteResumeStateStore store, Func<string> resumeFilePath, TimeProvider time)
    {
        _store = store;
        _resumeFilePath = resumeFilePath;
        _time = time;
    }

    public HoldInfo? Current()
    {
        var hold = Read();
        if (hold?.Until is { } until && until <= _time.GetUtcNow())
        {
            Delete();
            return null;
        }

        return hold;
    }

    public HoldInfo Set(string? reason, DateTimeOffset? until, bool suspendCurrent, string? setBy)
    {
        var existing = Current();
        var hold = new HoldInfo(string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(), existing?.Since ?? _time.GetUtcNow(), until, suspendCurrent, setBy);
        Write(hold);
        return hold;
    }

    public bool Release()
    {
        var had = Read() is not null;
        Delete();
        return had;
    }

    private HoldInfo? Read()
    {
        using var connection = _store.OpenReady(_resumeFilePath());
        if (connection is null)
        {
            lock (_memoryLock) return _memory;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT reason, since, until, suspend_current, set_by FROM holds WHERE id = 1;";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new HoldInfo(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            reader.GetInt32(3) != 0,
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private void Write(HoldInfo hold)
    {
        using var connection = _store.OpenReady(_resumeFilePath());
        if (connection is null)
        {
            lock (_memoryLock) _memory = hold;
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO holds (id, reason, since, until, suspend_current, set_by) VALUES (1, @reason, @since, @until, @suspend, @by)
            ON CONFLICT (id) DO UPDATE SET reason = excluded.reason, since = excluded.since, until = excluded.until,
                                           suspend_current = excluded.suspend_current, set_by = excluded.set_by;
            """;
        command.Parameters.AddWithValue("@reason", (object?)hold.Reason ?? DBNull.Value);
        command.Parameters.AddWithValue("@since", hold.Since.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@until", hold.Until is { } u ? u.ToString("O", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("@suspend", hold.SuspendCurrent ? 1 : 0);
        command.Parameters.AddWithValue("@by", (object?)hold.SetBy ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private void Delete()
    {
        using var connection = _store.OpenReady(_resumeFilePath());
        if (connection is null)
        {
            lock (_memoryLock) _memory = null;
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM holds WHERE id = 1;";
        command.ExecuteNonQuery();
    }
}
