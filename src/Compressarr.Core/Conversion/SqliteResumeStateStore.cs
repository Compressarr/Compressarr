using System.Globalization;
using System.Text.Json;
using Compressarr.Core.Logging;
using Microsoft.Data.Sqlite;

namespace Compressarr.Core.Conversion;

/// <summary>The queue and resume state in SQLite (3.x): the same <see cref="ResumeEntry"/> list the JSON file held, behind the same
/// <see cref="IResumeStateStore"/>, so no caller changes. <see cref="Update{T}"/> is one transaction: it loads the rows, runs the callback and writes
/// back only the rows the callback changed, so a crash leaves the old rows, two edits never lose one another and a second process is safe.
/// The first use with no database imports <c>compressarr.resume.json</c> (kept, renamed to .imported); a damaged database is set aside as .damaged
/// and the app starts with an empty queue; if the import fails for any other reason the JSON store keeps serving for the session and the file is left
/// exactly as it was. See docs/design-sqlite-queue.md.</summary>
public sealed class SqliteResumeStateStore : IResumeStateStore
{
    private const string ImportedKey = "resume_imported";
    private static readonly object InitLock = new();
    private static readonly HashSet<string> ReadyPaths = new(StringComparer.OrdinalIgnoreCase);

    // The ResumeEntry properties in the order of the columns below (a test checks that every property is here).
    internal static readonly string[] MappedProperties =
    {
        nameof(ResumeEntry.LaneId), nameof(ResumeEntry.FullName), nameof(ResumeEntry.Status), nameof(ResumeEntry.EncodedFilePath),
        nameof(ResumeEntry.EncodedFileDesiredName), nameof(ResumeEntry.PendingCompanionSourceDirectory), nameof(ResumeEntry.PendingCompanionVideoDestPath),
        nameof(ResumeEntry.LastRetryFailureMessage), nameof(ResumeEntry.Order), nameof(ResumeEntry.Skipped), nameof(ResumeEntry.PresetOverride),
        nameof(ResumeEntry.Removed), nameof(ResumeEntry.HeldAfterFailure), nameof(ResumeEntry.KeptSourceSize), nameof(ResumeEntry.KeptSourceModifiedUtc),
        nameof(ResumeEntry.CreatedByQueueEdit), nameof(ResumeEntry.FileBotUnmatched), nameof(ResumeEntry.DestinationLaneId)
    };

    private static readonly string[] Columns =
    {
        "lane_id", "full_name", "status", "encoded_file_path", "encoded_file_desired_name", "pending_companion_source_dir", "pending_companion_video_dest_path",
        "last_retry_failure_message", "position", "skipped", "preset_override", "removed", "held_after_failure", "kept_source_size", "kept_source_modified_utc",
        "created_by_queue_edit", "filebot_unmatched", "destination_lane_id"
    };

    private static readonly string SelectSql = "SELECT id, " + string.Join(", ", Columns) + " FROM queue ORDER BY id;";

    private static readonly string InsertSql =
        "INSERT INTO queue (key, " + string.Join(", ", Columns) + ", created_at, updated_at) VALUES (@key, " + string.Join(", ", Columns.Select((_, i) => "@p" + i)) + ", @now, @now) " +
        "ON CONFLICT (key) DO UPDATE SET " + string.Join(", ", Columns.Select(c => c + " = excluded." + c)) + ", updated_at = excluded.updated_at;";

    private static readonly string UpdateSql =
        "UPDATE OR REPLACE queue SET key = @key, " + string.Join(", ", Columns.Select((c, i) => c + " = @p" + i)) + ", updated_at = @now WHERE id = @id;";

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly JsonResumeStateStore _json = new();
    private string? _importFailure;
    private string? _notice;

    /// <summary>Why the queue could not be moved into the database, or null. While it is set the JSON store serves instead.</summary>
    public string? ImportFailure => _importFailure;

    /// <summary>Something the user should know about the queue that happened when the database was opened (a damaged database or JSON file was set
    /// aside), or null. The desktop app writes it to the log at start-up.</summary>
    public string? Notice => _notice;

