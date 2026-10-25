using Compressarr.Core.Logging;
using Microsoft.Data.Sqlite;

namespace Compressarr.Core.Conversion;

/// <summary>The queue's SQLite file (<c>compressarr.state.db</c>): its schema (versioned with PRAGMA user_version, one migration per step), the checks
/// made when it is opened, and the backup copy. It replaces <c>compressarr.resume.json</c>; see docs/design-sqlite-queue.md. Opening and plain
/// statements are shared with <see cref="HistoryDatabase"/>.</summary>
internal static class StateDatabase
{
    public const int CurrentVersion = 2;
    public const string FileName = "compressarr.state.db";

    /// <summary>The database that goes with a resume-file path: the app's own <c>compressarr.resume.json</c> maps to <c>compressarr.state.db</c> in the
    /// same folder; any other name (a test's) gets its own database beside it, so two queues never share one.</summary>
    public static string PathFor(string resumeFilePath)
    {
        var folder = Path.GetDirectoryName(resumeFilePath) ?? "";
        var name = Path.GetFileName(resumeFilePath);
        return Path.Combine(folder, string.Equals(name, "compressarr.resume.json", StringComparison.OrdinalIgnoreCase) ? FileName : name + ".state.db");
    }

    /// <summary>Opens the file and checks it: SQLite's quick check, then the schema is brought up to date. Returns null when it is usable, or a plain
    /// description of the damage (the caller sets the file aside). A file written by a newer build throws instead - that one is not damaged.</summary>
    public static string? OpenAndCheck(string path)
    {
        try
        {
            using var connection = HistoryDatabase.Open(path);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA quick_check;";
                var result = command.ExecuteScalar() as string;
                if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase)) return result ?? "the integrity check failed";
            }

            Migrate(connection);
            return null;
        }
        catch (SqliteException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Brings the file up to <see cref="CurrentVersion"/>. A file written by a newer build is refused rather than guessed at.</summary>
    public static void Migrate(SqliteConnection connection)
    {
        var version = (int)HistoryDatabase.Scalar(connection, "PRAGMA user_version;");
        if (version > CurrentVersion) throw new InvalidOperationException($"The queue database is version {version}, newer than this build understands ({CurrentVersion}).");

        if (version < 1)
        {
            using var transaction = connection.BeginTransaction();
            HistoryDatabase.Execute(connection, SchemaV1, transaction);
            HistoryDatabase.Execute(connection, "PRAGMA user_version = 1;", transaction);
            transaction.Commit();
        }

        if (version < 2)
        {
            using var transaction = connection.BeginTransaction();
            HistoryDatabase.Execute(connection, SchemaV2, transaction);
            HistoryDatabase.Execute(connection, "PRAGMA user_version = 2;", transaction);
            transaction.Commit();
        }
    }

    // v2: the values the app writes as it works (viewed-through marker, last backup, digest-sent days), which used to live in the settings file.
    private const string SchemaV2 = """
        CREATE TABLE kv (
            key        TEXT PRIMARY KEY,
            value      TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );
        """;

    private const string SchemaV1 = """
        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);

        CREATE TABLE queue (
            id                                INTEGER PRIMARY KEY,         -- insertion order: the order the entries are handed out in
            key                               TEXT    NOT NULL UNIQUE,     -- lane id + '|' + the path in upper case: one entry per file per lane
            lane_id                           TEXT    NOT NULL,
            full_name                         TEXT    NOT NULL,
            status                            TEXT    NOT NULL,            -- Pending, Completed, Error, MoveFailed, CompanionMoveFailed, CleanupPending
            encoded_file_path                 TEXT    NULL,
            encoded_file_desired_name         TEXT    NULL,
            pending_companion_source_dir      TEXT    NULL,
            pending_companion_video_dest_path TEXT    NULL,
            last_retry_failure_message        TEXT    NULL,
            position                          INTEGER NULL,                -- the queue position (lower first); NULL until the file is first tracked
            skipped                           INTEGER NOT NULL DEFAULT 0,
            preset_override                   TEXT    NULL,
            removed                           INTEGER NOT NULL DEFAULT 0,
            held_after_failure                INTEGER NOT NULL DEFAULT 0,
            kept_source_size                  INTEGER NULL,
            kept_source_modified_utc          TEXT    NULL,
            created_by_queue_edit             INTEGER NOT NULL DEFAULT 0,
            filebot_unmatched                 INTEGER NOT NULL DEFAULT 0,
            destination_lane_id               TEXT    NULL,
            created_at                        TEXT    NOT NULL,
            updated_at                        TEXT    NOT NULL
        );
        CREATE INDEX ix_queue_status ON queue (status);
        CREATE INDEX ix_queue_lane ON queue (lane_id, position);
        """;

    /// <summary>Checks a database file taken from a backup (it opens, passes SQLite's integrity check, and is brought up to this build's schema).
    /// Throws with a plain message when it is not usable; the file is migrated in place.</summary>
    public static void ValidateAndMigrate(string path)
    {
        try
        {
            using var connection = HistoryDatabase.Open(path);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA integrity_check;";
                if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("integrity check failed");
            }

            Migrate(connection);
        }
        catch (SqliteException ex)
        {
            throw new InvalidDataException("the queue database in the backup is damaged", ex);
        }
    }

    /// <summary>A consistent copy of the live database, made with SQLite's own backup call (correct while the app is writing). False when there is none.</summary>
    public static bool Snapshot(string sourcePath, string destinationPath) => HistoryDatabase.Snapshot(sourcePath, destinationPath);
}
