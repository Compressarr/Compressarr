using System.Globalization;
using Compressarr.Core.Config;
using Compressarr.Core.Reporting;
using Microsoft.Data.Sqlite;

namespace Compressarr.Core.Logging;

/// <summary>The run history in SQLite (3.0): the same <see cref="RunHistoryRecord"/>s the CSV store kept, with the running totals maintained in
/// the same transaction as each recorded run and the run counter in the database too. The first use with no database imports the CSV that
/// earlier builds wrote (and keeps it, renamed to .imported). If that import ever fails, the CSV store keeps serving for the rest of the session
/// and the CSV is left exactly as it was.</summary>
public sealed partial class SqliteRunHistoryStore : IRunHistoryStore, IRunArchive
{
    private const string RunCounterKey = "run_counter";
    private static readonly object InitLock = new();
    private static readonly HashSet<string> ReadyPaths = new(StringComparer.OrdinalIgnoreCase);

    private readonly CsvRunHistoryStore _csv = new();
    private readonly Func<string> _databasePath;
    private readonly Func<string?>? _legacyLogFolder;
    private readonly Func<string?>? _reportsFolder;
    private string? _importFailure;

    /// <summary>The store the app uses. The configured Logs folder is where a 2.1.x install still keeps its history, so the first start finds it
    /// whichever call comes first; the configured Report folder is where the reports of earlier versions are, which the import keeps and reads.</summary>
    public SqliteRunHistoryStore(IConfigStore configStore, IPathExpander pathExpander)
        : this(AppPaths.GetHistoryDbPath,
              () => pathExpander.Expand(configStore.Load(AppPaths.GetConfigFilePath()).Logging.LogFilePath),
              () => pathExpander.Expand(configStore.Load(AppPaths.GetConfigFilePath()).Report.ReportPath)) { }

    /// <summary>For tests that want a database somewhere other than the app data folder.</summary>
    internal SqliteRunHistoryStore(Func<string> databasePath, Func<string?>? legacyLogFolder = null, Func<string?>? reportsFolder = null)
    {
        _databasePath = databasePath;
        _legacyLogFolder = legacyLogFolder;
        _reportsFolder = reportsFolder;
    }

    /// <summary>Why the CSV import failed, or null. While it is set the CSV store serves instead of the database.</summary>
    public string? ImportFailure => _importFailure;

    // ---- IRunHistoryStore ----------------------------------------------------------------------------------------------------------------

    public void AppendRun(string legacyLogFolder, RunHistoryRecord record) => RecordRunCore(legacyLogFolder, record);

    public long RecordRun(RunHistoryRecord record) => RecordRunCore(null, record);

    private long RecordRunCore(string? legacyLogFolder, RunHistoryRecord record)
    {
        if (!Ready(legacyLogFolder))
        {
            _csv.AppendRun(!string.IsNullOrWhiteSpace(legacyLogFolder) ? legacyLogFolder : _legacyLogFolder?.Invoke() ?? "", record);
            return 0;
        }

        using var connection = HistoryDatabase.Open(_databasePath());
        using var transaction = connection.BeginTransaction();
        var id = InsertRun(connection, transaction, record);
        transaction.Commit();
        return id;
    }

    public bool IsAvailable() => Ready(null);

    public IReadOnlyList<RunHistoryRecord> GetHistory(string legacyLogFolder)
    {
        if (!Ready(legacyLogFolder)) return _csv.GetHistory(legacyLogFolder);

        using var connection = HistoryDatabase.Open(_databasePath());
        return ReadRuns(connection, "kind = 'run'").Select(r => r.Record).ToList();
    }

    public int GetRunCount(string runCountPath)
    {
        if (!Ready(null)) return _csv.GetRunCount(runCountPath);

        using var connection = HistoryDatabase.Open(_databasePath());
        return (int)ReadCounter(connection);
    }

    public void IncrementRunCount(string runCountPath)
    {
        if (!Ready(null)) { _csv.IncrementRunCount(runCountPath); return; }

        using var connection = HistoryDatabase.Open(_databasePath());
        HistoryDatabase.Execute(connection, $"INSERT INTO meta (key, value) VALUES ('{RunCounterKey}', '1') ON CONFLICT (key) DO UPDATE SET value = CAST(value AS INTEGER) + 1;");
    }

    // ---- maintenance ---------------------------------------------------------------------------------------------------------------------