    /// <summary>Opens (and, the first time, creates and imports into) the database now rather than on first use. Returns <see cref="Notice"/>.</summary>
    public string? Prepare(string resumeFilePath)
    {
        Ready(resumeFilePath);
        return _notice ?? (_importFailure is null ? null : $"The queue could not be opened in its database ({_importFailure}); the old queue file is used for now and nothing was removed.");
    }

    /// <summary>A connection to the state database, ready to use (created, checked, migrated, the old JSON file imported) - or null when it could not be
    /// opened and the JSON store is serving instead. For the other things kept in this file (see <see cref="Compressarr.Core.Config.IAppState"/>).</summary>
    internal Microsoft.Data.Sqlite.SqliteConnection? OpenReady(string resumeFilePath) =>
        Ready(resumeFilePath) ? HistoryDatabase.Open(StateDatabase.PathFor(resumeFilePath)) : null;

    // ---- IResumeStateStore ---------------------------------------------------------------------------------------------------------------

    public List<ResumeEntry> Load(string path)
    {
        if (!Ready(path)) return _json.Load(path);

        using var connection = HistoryDatabase.Open(StateDatabase.PathFor(path));
        return ReadAll(connection, withValues: false).Select(r => r.Entry).ToList();
    }

    public void Save(List<ResumeEntry> state, string path)
    {
        if (!Ready(path)) { _json.Save(state, path); return; }

        _lock.Wait();
        try
        {
            using var connection = HistoryDatabase.Open(StateDatabase.PathFor(path));
            Transact(connection, () =>
            {
                HistoryDatabase.Execute(connection, "DELETE FROM queue;");
                foreach (var entry in state) Insert(connection, entry);
            });
        }
        finally
        {
            _lock.Release();
        }
    }

    public void DeleteIfComplete(List<ResumeEntry> state, string path)
    {
        if (!Ready(path)) { _json.DeleteIfComplete(state, path); return; }

        var hasOutstanding = state.Any(e => e.Status is ResumeStatus.Pending or ResumeStatus.Error or ResumeStatus.MoveFailed or ResumeStatus.CompanionMoveFailed or ResumeStatus.CleanupPending);
        if (hasOutstanding) return;

        // Nothing left to do - but a converted file whose source was kept must stay remembered, or it is converted again.
        Save(state.Where(ResumeKeeping.StillNeeded).ToList(), path);
    }

