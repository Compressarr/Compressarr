using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Compressarr.Core.Logging;

/// <summary>The run archive's SQLite file: opening it, its schema (versioned with PRAGMA user_version, one migration per step) and the running
/// totals kept next to the runs. See docs/design-sqlite-history.md.</summary>
internal static class HistoryDatabase
{
    public const int CurrentVersion = 2;

    /// <summary>Opens the file (creating it). Not pooled: a pooled connection keeps the file open, which would block replacing it on a restore
    /// and deleting it in a test; a local file opens in about a millisecond, and the archive is touched a few times per run.</summary>
    public static SqliteConnection Open(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true
        }.ToString());
        connection.Open();
        Execute(connection, "PRAGMA busy_timeout = 5000;");
        return connection;
    }

    public static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static long Scalar(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Brings the file up to <see cref="CurrentVersion"/>. A file written by a newer build is refused rather than guessed at.</summary>
    public static void Migrate(SqliteConnection connection)
    {
        var version = (int)Scalar(connection, "PRAGMA user_version;");
        if (version > CurrentVersion) throw new InvalidOperationException($"The history database is version {version}, newer than this build understands ({CurrentVersion}).");

        if (version < 1)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, SchemaV1, transaction);
            Execute(connection, "PRAGMA user_version = 1;", transaction);
            transaction.Commit();
        }

        if (version < 2)
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, SchemaV2, transaction);
            Execute(connection, "PRAGMA user_version = 2;", transaction);
            transaction.Commit();
        }
    }

    /// <summary>3.1: videos are counted on their own (a Videos lane's files), and a file can be scrubbed (its name and messages replaced by "Removed" while its numbers stay).
    /// Runs recorded before this have no videos: they were counted with the movies, as they always were.</summary>
    private const string SchemaV2 = """
        ALTER TABLE run_files ADD COLUMN scrubbed INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE runs ADD COLUMN videos INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE daily_totals ADD COLUMN videos INTEGER NOT NULL DEFAULT 0;
        """;

    private const string SchemaV1 = """
        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);

        CREATE TABLE runs (
            id                 INTEGER PRIMARY KEY,
            kind               TEXT    NOT NULL DEFAULT 'run',      -- 'run', or 'pass' for a pass that only hit a lane problem or retried a deferred file (a report, no history row, no totals)
            has_report         INTEGER NOT NULL DEFAULT 0,           -- 1 when the lane sections and files of the report are stored (or an old report's html is)
            rollups            TEXT    NULL,                         -- JSON: the Today / This month / This year rows the report showed when it was made
            counted            INTEGER NOT NULL DEFAULT 1,           -- 0 for a run in which every file failed and nothing was produced (left out of every total)
            run_number         INTEGER NOT NULL DEFAULT 0,
            ended_date         TEXT    NOT NULL,                     -- yyyy-MM-dd, the day the run ended
            ended_at           TEXT    NULL,                         -- yyyy-MM-ddTHH:mm:ss when known (older rows: worked out from the report name)
            seconds            INTEGER NOT NULL DEFAULT 0,
            files              INTEGER NOT NULL DEFAULT 0,
            before_gb          REAL    NOT NULL DEFAULT 0,
            after_gb           REAL    NOT NULL DEFAULT 0,
            errors             INTEGER NOT NULL DEFAULT 0,
            warnings           INTEGER NOT NULL DEFAULT 0,
            redirects          INTEGER NOT NULL DEFAULT 0,
            movies             INTEGER NULL,                         -- NULL = unknown, never 0
            tvs                INTEGER NULL,
            avg_fps            REAL    NULL,
            hb_files           INTEGER NULL,                         -- NULL = the engine / lane / profile breakdown was never recorded for this run
            ff_files           INTEGER NULL,
            pass_files         INTEGER NULL,
            retries_succeeded  INTEGER NOT NULL DEFAULT 0,
            report_file        TEXT    NULL,                         -- a saved copy of the report on disk, when there is one
            summary_log        TEXT    NULL,
            legacy_report      BLOB    NULL,                         -- gzip of an old saved report html (runs imported from before 3.0)
            legacy_report_name TEXT    NULL
        );
        CREATE INDEX ix_runs_date ON runs (ended_date);
        CREATE INDEX ix_runs_number ON runs (run_number);

        CREATE TABLE run_lanes (
            run_id   INTEGER NOT NULL REFERENCES runs (id) ON DELETE CASCADE,
            ord      INTEGER NOT NULL,
            lane     TEXT    NOT NULL,
            files    INTEGER NOT NULL DEFAULT 0,                     -- counted files this lane handled
            problems TEXT    NULL,                                   -- comma-separated lane problem codes
            PRIMARY KEY (run_id, ord)
        );

        CREATE TABLE run_profiles (
            run_id  INTEGER NOT NULL REFERENCES runs (id) ON DELETE CASCADE,
            profile TEXT    NOT NULL,
            files   INTEGER NOT NULL,
            PRIMARY KEY (run_id, profile)
        );

        CREATE TABLE run_files (
            id                    INTEGER PRIMARY KEY,
            run_id                INTEGER NOT NULL REFERENCES runs (id) ON DELETE CASCADE,
            lane_ord              INTEGER NOT NULL,
            lane_id               TEXT    NULL,
            name                  TEXT    NOT NULL,
            content_type          TEXT    NOT NULL,
            preset                TEXT    NULL,
            encoder_label         TEXT    NULL,
            passthrough           INTEGER NOT NULL DEFAULT 0,
            engine                TEXT    NOT NULL,                  -- handbrake | ffmpeg | passthrough
            before_gb             REAL    NOT NULL DEFAULT 0,
            after_gb              REAL    NOT NULL DEFAULT 0,
            seconds               REAL    NOT NULL DEFAULT 0,
            started_at            TEXT    NULL,
            ended_at              TEXT    NULL,
            success               INTEGER NOT NULL DEFAULT 0,
            counted               INTEGER NOT NULL DEFAULT 0,
            avg_fps               REAL    NULL,
            error_code            INTEGER NULL,
            failure_reason        TEXT    NULL,
            warning_text          TEXT    NULL,
            warning_codes         TEXT    NULL,
            arr_status            TEXT    NULL,
            detail_log            TEXT    NULL,
            disk_full             INTEGER NOT NULL DEFAULT 0,
            redirected_to_lane_id TEXT    NULL,
            redirected_to_lane    TEXT    NULL,
            home_lane             TEXT    NULL
        );
        CREATE INDEX ix_run_files_run ON run_files (run_id);
        CREATE INDEX ix_run_files_ended ON run_files (ended_at);

        CREATE TABLE daily_totals (
            day           TEXT PRIMARY KEY,
            runs          INTEGER NOT NULL DEFAULT 0,
            files         INTEGER NOT NULL DEFAULT 0,
            before_gb     REAL    NOT NULL DEFAULT 0,
            after_gb      REAL    NOT NULL DEFAULT 0,
            seconds       INTEGER NOT NULL DEFAULT 0,
            movies        INTEGER NOT NULL DEFAULT 0,
            tvs           INTEGER NOT NULL DEFAULT 0,
            unknown_files INTEGER NOT NULL DEFAULT 0,                -- files of runs whose movie / TV split is unknown
            errors        INTEGER NOT NULL DEFAULT 0,
            warnings      INTEGER NOT NULL DEFAULT 0,
            redirects     INTEGER NOT NULL DEFAULT 0,
            hb_files      INTEGER NOT NULL DEFAULT 0,
            ff_files      INTEGER NOT NULL DEFAULT 0,
            pass_files    INTEGER NOT NULL DEFAULT 0,
            fps_weighted  REAL    NOT NULL DEFAULT 0,                -- sum of avg_fps * seconds over runs with a speed
            fps_seconds   REAL    NOT NULL DEFAULT 0
        );

        CREATE TABLE daily_lanes (
            day   TEXT    NOT NULL,
            lane  TEXT    NOT NULL,
            files INTEGER NOT NULL,
            PRIMARY KEY (day, lane)
        );

        CREATE TABLE daily_profiles (
            day     TEXT    NOT NULL,
            profile TEXT    NOT NULL,
            files   INTEGER NOT NULL,
            PRIMARY KEY (day, profile)
        );
        """;

    /// <summary>A consistent copy of the live database at <paramref name="destinationPath"/>, made with SQLite's own backup call so it is correct even
    /// while the app is writing. False when there is no database (a fresh install, or one still on the CSV).</summary>
    public static bool Snapshot(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath)) return false;
        using var source = Open(sourcePath);
        using var destination = Open(destinationPath);
        source.BackupDatabase(destination);
        return true;
    }

    /// <summary>Checks a database file taken from a backup (it opens, passes SQLite's integrity check, and is brought up to this build's schema).
    /// Throws with a plain message when it is not usable; the file is migrated in place.</summary>
    public static void ValidateAndMigrate(string path)
    {
        try
        {
            using var connection = Open(path);
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA integrity_check;";
                if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("integrity check failed");
            }
            Migrate(connection);
        }
        catch (SqliteException ex)
        {
            throw new InvalidDataException("the history database in the backup is damaged", ex);
        }
    }

    public static string Iso(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Day(int year, int month, int day) => string.Create(CultureInfo.InvariantCulture, $"{year:0000}-{month:00}-{day:00}");

    /// <summary>Adds one counted run to the running totals (a day's row, and its lane and profile rows). Called in the same transaction that
    /// records the run, so the totals can never disagree with the runs.</summary>
    public static void AddToTotals(SqliteConnection connection, SqliteTransaction transaction, RunHistoryRecord run)
    {
        var day = Day(run.Year, run.Month, run.Day);
        var seconds = run.ProcessHours * 3600 + run.ProcessMinutes * 60 + run.ProcessSeconds;

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO daily_totals (day, runs, files, before_gb, after_gb, seconds, movies, tvs, videos, unknown_files, errors, warnings, redirects,
                                          hb_files, ff_files, pass_files, fps_weighted, fps_seconds)
                VALUES (@day, 1, @files, @before, @after, @seconds, @movies, @tvs, @videos, @unknown, @errors, @warnings, @redirects, @hb, @ff, @pass, @fpsw, @fpss)
                ON CONFLICT (day) DO UPDATE SET
                    runs = runs + 1, files = files + excluded.files, before_gb = before_gb + excluded.before_gb, after_gb = after_gb + excluded.after_gb,
                    seconds = seconds + excluded.seconds, movies = movies + excluded.movies, tvs = tvs + excluded.tvs, videos = videos + excluded.videos,
                    unknown_files = unknown_files + excluded.unknown_files, errors = errors + excluded.errors, warnings = warnings + excluded.warnings,
                    redirects = redirects + excluded.redirects, hb_files = hb_files + excluded.hb_files, ff_files = ff_files + excluded.ff_files,
                    pass_files = pass_files + excluded.pass_files, fps_weighted = fps_weighted + excluded.fps_weighted,
                    fps_seconds = fps_seconds + excluded.fps_seconds;
                """;
            command.Parameters.AddWithValue("@day", day);
            command.Parameters.AddWithValue("@files", run.FileCount);
            command.Parameters.AddWithValue("@before", run.BeginSizeGb);
            command.Parameters.AddWithValue("@after", run.EndSizeGb);
            command.Parameters.AddWithValue("@seconds", seconds);
            command.Parameters.AddWithValue("@movies", run.MovieCount ?? 0);
            command.Parameters.AddWithValue("@tvs", run.TvCount ?? 0);
            command.Parameters.AddWithValue("@videos", run.VideoCount ?? 0);
            command.Parameters.AddWithValue("@unknown", run.MovieCount is null ? run.FileCount : 0);
            command.Parameters.AddWithValue("@errors", run.ErrorCount);
            command.Parameters.AddWithValue("@warnings", run.WarningCount);
            command.Parameters.AddWithValue("@redirects", run.RedirectCount);
            command.Parameters.AddWithValue("@hb", run.HandBrakeCount ?? 0);
            command.Parameters.AddWithValue("@ff", run.FfmpegCount ?? 0);
            command.Parameters.AddWithValue("@pass", run.PassthroughCount ?? 0);
            command.Parameters.AddWithValue("@fpsw", run.AvgFps is { } fps ? fps * seconds : 0);
            command.Parameters.AddWithValue("@fpss", run.AvgFps is not null ? seconds : 0);
            command.ExecuteNonQuery();
        }

        AddCounts(connection, transaction, "daily_lanes", "lane", day, run.LaneCounts);
        AddCounts(connection, transaction, "daily_profiles", "profile", day, run.PresetCounts);
    }

    private static void AddCounts(SqliteConnection connection, SqliteTransaction transaction, string table, string column, string day, IReadOnlyDictionary<string, int>? counts)
    {
        if (counts is null) return;
        foreach (var (name, files) in counts)
        {
            if (files <= 0) continue;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"INSERT INTO {table} (day, {column}, files) VALUES (@day, @name, @files) ON CONFLICT (day, {column}) DO UPDATE SET files = files + excluded.files;";
            command.Parameters.AddWithValue("@day", day);
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@files", files);
            command.ExecuteNonQuery();
        }
    }

    public static void ClearTotals(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, "DELETE FROM daily_totals; DELETE FROM daily_lanes; DELETE FROM daily_profiles;", transaction);
    }
}