    /// <summary>Deletes every run and every figure derived from them. The run counter stays (it is a lifetime figure, not history).</summary>
    public void ClearHistory()
    {
        if (!Ready(null)) { _csv.ClearHistory(); return; }
        using var connection = HistoryDatabase.Open(_databasePath());
        using var transaction = connection.BeginTransaction();
        HistoryDatabase.Execute(connection, "DELETE FROM run_files; DELETE FROM run_lanes; DELETE FROM run_profiles; DELETE FROM runs;", transaction);
        HistoryDatabase.ClearTotals(connection, transaction);
        transaction.Commit();
    }

    /// <summary>Recomputes every running total from the runs (after an import, or if they were ever in doubt).</summary>
    public void RebuildTotals()
    {
        if (!Ready(null)) return;
        using var connection = HistoryDatabase.Open(_databasePath());
        using var transaction = connection.BeginTransaction();
        RebuildTotals(connection, transaction);
        transaction.Commit();
    }

    // ---- setup and import ----------------------------------------------------------------------------------------------------------------

    /// <summary>True when the database is ready to use (created, migrated, and the CSV imported if there was one).</summary>
    private bool Ready(string? legacyLogFolder)
    {
        if (_importFailure is not null) return false;
        var path = _databasePath();
        lock (InitLock)
        {
            if (ReadyPaths.Contains(path) && File.Exists(path)) return true;
            var isNew = !File.Exists(path);
            try
            {
                using (var connection = HistoryDatabase.Open(path)) HistoryDatabase.Migrate(connection);
                if (isNew) ImportCsv(path, !string.IsNullOrWhiteSpace(legacyLogFolder) ? legacyLogFolder : _legacyLogFolder?.Invoke() ?? "");
                ReadyPaths.Add(path);
                return true;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _importFailure = ex.Message;
                // A database this call just created is thrown away, so the next start imports the untouched CSV again. One that was already
                // there is never deleted: it may be damaged, but it may also hold the only copy of the history.
                if (isNew) { try { File.Delete(path); } catch { /* the next start tries again */ } }
                return false;
            }
        }
    }