    public T Update<T>(string path, Func<List<ResumeEntry>, T> mutate)
    {
        if (!Ready(path)) return _json.Update(path, mutate);

        _lock.Wait();
        try
        {
            using var connection = HistoryDatabase.Open(StateDatabase.PathFor(path));
            T result = default!;
            Transact(connection, () =>
            {
                var rows = ReadAll(connection);
                var entries = rows.Select(r => r.Entry).ToList();
                result = mutate(entries);
                Apply(connection, rows, entries);
            });
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ---- rows ----------------------------------------------------------------------------------------------------------------------------

    private sealed record Row(ResumeEntry Entry, long Id, object?[] Values);

    private static string KeyOf(ResumeEntry e) => e.LaneId + "|" + e.FullName.ToUpperInvariant();

    /// <summary>The entry's fields as the database holds them, in column order - what a changed-row check compares.</summary>
    private static object?[] ValuesOf(ResumeEntry e) => new object?[]
    {
        e.LaneId, e.FullName, e.Status.ToString(), e.EncodedFilePath, e.EncodedFileDesiredName, e.PendingCompanionSourceDirectory, e.PendingCompanionVideoDestPath,
        e.LastRetryFailureMessage, e.Order is { } order ? (long)order : null, e.Skipped ? 1L : 0L, e.PresetOverride, e.Removed ? 1L : 0L, e.HeldAfterFailure ? 1L : 0L,
        e.KeptSourceSize, e.KeptSourceModifiedUtc is { } kept ? kept.ToString("O", CultureInfo.InvariantCulture) : null,
        e.CreatedByQueueEdit ? 1L : 0L, e.FileBotUnmatched ? 1L : 0L, e.DestinationLaneId
    };

    /// <summary>Every row, in insertion order. <paramref name="withValues"/> also keeps each entry's stored values, which only an update needs (to see what its callback changed).</summary>
    private static List<Row> ReadAll(SqliteConnection connection, bool withValues = true)
    {
        var rows = new List<Row>();
        using var command = connection.CreateCommand();
        command.CommandText = SelectSql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            long? Number(int i) => reader.IsDBNull(i) ? null : reader.GetInt64(i);

            var entry = new ResumeEntry
            {
                LaneId = reader.GetString(1),
                FullName = reader.GetString(2),
                Status = Enum.TryParse<ResumeStatus>(Text(3), true, out var status) ? status : ResumeStatus.Pending,
                EncodedFilePath = Text(4),
                EncodedFileDesiredName = Text(5),
                PendingCompanionSourceDirectory = Text(6),
                PendingCompanionVideoDestPath = Text(7),
                LastRetryFailureMessage = Text(8),
                Order = Number(9) is { } order ? (int)order : null,
                Skipped = Number(10) == 1,
                PresetOverride = Text(11),
                Removed = Number(12) == 1,
                HeldAfterFailure = Number(13) == 1,
                KeptSourceSize = Number(14),
                KeptSourceModifiedUtc = Text(15) is { } kept ? DateTime.Parse(kept, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null,
                CreatedByQueueEdit = Number(16) == 1,
                FileBotUnmatched = Number(17) == 1,
                DestinationLaneId = Text(18)
            };
            rows.Add(new Row(entry, reader.GetInt64(0), withValues ? ValuesOf(entry) : Array.Empty<object?>()));
        }

        return rows;
    }

    private static void Bind(SqliteCommand command, ResumeEntry entry, string now)
    {
        command.Parameters.Clear();
        command.Parameters.AddWithValue("@key", KeyOf(entry));
        command.Parameters.AddWithValue("@now", now);
        var values = ValuesOf(entry);
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue("@p" + i, values[i] ?? DBNull.Value);
    }

    private static void Insert(SqliteConnection connection, ResumeEntry entry)
    {
        using var command = connection.CreateCommand();
        command.CommandText = InsertSql;
        Bind(command, entry, HistoryDatabase.Iso(DateTime.UtcNow));
        command.ExecuteNonQuery();
    }

    /// <summary>Writes what the callback did to the list: rows whose entry is gone are deleted, entries whose fields differ are updated in place (found by
    /// reference, so an entry that was renamed in the callback keeps its row), entries that are new are inserted. Nothing else is touched.</summary>
    private static void Apply(SqliteConnection connection, List<Row> rows, List<ResumeEntry> entries)
    {
        var live = new HashSet<ResumeEntry>(entries, ReferenceEqualityComparer.Instance);
        var known = new HashSet<ResumeEntry>(rows.Select(r => r.Entry), ReferenceEqualityComparer.Instance);
        var now = HistoryDatabase.Iso(DateTime.UtcNow);

        foreach (var row in rows.Where(r => !live.Contains(r.Entry)))
        {
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM queue WHERE id = @id;";
            delete.Parameters.AddWithValue("@id", row.Id);
            delete.ExecuteNonQuery();
        }

        using (var update = connection.CreateCommand())
        {
            update.CommandText = UpdateSql;
            foreach (var row in rows.Where(r => live.Contains(r.Entry) && !ValuesOf(r.Entry).SequenceEqual(r.Values)))
            {
                Bind(update, row.Entry, now);
                update.Parameters.AddWithValue("@id", row.Id);
                update.ExecuteNonQuery();
            }
        }

        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = InsertSql;
            foreach (var entry in entries.Where(e => !known.Contains(e)))
            {
                Bind(insert, entry, now);
                insert.ExecuteNonQuery();
            }
        }
    }

    private static void Transact(SqliteConnection connection, Action body)
    {
        HistoryDatabase.Execute(connection, "BEGIN IMMEDIATE;");
        try
        {
            body();
            HistoryDatabase.Execute(connection, "COMMIT;");
        }
        catch
        {
            try { HistoryDatabase.Execute(connection, "ROLLBACK;"); } catch { /* the connection is closed next; SQLite rolls an open transaction back */ }
            throw;
        }
    }

    // ---- setup and import ----------------------------------------------------------------------------------------------------------------

    /// <summary>True when the database is ready to use (created, checked, migrated, and the JSON file imported if there was one).</summary>
    private bool Ready(string resumeFilePath)
    {
        if (_importFailure is not null) return false;
        var databasePath = StateDatabase.PathFor(resumeFilePath);
        lock (InitLock)
        {
            if (ReadyPaths.Contains(databasePath) && File.Exists(databasePath)) return true;
            var isNew = !File.Exists(databasePath);
            try
            {
                if (!isNew && StateDatabase.OpenAndCheck(databasePath) is { } damage)
                {
                    var aside = SetAside(databasePath, ".damaged");
                    _notice = $"The queue database was damaged ({damage}) and was set aside as {Path.GetFileName(aside)}; the queue starts empty and your folders are scanned again. Queue edits (order, skips, profile choices) were lost.";
                    isNew = true;
                }

                if (isNew && StateDatabase.OpenAndCheck(databasePath) is { } createFailure) throw new InvalidDataException(createFailure);
                Import(resumeFilePath, databasePath);
                ReadyPaths.Add(databasePath);
                return true;
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or InvalidOperationException))
            {
                _importFailure = ex.Message;
                // A database this call just created is thrown away, so the next start imports the untouched JSON file again. One that was already
                // there is never deleted: it may be damaged, but it may also hold the only copy of the queue.
                if (isNew) { try { File.Delete(databasePath); } catch { /* the next start tries again */ } }
                return false;
            }
        }
    }

