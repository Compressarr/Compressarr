using Compressarr.Core.Config;
using Compressarr.Core.Conversion;
using Compressarr.Core.Logging;
using Microsoft.Data.Sqlite;

namespace Compressarr.Core.Renaming;

/// <summary>What the renamer (or the queue's Match / Fix match) did to one file: where it came from, what it is called now, and what it was matched to. <paramref name="TmdbId"/> is null for a rename FileBot
/// made (only the names are known). <paramref name="Source"/> is "tmdb" (the automatic renamer), "manual" (a person chose the match) or "filebot".</summary>
public sealed record RenameRecord(string OriginalPath, string CurrentPath, int? TmdbId, bool IsTv, int? Season, IReadOnlyList<int> Episodes, string Source)
{
    public const string AutomaticSource = "tmdb";
    public const string ManualSource = "manual";
    public const string FileBotSource = "filebot";

    /// <summary>The name was put back by hand: the file is as it first was, and the automatic renamer leaves it alone. Not shown as renamed.</summary>
    public const string RestoredSource = "restored";

    /// <summary>A name the automatic renamer must not touch again: a person chose it, or put the original back.</summary>
    public bool IsHandled => Source is ManualSource or RestoredSource;
}

/// <summary>A match a person chose by hand, remembered so the rest of that show (and its next episodes) follow it instead of being guessed again.</summary>
public sealed record RememberedMatch(int TmdbId, bool IsTv, string Title, int? Year);

/// <summary>The record of renames and of hand-picked matches (3.2). It is what lets a wrong match be put right ("Fix match") or undone ("Restore original name"), and what keeps a hand-picked match
/// from being overruled by the automatic renamer. Best effort: when the queue database cannot be opened nothing is recorded and nothing is found, and the renamer carries on without it.</summary>
public interface IRenameLog
{
    /// <summary>Notes that <paramref name="originalPath"/> is now <paramref name="newPath"/>. When the file was renamed before (the old path is the one on record) the same row moves to the new name and
    /// keeps the name the file first had, so "restore" always goes back to the very beginning.</summary>
    void Record(string originalPath, string newPath, int? tmdbId, bool isTv, int? season, IReadOnlyList<int>? episodes, string source);

    /// <summary>The record of the file that is called <paramref name="currentPath"/> now, or null when nothing renamed it.</summary>
    RenameRecord? Find(string currentPath);

    /// <summary>Drops the record of the file called <paramref name="currentPath"/> (it was restored to its first name).</summary>
    void Forget(string currentPath);

    /// <summary>The present path (upper case) of every file that was renamed and could be fixed (a file put back to its first name is not one), for the queue to show which rows can be.</summary>
    IReadOnlySet<string> CurrentPaths();

    /// <summary>Drops a remembered match (the choice was undone).</summary>
    void ForgetMemory(string memoryKey);

    RememberedMatch? Recall(string memoryKey);

    void Remember(string memoryKey, RememberedMatch match);
}

/// <summary>The memory key for what a file name says it is: the same show or film written the same way gives the same key.</summary>
public static class RenameMemoryKey
{
    public static string For(ParsedName name) =>
        $"{(name.IsTv ? "tv" : "movie")}|{TmdbMatcher.Key(name.Title, dropThe: false)}|{name.Year}|{name.Region}";
}

