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
public sealed class SqliteResumeStateStore : IResumeStateStore, IQueueAvailability
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
        nameof(ResumeEntry.CreatedByQueueEdit), nameof(ResumeEntry.FileBotUnmatched), nameof(ResumeEntry.DestinationLaneId),
        nameof(ResumeEntry.EncodedFromSize), nameof(ResumeEntry.EncodedFromModifiedUtc), nameof(ResumeEntry.EncodedFromCreatedUtc), nameof(ResumeEntry.EncodedFromFingerprint),
        nameof(ResumeEntry.EncodedFileSize), nameof(ResumeEntry.EncodedFileDigest), nameof(ResumeEntry.FiledInOutput),
        nameof(ResumeEntry.ParkedOriginalPath)
    };

    private static readonly string[] Columns =
    {
        "lane_id", "full_name", "status", "encoded_file_path", "encoded_file_desired_name", "pending_companion_source_dir", "pending_companion_video_dest_path",
        "last_retry_failure_message", "position", "skipped", "preset_override", "removed", "held_after_failure", "kept_source_size", "kept_source_modified_utc",
        "created_by_queue_edit", "filebot_unmatched", "destination_lane_id", "encoded_from_size", "encoded_from_modified_utc", "encoded_from_created_utc", "encoded_from_fingerprint",
        "encoded_file_size", "encoded_file_digest", "filed_in_output", "parked_original_path"
    };

    private static readonly string SelectSql = "SELECT id, " + string.Join(", ", Columns) + " FROM queue ORDER BY id;";

    private static readonly string InsertSql =
        "INSERT INTO queue (key, " + string.Join(", ", Columns) + ", created_at, updated_at) VALUES (@key, " + string.Join(", ", Columns.Select((_, i) => "@p" + i)) + ", @now, @now) " +
        "ON CONFLICT (key) DO UPDATE SET " + string.Join(", ", Columns.Select(c => c + " = excluded." + c)) + ", updated_at = excluded.updated_at;";

    private static readonly string UpdateSql =
        "UPDATE OR REPLACE queue SET key = @key, " + string.Join(", ", Columns.Select((c, i) => c + " = @p" + i)) + ", updated_at = @now WHERE id = @id;";

    /// <summary>After a failure to open the database the JSON store serves, but only for this long: then the database is tried again. A failure that was only a moment's
    /// trouble (a virus scanner holding the file as the app started, a busy database) must not leave the queue, the sign-in, the API keys and the hold on the fallback until
    /// the app is restarted.</summary>
    internal static readonly TimeSpan RetryFailedAfter = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan[] TransientRetryDelays = { TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(750) };

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly JsonResumeStateStore _json = new();
    private readonly TimeProvider _time;
    private string? _importFailure;
    private bool _authoritativeUnavailable;   // the failure is of a database that was the queue's home: the JSON fallback is not a copy of it, so it must not stand in
    private DateTimeOffset _failedAt;
    private string? _notice;

    public SqliteResumeStateStore() : this(TimeProvider.System) { }

    /// <summary>For tests that need to move the clock past the retry pause.</summary>
    internal SqliteResumeStateStore(TimeProvider time) => _time = time;

    /// <summary>Why the queue could not be moved into the database, or null. While it is set the JSON store serves instead.</summary>
    public string? ImportFailure => _importFailure;

    /// <summary>Why the queue cannot be used right now, or null. Set only when the database that holds the queue (it exists, or it was in use) cannot be opened: the JSON
    /// fallback starts empty, knows nothing of what was skipped, removed, held or kept, and an edit made to it cannot be reconciled with the database exactly, so while this is
    /// set nothing is scanned or encoded and an edit is refused, instead of acting on an empty queue or accepting a change that may be lost. A database that could never be created
    /// (nothing was ever in it) is not this: there the JSON file is still the queue and keeps serving.</summary>
    public string? UnavailableReason(string resumeFilePath) => Ready(resumeFilePath) || !_authoritativeUnavailable ? null : _importFailure;

    private void RefuseWhileUnavailable()
    {
        if (_authoritativeUnavailable)
            throw new InvalidOperationException($"The queue database cannot be opened right now ({_importFailure}), so the change was not made. It is tried again every {(int)RetryFailedAfter.TotalSeconds} seconds.");
    }

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
        if (!Ready(path)) { RefuseWhileUnavailable(); _json.Save(state, path); return; }

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
        if (!Ready(path)) { RefuseWhileUnavailable(); _json.DeleteIfComplete(state, path); return; }

        var hasOutstanding = state.Any(e => e.Status is ResumeStatus.Pending or ResumeStatus.Error or ResumeStatus.MoveFailed or ResumeStatus.CompanionMoveFailed or ResumeStatus.CleanupPending or ResumeStatus.FinishPending);
        if (hasOutstanding) return;

        // Nothing left to do - but a converted file whose source was kept must stay remembered, or it is converted again.
        Save(state.Where(ResumeKeeping.StillNeeded).ToList(), path);
    }

    public T Update<T>(string path, Func<List<ResumeEntry>, T> mutate)
    {
        if (!Ready(path)) { RefuseWhileUnavailable(); return _json.Update(path, mutate); }

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
        e.CreatedByQueueEdit ? 1L : 0L, e.FileBotUnmatched ? 1L : 0L, e.DestinationLaneId,
        e.EncodedFromSize, e.EncodedFromModifiedUtc is { } from ? from.ToString("O", CultureInfo.InvariantCulture) : null,
        e.EncodedFromCreatedUtc is { } createdFrom ? createdFrom.ToString("O", CultureInfo.InvariantCulture) : null, e.EncodedFromFingerprint,
        e.EncodedFileSize, e.EncodedFileDigest, e.FiledInOutput ? 1L : 0L, e.ParkedOriginalPath
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
                DestinationLaneId = Text(18),
                EncodedFromSize = Number(19),
                EncodedFromModifiedUtc = Text(20) is { } from ? DateTime.Parse(from, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null,
                EncodedFromCreatedUtc = Text(21) is { } createdFrom ? DateTime.Parse(createdFrom, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) : null,
                EncodedFromFingerprint = Text(22),
                EncodedFileSize = Number(23),
                EncodedFileDigest = Text(24),
                FiledInOutput = Number(25) == 1,
                ParkedOriginalPath = Text(26)
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
        var databasePath = StateDatabase.PathFor(resumeFilePath);
        lock (InitLock)
        {
            if (_importFailure is not null)
            {
                // Inside the pause after a failure the fallback keeps serving; after it, the database is looked at again.
                if (_time.GetUtcNow() - _failedAt < RetryFailedAfter) return false;
                _importFailure = null;
            }

            if (ReadyPaths.Contains(databasePath) && File.Exists(databasePath)) return true;

            // Whether this database was already the queue's home (it is there, it was in use, or its JSON file was moved into it): then a failure to open it is an outage
            // of the real queue, not a fallback to the old one. Sticky until it opens again.
            var wasInUse = ReadyPaths.Contains(databasePath) || File.Exists(databasePath) || File.Exists(resumeFilePath + ".imported");

            for (var attempt = 0; ; attempt++)
            {
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
                    _authoritativeUnavailable = false;
                    return true;
                }
                catch (Exception ex) when (IsTransient(ex) && attempt < TransientRetryDelays.Length)
                {
                    // A file held for a moment (a scanner, another process): wait a little and open it again before falling back.
                    DiscardIfJustCreated(isNew, databasePath);
                    Thread.Sleep(TransientRetryDelays[attempt]);
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or InvalidOperationException))
                {
                    _importFailure = ex.Message;
                    _authoritativeUnavailable |= wasInUse;
                    _failedAt = _time.GetUtcNow();
                    ReadyPaths.Remove(databasePath);   // not ready any more: when it is tried again it is opened, checked and (for a queue file written meanwhile) merged afresh
                    // A database this call just created is thrown away, so the next try imports the untouched JSON file again. One that was already
                    // there is never deleted: it may be damaged, but it may also hold the only copy of the queue.
                    DiscardIfJustCreated(isNew, databasePath);
                    return false;
                }
            }
        }
    }

    /// <summary>Trouble that passes by itself: the file or its folder is held, or the database is busy or locked, right now.</summary>
    private static bool IsTransient(Exception ex) => ex is IOException || ex is SqliteException { SqliteErrorCode: 5 or 6 };

    private static void DiscardIfJustCreated(bool isNew, string databasePath)
    {
        if (isNew) { try { File.Delete(databasePath); } catch { /* the next try starts again */ } }
    }

    /// <summary>Moves the JSON queue file into an empty database, once (the marker in <c>meta</c> says it was done, with or without a file).</summary>
    private void Import(string resumeFilePath, string databasePath)
    {
        using var connection = HistoryDatabase.Open(databasePath);
        if (HistoryDatabase.Scalar(connection, $"SELECT COUNT(*) FROM meta WHERE key = '{ImportedKey}';") > 0)
        {
            MergeFallback(connection, resumeFilePath);
            return;
        }

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

    /// <summary>The database is already the queue's home, yet a queue file is there: it was written by the JSON store while the database could not be opened (everything
    /// the app did meanwhile - queue edits, finished encodes, a failed move waiting to be retried - went there). Now that the database is back that work is merged into it,
    /// then the file is renamed .recovered. Rows the file does not mention are untouched. For a file in both: what the app recorded about the file's processing (status,
    /// where the encoded file sits, held failure, kept-source and source stamps) wins when the fallback actually processed it; the person's queue choices (skip, remove,
    /// profile, destination) are carried over when set; the database's own values stand otherwise.</summary>
    private void MergeFallback(SqliteConnection connection, string resumeFilePath)
    {
        if (!File.Exists(resumeFilePath)) return;

        List<ResumeEntry> fallback;
        try
        {
            fallback = _json.Load(resumeFilePath);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            var aside = SetAside(resumeFilePath, ".damaged");
            _notice = $"The queue file written while the database was unavailable could not be read ({ex.Message}) and was set aside as {Path.GetFileName(aside)}; the queue in the database is used as it is.";
            return;
        }

        if (fallback.Count > 0)
        {
            Transact(connection, () =>
            {
                var rows = ReadAll(connection);
                var entries = rows.Select(r => r.Entry).ToList();
                var byKey = entries.ToDictionary(KeyOf);
                var nextOrder = ResumeQueueOrder.NextOrder(entries);

                foreach (var fromFallback in fallback.OrderBy(e => e.Order ?? int.MaxValue))
                {
                    if (byKey.TryGetValue(KeyOf(fromFallback), out var inDatabase))
                    {
                        MergeEntry(inDatabase, fromFallback);
                        continue;
                    }

                    if (fromFallback.Order is not null) fromFallback.Order = nextOrder++;   // after what the database already holds, in the order the fallback had
                    entries.Add(fromFallback);
                    byKey[KeyOf(fromFallback)] = fromFallback;
                }

                Apply(connection, rows, entries);
            });
        }

        try { File.Move(resumeFilePath, resumeFilePath + ".recovered", overwrite: true); }
        catch (IOException) { /* merged all the same; a second merge of the same file changes nothing */ }
    }

    private static void MergeEntry(ResumeEntry inDatabase, ResumeEntry fromFallback)
    {
        if (fromFallback.Status != ResumeStatus.Pending)
        {
            inDatabase.Status = fromFallback.Status;
            inDatabase.EncodedFilePath = fromFallback.EncodedFilePath;
            inDatabase.EncodedFileDesiredName = fromFallback.EncodedFileDesiredName;
            inDatabase.PendingCompanionSourceDirectory = fromFallback.PendingCompanionSourceDirectory;
            inDatabase.PendingCompanionVideoDestPath = fromFallback.PendingCompanionVideoDestPath;
            inDatabase.LastRetryFailureMessage = fromFallback.LastRetryFailureMessage;
            inDatabase.HeldAfterFailure = fromFallback.HeldAfterFailure;
            inDatabase.KeptSourceSize = fromFallback.KeptSourceSize;
            inDatabase.KeptSourceModifiedUtc = fromFallback.KeptSourceModifiedUtc;
            inDatabase.EncodedFromSize = fromFallback.EncodedFromSize;
            inDatabase.EncodedFromModifiedUtc = fromFallback.EncodedFromModifiedUtc;
            inDatabase.EncodedFromCreatedUtc = fromFallback.EncodedFromCreatedUtc;
            inDatabase.EncodedFromFingerprint = fromFallback.EncodedFromFingerprint;
            inDatabase.EncodedFileSize = fromFallback.EncodedFileSize;
            inDatabase.EncodedFileDigest = fromFallback.EncodedFileDigest;
            inDatabase.FiledInOutput = fromFallback.FiledInOutput;
            inDatabase.ParkedOriginalPath = fromFallback.ParkedOriginalPath;
        }

        inDatabase.Skipped |= fromFallback.Skipped;
        inDatabase.Removed |= fromFallback.Removed;
        if (!string.IsNullOrEmpty(fromFallback.PresetOverride)) inDatabase.PresetOverride = fromFallback.PresetOverride;
        if (fromFallback.DestinationLaneId is not null) inDatabase.DestinationLaneId = fromFallback.DestinationLaneId;
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