    private void ImportCsv(string databasePath, string legacyLogFolder)
    {
        // The CSV store knows where an old file may be (the 2.1.x Logs folder) and carries it into the data folder.
        var rows = _csv.GetHistory(legacyLogFolder);
        var csvPath = AppPaths.GetHistoryFilePath();
        var runCountPath = AppPaths.GetRunCountFilePath();
        var hadCsv = File.Exists(csvPath);
        var hadCounter = File.Exists(runCountPath);
        if (!hadCsv && !hadCounter) return;

        using (var connection = HistoryDatabase.Open(databasePath))
        using (var transaction = connection.BeginTransaction())
        {
            var reportsFolder = _reportsFolder?.Invoke();
            var withReport = 0;
            foreach (var row in rows)
            {
                // A run whose report file is still on disk keeps it exactly as it was (a gzip blob), and the report is read for what the history
                // never recorded: which lane, tool and profile handled each file, and the movie / TV split. The rows go in as the run's files.
                byte[]? html = null;
                IReadOnlyList<ParsedReportFile>? parsed = null;
                if (!string.IsNullOrWhiteSpace(reportsFolder) && !string.IsNullOrEmpty(row.ReportFileName))
                {
                    var path = Path.Combine(reportsFolder, Path.GetFileName(row.ReportFileName));
                    try
                    {
                        if (File.Exists(path))
                        {
                            html = File.ReadAllBytes(path);
                            parsed = HtmlReportParser.Parse(System.Text.Encoding.UTF8.GetString(html));
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { html = null; parsed = null; }   // unreadable: the run just has no report
                }

                var id = InsertRun(connection, transaction, parsed is { Count: > 0 } ? Enrich(row, parsed) : row, totals: false);
                if (html is not null) StoreLegacyReport(connection, transaction, id, row.ReportFileName, html, parsed ?? Array.Empty<ParsedReportFile>());
                if (html is not null) withReport++;
            }
            SetMeta(connection, transaction, "imported_reports", withReport.ToString(CultureInfo.InvariantCulture));
            RebuildTotals(connection, transaction);

            var counter = Math.Max(_csv.GetRunCount(runCountPath), rows.Count == 0 ? 0 : rows.Max(r => r.RunNumber));
            SetMeta(connection, transaction, RunCounterKey, counter.ToString(CultureInfo.InvariantCulture));
            SetMeta(connection, transaction, "imported_at", HistoryDatabase.Iso(DateTime.Now));
            SetMeta(connection, transaction, "imported_rows", rows.Count.ToString(CultureInfo.InvariantCulture));
            SetMeta(connection, transaction, "imported_skipped_rows", _csv.LastSkippedRows.ToString(CultureInfo.InvariantCulture));

            // Nothing may be lost on the way in: what is in the database must add up to what was read.
            var files = HistoryDatabase.Scalar(connection, "SELECT COALESCE(SUM(files), 0) FROM runs", transaction);
            var count = HistoryDatabase.Scalar(connection, "SELECT COUNT(*) FROM runs", transaction);
            if (count != rows.Count || files != rows.Sum(r => (long)r.FileCount))
                throw new InvalidOperationException($"The history import did not add up (read {rows.Count} runs / {rows.Sum(r => r.FileCount)} files, stored {count} / {files}).");

            transaction.Commit();
        }

        // Kept, not deleted: the CSV and the counter file, renamed so nothing reads them again.
        if (hadCsv) MoveAside(csvPath);
        if (hadCounter) MoveAside(runCountPath);
    }

    /// <summary>Fills in what a run's row lacks from its report: the movie / TV split, and how many files each tool, lane and profile handled. Only when the
    /// report's files add up to the run's file count - the files the history counts (OK, or converted but not moved), or every file row, which is what a few
    /// older builds counted - so a report that does not match is never trusted over the row.</summary>
    internal static RunHistoryRecord Enrich(RunHistoryRecord row, IReadOnlyList<ParsedReportFile> parsed)
    {
        var use = parsed.Where(f => f.Counted).ToList();
        if (use.Count != row.FileCount)
        {
            if (parsed.Count != row.FileCount) return row;
            use = parsed.ToList();
        }

        var result = row;
        if (row.MovieCount is null || row.TvCount is null)
            result = result with { MovieCount = use.Count(f => f.ContentType == "Movie"), TvCount = use.Count(f => f.ContentType == "TV Show") };

        if (row.HandBrakeCount is null)
        {
            var lanes = new Dictionary<string, int>(StringComparer.Ordinal);
            var profiles = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var f in use)
            {
                lanes[f.Lane] = lanes.GetValueOrDefault(f.Lane) + 1;
                if (!f.Passthrough && !string.IsNullOrWhiteSpace(f.Preset)) profiles[f.Preset!] = profiles.GetValueOrDefault(f.Preset!) + 1;
            }
            result = result with
            {
                HandBrakeCount = use.Count(f => f.Engine == "handbrake"),
                FfmpegCount = use.Count(f => f.Engine == "ffmpeg"),
                PassthroughCount = use.Count(f => f.Engine == "passthrough"),
                LaneCounts = lanes,
                PresetCounts = profiles
            };
        }
        return result;
    }

    private const string LogoToken = "@@COMPRESSARR-LOGO@@";
    private const string FaviconToken = "@@COMPRESSARR-FAVICON@@";

    /// <summary>An old report with its embedded logo and favicon replaced by tokens: they are about 20 KB of base64 in every report, and base64 hardly
    /// compresses. <see cref="ExpandPictures"/> puts the current ones back when the report is opened.</summary>
    internal static string ShrinkPictures(string html) =>
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(html, "data:image/png;base64,[A-Za-z0-9+/=]+", "data:image/png;base64," + LogoToken),
            "data:image/x-icon;base64,[A-Za-z0-9+/=]+", "data:image/x-icon;base64," + FaviconToken);

    internal static string ExpandPictures(string html)
    {
        if (html.Contains(LogoToken, StringComparison.Ordinal) && HtmlReportGenerator.LogoBase64() is { } logo) html = html.Replace(LogoToken, logo, StringComparison.Ordinal);
        if (html.Contains(FaviconToken, StringComparison.Ordinal) && HtmlReportGenerator.FaviconBase64() is { } favicon) html = html.Replace(FaviconToken, favicon, StringComparison.Ordinal);
        return html;
    }