public sealed class SqliteRenameLog : IRenameLog
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(120);

    private readonly SqliteResumeStateStore _store;
    private readonly Func<string> _resumeFilePath;
    private readonly object _lock = new();
    private Dictionary<string, RenameRecord>? _byKey;
    private Dictionary<string, RememberedMatch>? _memory;

    public SqliteRenameLog(SqliteResumeStateStore store) : this(store, AppPaths.GetResumeFilePath) { }

    internal SqliteRenameLog(SqliteResumeStateStore store, Func<string> resumeFilePath)
    {
        _store = store;
        _resumeFilePath = resumeFilePath;
    }

    private static string KeyOf(string path) => Path.GetFullPath(path).ToUpperInvariant();

    public void Record(string originalPath, string newPath, int? tmdbId, bool isTv, int? season, IReadOnlyList<int>? episodes, string source)
    {
        lock (_lock)
        {
            try
            {
                using var connection = _store.OpenReady(_resumeFilePath());
                if (connection is null) return;

                using var transaction = connection.BeginTransaction();
                var oldKey = KeyOf(originalPath);
                var newKey = KeyOf(newPath);
                var now = HistoryDatabase.Iso(DateTime.UtcNow);

                // Renamed before: the same row moves on and keeps the name the file first had.
                string? firstName = null;
                using (var find = connection.CreateCommand())
                {
                    find.Transaction = transaction;
                    find.CommandText = "SELECT original_path FROM rename_log WHERE current_key = @key;";
                    find.Parameters.AddWithValue("@key", oldKey);
                    firstName = find.ExecuteScalar() as string;
                }

                Execute(connection, transaction, "DELETE FROM rename_log WHERE current_key = @a OR current_key = @b;", ("@a", oldKey), ("@b", newKey));
                Execute(connection, transaction,
                    "INSERT INTO rename_log (current_key, original_path, current_path, tmdb_id, is_tv, season, episodes, source, created_at, updated_at) VALUES (@key, @original, @current, @tmdb, @tv, @season, @episodes, @source, @now, @now);",
                    ("@key", newKey), ("@original", firstName ?? originalPath), ("@current", newPath), ("@tmdb", (object?)tmdbId ?? DBNull.Value), ("@tv", isTv ? 1 : 0),
                    ("@season", (object?)season ?? DBNull.Value), ("@episodes", episodes is { Count: > 0 } ? string.Join(',', episodes) : DBNull.Value), ("@source", source), ("@now", now));
                Execute(connection, transaction, "DELETE FROM rename_log WHERE updated_at < @cutoff;", ("@cutoff", HistoryDatabase.Iso(DateTime.UtcNow - KeepFor)));
                transaction.Commit();
                _byKey = null;
            }
            catch (SqliteException)
            {
                // Best effort: a rename that could not be recorded is simply not fixable later.
            }
        }
    }

    public RenameRecord? Find(string currentPath)
    {
        lock (_lock)
        {
            var all = Load();
            return all.TryGetValue(KeyOf(currentPath), out var record) ? record : null;
        }
    }

    public void Forget(string currentPath)
    {
        lock (_lock)
        {
            try
            {
                using var connection = _store.OpenReady(_resumeFilePath());
                if (connection is null) return;

                Execute(connection, null, "DELETE FROM rename_log WHERE current_key = @key;", ("@key", KeyOf(currentPath)));
                _byKey = null;
            }
            catch (SqliteException)
            {
            }
        }
    }

    public IReadOnlySet<string> CurrentPaths()
    {
        lock (_lock) return Load().Where(r => r.Value.Source != RenameRecord.RestoredSource).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
    }

    public void ForgetMemory(string memoryKey)
    {
        lock (_lock)
        {
            try
            {
                using var connection = _store.OpenReady(_resumeFilePath());
                if (connection is null) return;

                Execute(connection, null, "DELETE FROM rename_memory WHERE key = @key;", ("@key", memoryKey));
                _memory = null;
            }
            catch (SqliteException)
            {
            }
        }
    }

    public RememberedMatch? Recall(string memoryKey)
    {
        lock (_lock)
        {
            return LoadMemory().TryGetValue(memoryKey, out var match) ? match : null;
        }
    }

    public void Remember(string memoryKey, RememberedMatch match)
    {
        lock (_lock)
        {
            try
            {
                using var connection = _store.OpenReady(_resumeFilePath());
                if (connection is null) return;

                Execute(connection, null,
                    "INSERT INTO rename_memory (key, tmdb_id, is_tv, title, year, updated_at) VALUES (@key, @id, @tv, @title, @year, @now) ON CONFLICT (key) DO UPDATE SET tmdb_id = excluded.tmdb_id, is_tv = excluded.is_tv, title = excluded.title, year = excluded.year, updated_at = excluded.updated_at;",
                    ("@key", memoryKey), ("@id", match.TmdbId), ("@tv", match.IsTv ? 1 : 0), ("@title", match.Title), ("@year", (object?)match.Year ?? DBNull.Value), ("@now", HistoryDatabase.Iso(DateTime.UtcNow)));
                _memory = null;
            }
            catch (SqliteException)
            {
            }
        }
    }

    private Dictionary<string, RenameRecord> Load()
    {
        if (_byKey is not null) return _byKey;

        var all = new Dictionary<string, RenameRecord>(StringComparer.Ordinal);
        var read = false;
        try
        {
            using var connection = _store.OpenReady(_resumeFilePath());
            if (connection is not null)
            {
                read = true;
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT current_key, original_path, current_path, tmdb_id, is_tv, season, episodes, source FROM rename_log;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var episodes = reader.IsDBNull(6) ? Array.Empty<int>() : reader.GetString(6).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                    all[reader.GetString(0)] = new RenameRecord(reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.GetInt32(4) != 0,
                        reader.IsDBNull(5) ? null : reader.GetInt32(5), episodes, reader.GetString(7));
                }
            }
        }
        catch (SqliteException)
        {
            read = false;      // nothing on record is better than a failed poll, and it is asked again next time
        }

        return read ? _byKey = all : all;
    }

    private Dictionary<string, RememberedMatch> LoadMemory()
    {
        if (_memory is not null) return _memory;

        var all = new Dictionary<string, RememberedMatch>(StringComparer.Ordinal);
        var read = false;
        try
        {
            using var connection = _store.OpenReady(_resumeFilePath());
            if (connection is not null)
            {
                read = true;
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT key, tmdb_id, is_tv, title, year FROM rename_memory;";
                using var reader = command.ExecuteReader();
                while (reader.Read()) all[reader.GetString(0)] = new RememberedMatch(reader.GetInt32(1), reader.GetInt32(2) != 0, reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetInt32(4));
            }
        }
        catch (SqliteException)
        {
            read = false;
        }

        return read ? _memory = all : all;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
}