    /// <summary>Moves the JSON queue file into an empty database, once (the marker in <c>meta</c> says it was done, with or without a file).</summary>
    private void Import(string resumeFilePath, string databasePath)
    {
        using var connection = HistoryDatabase.Open(databasePath);
        if (HistoryDatabase.Scalar(connection, $"SELECT COUNT(*) FROM meta WHERE key = '{ImportedKey}';") > 0) return;

        var entries = new List<ResumeEntry>();
        var hadFile = File.Exists(resumeFilePath);
        if (hadFile)
        {
            try
            {
                entries = _json.Load(resumeFilePath);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                var aside = SetAside(resumeFilePath, ".damaged");
                _notice = $"The old queue file could not be read ({ex.Message}) and was set aside as {Path.GetFileName(aside)}; the queue starts empty and your folders are scanned again. Queue edits (order, skips, profile choices) were lost.";
                hadFile = false;
            }
        }

        Transact(connection, () =>
        {
            foreach (var entry in entries) Insert(connection, entry);
            var expected = entries.Select(KeyOf).Distinct().Count();
            var actual = HistoryDatabase.Scalar(connection, "SELECT COUNT(*) FROM queue;");
            if (actual != expected) throw new InvalidDataException($"the queue import wrote {actual} entries, expected {expected}");
            HistoryDatabase.Execute(connection, $"INSERT INTO meta (key, value) VALUES ('{ImportedKey}', '{HistoryDatabase.Iso(DateTime.UtcNow)}');");
        });

        if (hadFile)
        {
            try { File.Move(resumeFilePath, resumeFilePath + ".imported", overwrite: true); }
            catch (IOException) { /* imported all the same: the marker stops a second import */ }
        }
    }

    private static string SetAside(string path, string suffix)
    {
        var target = path + suffix;
        if (File.Exists(target)) target = path + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + suffix;
        File.Move(path, target, overwrite: true);
        try { File.Delete(path + "-journal"); } catch { /* a leftover journal of a file that is gone does no harm */ }
        return target;
    }
}