    private static void StoreLegacyReport(SqliteConnection connection, SqliteTransaction transaction, long runId, string reportFileName, byte[] html, IReadOnlyList<ParsedReportFile> files)
    {
        byte[] packed;
        using (var output = new MemoryStream())
        {
            using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                gzip.Write(System.Text.Encoding.UTF8.GetBytes(ShrinkPictures(System.Text.Encoding.UTF8.GetString(html))));
            packed = output.ToArray();
        }
        Run(connection, transaction, "UPDATE runs SET legacy_report = @blob, legacy_report_name = @name, has_report = 1 WHERE id = @id",
            ("@blob", packed), ("@name", reportFileName), ("@id", runId));

        // The report's lane sections in the order it showed them, and one row per file (a saved report has no start or finish time per file, only a duration).
        Run(connection, transaction, "DELETE FROM run_lanes WHERE run_id = @id", ("@id", runId));
        var order = files.Select(f => f.Lane).Distinct(StringComparer.Ordinal).ToList();
        for (var ord = 0; ord < order.Count; ord++)
        {
            Run(connection, transaction, "INSERT INTO run_lanes (run_id, ord, lane, files) VALUES (@run, @ord, @lane, @files)",
                ("@run", runId), ("@ord", ord), ("@lane", order[ord]), ("@files", files.Count(f => f.Lane == order[ord] && f.Counted)));
        }
        foreach (var f in files)
        {
            Run(connection, transaction, """
                INSERT INTO run_files (run_id, lane_ord, name, content_type, preset, encoder_label, passthrough, engine, before_gb, after_gb, seconds, success, counted,
                                       error_code, warning_text, warning_codes, arr_status, redirected_to_lane, home_lane)
                VALUES (@run, @ord, @name, @type, @preset, @label, @pass, @engine, @before, @after, @seconds, @success, @counted,
                        @code, @warning, @warningCodes, @arr, @redirect, @home)
                """,
                ("@run", runId), ("@ord", order.IndexOf(f.Lane)), ("@name", f.Name), ("@type", f.ContentType), ("@preset", f.Preset), ("@label", f.EncoderLabel),
                ("@pass", f.Passthrough ? 1 : 0), ("@engine", f.Engine), ("@before", f.BeforeGb), ("@after", f.AfterGb), ("@seconds", f.Seconds),
                ("@success", f.Success ? 1 : 0), ("@counted", f.Counted ? 1 : 0), ("@code", f.ErrorCode), ("@warning", f.WarningText),
                ("@warningCodes", f.WarningCodes.Count == 0 ? null : string.Join(",", f.WarningCodes)), ("@arr", f.ArrStatus),
                ("@redirect", f.RedirectedToLane), ("@home", f.HomeLane));
        }
    }

    private static void MoveAside(string path)
    {
        try { File.Move(path, path + ".imported", overwrite: true); }
        catch { /* still there is harmless: the database already exists, so nothing imports it twice */ }
    }

    // ---- reading and writing runs --------------------------------------------------------------------------------------------------------

    internal sealed record StoredRun(long Id, RunHistoryRecord Record);

    internal static long InsertRun(SqliteConnection connection, SqliteTransaction transaction, RunHistoryRecord run, bool totals = true)
    {
        var seconds = run.ProcessHours * 3600 + run.ProcessMinutes * 60 + run.ProcessSeconds;
        var counted = !run.IsAllFailed;
        var known = run.HandBrakeCount is not null;
        long id;

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO runs (kind, counted, run_number, ended_date, ended_at, seconds, files, before_gb, after_gb, errors, warnings, redirects,
                                  movies, tvs, avg_fps, hb_files, ff_files, pass_files, report_file)
                VALUES ('run', @counted, @number, @date, @endedAt, @seconds, @files, @before, @after, @errors, @warnings, @redirects,
                        @movies, @tvs, @fps, @hb, @ff, @pass, @report);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("@counted", counted ? 1 : 0);
            command.Parameters.AddWithValue("@number", run.RunNumber);
            command.Parameters.AddWithValue("@date", HistoryDatabase.Day(run.Year, run.Month, run.Day));
            command.Parameters.AddWithValue("@endedAt", RunTimes.EndedAt(run) is { } end ? HistoryDatabase.Iso(end) : DBNull.Value);
            command.Parameters.AddWithValue("@seconds", seconds);
            command.Parameters.AddWithValue("@files", run.FileCount);
            command.Parameters.AddWithValue("@before", run.BeginSizeGb);
            command.Parameters.AddWithValue("@after", run.EndSizeGb);
            command.Parameters.AddWithValue("@errors", run.ErrorCount);
            command.Parameters.AddWithValue("@warnings", run.WarningCount);
            command.Parameters.AddWithValue("@redirects", run.RedirectCount);
            command.Parameters.AddWithValue("@movies", (object?)run.MovieCount ?? DBNull.Value);
            command.Parameters.AddWithValue("@tvs", (object?)run.TvCount ?? DBNull.Value);
            command.Parameters.AddWithValue("@fps", (object?)run.AvgFps ?? DBNull.Value);
            command.Parameters.AddWithValue("@hb", (object?)run.HandBrakeCount ?? DBNull.Value);
            command.Parameters.AddWithValue("@ff", (object?)run.FfmpegCount ?? DBNull.Value);
            command.Parameters.AddWithValue("@pass", (object?)run.PassthroughCount ?? DBNull.Value);
            command.Parameters.AddWithValue("@report", string.IsNullOrEmpty(run.ReportFileName) ? DBNull.Value : run.ReportFileName);
            id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (known)
        {
            var order = 0;
            foreach (var (lane, files) in run.LaneCounts ?? new Dictionary<string, int>())
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO run_lanes (run_id, ord, lane, files) VALUES (@run, @ord, @lane, @files);";
                command.Parameters.AddWithValue("@run", id);
                command.Parameters.AddWithValue("@ord", order++);
                command.Parameters.AddWithValue("@lane", lane);
                command.Parameters.AddWithValue("@files", files);
                command.ExecuteNonQuery();
            }
            foreach (var (profile, files) in run.PresetCounts ?? new Dictionary<string, int>())
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO run_profiles (run_id, profile, files) VALUES (@run, @profile, @files);";
                command.Parameters.AddWithValue("@run", id);
                command.Parameters.AddWithValue("@profile", profile);
                command.Parameters.AddWithValue("@files", files);
                command.ExecuteNonQuery();
            }
        }

        if (totals && counted) HistoryDatabase.AddToTotals(connection, transaction, run);
        return id;
    }

    internal static List<StoredRun> ReadRuns(SqliteConnection connection, string where)
    {
        var lanes = ReadCounts(connection, "SELECT run_id, lane, files FROM run_lanes WHERE files > 0 ORDER BY run_id, ord");
        var profiles = ReadCounts(connection, "SELECT run_id, profile, files FROM run_profiles ORDER BY run_id");

        var runs = new List<StoredRun>();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, run_number, ended_date, seconds, files, before_gb, after_gb, errors, warnings, redirects, movies, tvs, avg_fps,
                   hb_files, ff_files, pass_files, report_file, has_report
            FROM runs WHERE {where} ORDER BY id
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var date = DateTime.ParseExact(reader.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var seconds = reader.GetInt32(3);
            int? Int(int i) => reader.IsDBNull(i) ? null : reader.GetInt32(i);
            var known = !reader.IsDBNull(13);

            runs.Add(new StoredRun(id, new RunHistoryRecord(
                date.Year, date.Month, date.Day, reader.GetDouble(5), reader.GetDouble(6), reader.GetInt32(4),
                seconds / 3600, seconds % 3600 / 60, seconds % 60,
                RunNumber: reader.GetInt32(1),
                ReportFileName: reader.IsDBNull(16) ? "" : reader.GetString(16),
                ErrorCount: reader.GetInt32(7), WarningCount: reader.GetInt32(8), RedirectCount: reader.GetInt32(9),
                MovieCount: Int(10), TvCount: Int(11),
                AvgFps: reader.IsDBNull(12) ? null : reader.GetDouble(12),
                HandBrakeCount: Int(13), FfmpegCount: Int(14), PassthroughCount: Int(15),
                LaneCounts: known ? lanes.GetValueOrDefault(id) ?? new Dictionary<string, int>() : null,
                PresetCounts: known ? profiles.GetValueOrDefault(id) ?? new Dictionary<string, int>() : null,
                Id: id, HasReport: reader.GetInt32(17) != 0)));
        }
        return runs;
    }

    private static Dictionary<long, Dictionary<string, int>> ReadCounts(SqliteConnection connection, string sql)
    {
        var result = new Dictionary<long, Dictionary<string, int>>();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            if (!result.TryGetValue(id, out var counts)) result[id] = counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var name = reader.GetString(1);
            counts[name] = counts.GetValueOrDefault(name) + reader.GetInt32(2);   // two sections can share a lane name
        }
        return result;
    }

    internal static void RebuildTotals(SqliteConnection connection, SqliteTransaction transaction)
    {
        HistoryDatabase.ClearTotals(connection, transaction);
        foreach (var stored in ReadRuns(connection, "kind = 'run' AND counted = 1")) HistoryDatabase.AddToTotals(connection, transaction, stored.Record);
    }

    private static long ReadCounter(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT value FROM meta WHERE key = '{RunCounterKey}'";
        var value = command.ExecuteScalar();
        return value is string text && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    private static void SetMeta(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO meta (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        command.ExecuteNonQuery();
    }
}
