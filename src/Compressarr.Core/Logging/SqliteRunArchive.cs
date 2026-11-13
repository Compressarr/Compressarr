using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Compressarr.Core.Conversion;
using Compressarr.Core.Reporting;
using Microsoft.Data.Sqlite;

namespace Compressarr.Core.Logging;

// The run archive half of the store: what a report shows, kept as rows, and the report rebuilt from them.
public sealed partial class SqliteRunHistoryStore
{
    private sealed record Rollups(HistoryRollup? Today, HistoryRollup? ThisMonth, HistoryRollup? ThisYear);

    private const string MillisecondFormat = "yyyy-MM-dd'T'HH:mm:ss.fff";

    public void SaveReport(long runId, ReportModel report)
    {
        if (runId <= 0 || !Ready(null)) return;
        using var connection = HistoryDatabase.Open(_databasePath());
        using var transaction = connection.BeginTransaction();
        WriteReport(connection, transaction, runId, report);
        transaction.Commit();
    }

    public long RecordPass(ReportModel report)
    {
        if (!Ready(null)) return 0;
        using var connection = HistoryDatabase.Open(_databasePath());
        using var transaction = connection.BeginTransaction();
        var ended = report.GeneratedAt;
        long id;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO runs (kind, counted, run_number, ended_date, ended_at, seconds, files, errors, has_report)
                VALUES ('pass', 0, 0, @date, @at, @seconds, 0, @errors, 1);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("@date", ended.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@at", ended.ToString(MillisecondFormat, CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("@seconds", (int)report.RunTime.TotalSeconds);
            command.Parameters.AddWithValue("@errors", report.ErrorCount);
            id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        WriteReport(connection, transaction, id, report);
        transaction.Commit();
        return id;
    }

    public void SetSavedCopy(long runId, string fileName)
    {
        if (runId <= 0 || !Ready(null)) return;
        using var connection = HistoryDatabase.Open(_databasePath());
        Run(connection, null, "UPDATE runs SET report_file = @file WHERE id = @id", ("@file", fileName), ("@id", runId));
    }

    private static void WriteReport(SqliteConnection connection, SqliteTransaction transaction, long runId, ReportModel report)
    {
        Run(connection, transaction, "DELETE FROM run_files WHERE run_id = @id", ("@id", runId));
        Run(connection, transaction, "DELETE FROM run_lanes WHERE run_id = @id", ("@id", runId));
        Run(connection, transaction, "UPDATE runs SET has_report = 1, ended_at = @at, retries_succeeded = @retries, summary_log = @log, rollups = @rollups WHERE id = @id",
            ("@at", report.GeneratedAt.ToString(MillisecondFormat, CultureInfo.InvariantCulture)),
            ("@retries", report.RetriesSucceeded),
            ("@log", report.SummaryLogFilePath),
            ("@rollups", JsonSerializer.Serialize(new Rollups(report.Today, report.ThisMonth, report.ThisYear))),
            ("@id", runId));

        for (var ord = 0; ord < report.Lanes.Count; ord++)
        {
            var section = report.Lanes[ord];
            Run(connection, transaction, "INSERT INTO run_lanes (run_id, ord, lane, files, problems) VALUES (@run, @ord, @lane, @files, @problems)",
                ("@run", runId), ("@ord", ord), ("@lane", section.LaneDisplayName),
                ("@files", section.Results.Count(r => r.CountsTowardTotals)),
                ("@problems", section.LaneProblems.Count == 0 ? null : string.Join(",", section.LaneProblems.Select(p => (int)p))));

            foreach (var r in section.Results)
            {
                Run(connection, transaction, """
                    INSERT INTO run_files (run_id, lane_ord, lane_id, name, content_type, preset, encoder_label, passthrough, engine, before_gb, after_gb, seconds,
                                           started_at, ended_at, success, counted, avg_fps, error_code, failure_reason, warning_text, warning_codes, arr_status,
                                           detail_log, disk_full, redirected_to_lane_id, redirected_to_lane, home_lane)
                    VALUES (@run, @ord, @laneId, @name, @type, @preset, @label, @pass, @engine, @before, @after, @seconds,
                            @started, @ended, @success, @counted, @fps, @code, @failure, @warning, @warningCodes, @arr,
                            @log, @disk, @redirectId, @redirectName, @home)
                    """,
                    ("@run", runId), ("@ord", ord), ("@laneId", r.LaneId), ("@name", r.FileName), ("@type", r.ContentType), ("@preset", r.PresetName),
                    ("@label", r.EncoderLabel), ("@pass", r.IsPassthrough ? 1 : 0), ("@engine", r.Engine.ToString().ToLowerInvariant()),
                    ("@before", r.BeginSizeGb), ("@after", r.EndSizeGb), ("@seconds", Math.Abs((r.EndTime - r.StartTime).TotalSeconds)),
                    ("@started", r.StartTime.ToString(MillisecondFormat, CultureInfo.InvariantCulture)),
                    ("@ended", r.EndTime.ToString(MillisecondFormat, CultureInfo.InvariantCulture)),
                    ("@success", r.Success ? 1 : 0), ("@counted", r.CountsTowardTotals ? 1 : 0), ("@fps", r.AvgFps),
                    ("@code", r.ErrorCode is { } code ? (int)code : null), ("@failure", r.FailureReason), ("@warning", r.PostProcessWarning),
                    ("@warningCodes", r.WarningCodes.Count == 0 ? null : string.Join(",", r.WarningCodes.Select(c => (int)c))),
                    ("@arr", r.ArrStatus), ("@log", r.DetailLogFile), ("@disk", r.DiskFull ? 1 : 0),
                    ("@redirectId", r.RedirectedToLaneId), ("@redirectName", r.RedirectedToLaneName), ("@home", r.HomeLaneName));
            }
        }
    }

    public ReportModel? LoadReport(long runId)
    {
        if (!Ready(null)) return null;
        using var connection = HistoryDatabase.Open(_databasePath());

        DateTime endedAt; int seconds, runNumber, retries; string? summaryLog, rollupsJson; bool hasReport;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT has_report, ended_date, ended_at, seconds, run_number, retries_succeeded, summary_log, rollups FROM runs WHERE id = @id";
            command.Parameters.AddWithValue("@id", runId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            hasReport = reader.GetInt32(0) != 0;
            endedAt = reader.IsDBNull(2) ? DateTime.ParseExact(reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture) : ParseTime(reader.GetString(2));
            seconds = reader.GetInt32(3);
            runNumber = reader.GetInt32(4);
            retries = reader.GetInt32(5);
            summaryLog = reader.IsDBNull(6) ? null : reader.GetString(6);
            rollupsJson = reader.IsDBNull(7) ? null : reader.GetString(7);
        }
        if (!hasReport) return null;

        var filesBySection = new Dictionary<int, List<ConversionResult>>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT lane_ord, lane_id, name, content_type, preset, encoder_label, passthrough, before_gb, after_gb, started_at, ended_at, success, avg_fps,
                       error_code, failure_reason, warning_text, warning_codes, arr_status, detail_log, disk_full, redirected_to_lane_id, redirected_to_lane, home_lane,
                       id, scrubbed
                FROM run_files WHERE run_id = @id ORDER BY id
                """;
            command.Parameters.AddWithValue("@id", runId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                var ord = reader.GetInt32(0);
                var warningCodes = Text(16) is { Length: > 0 } wc
                    ? wc.Split(',').Select(c => (ReportWarningCode)int.Parse(c, CultureInfo.InvariantCulture)).ToList()
                    : new List<ReportWarningCode>();
                var result = new ConversionResult
                {
                    LaneId = Text(1) ?? Text(22) ?? "",
                    FileName = reader.GetString(2),
                    FullName = reader.GetString(2),
                    ContentType = reader.GetString(3),
                    PresetName = Text(4),
                    EncoderLabel = Text(5),
                    IsPassthrough = reader.GetInt32(6) != 0,
                    BeginSizeGb = reader.GetDouble(7),
                    EndSizeGb = reader.GetDouble(8),
                    StartTime = reader.IsDBNull(9) ? endedAt : ParseTime(reader.GetString(9)),
                    EndTime = reader.IsDBNull(10) ? endedAt : ParseTime(reader.GetString(10)),
                    Success = reader.GetInt32(11) != 0,
                    AvgFps = reader.IsDBNull(12) ? null : reader.GetDouble(12),
                    ErrorCode = reader.IsDBNull(13) ? null : (ReportErrorCode)reader.GetInt32(13),
                    FailureReason = Text(14),
                    PostProcessWarning = Text(15),
                    WarningCodes = warningCodes,
                    ArrStatus = Text(17),
                    DetailLogFile = Text(18),
                    DiskFull = reader.GetInt32(19) != 0,
                    RedirectedToLaneId = Text(20),
                    RedirectedToLaneName = Text(21),
                    HomeLaneName = Text(22),
                    ArchiveRowId = reader.GetInt64(23),
                    IsScrubbed = reader.GetInt32(24) != 0
                };
                if (!filesBySection.TryGetValue(ord, out var list)) filesBySection[ord] = list = new List<ConversionResult>();
                list.Add(result);
            }
        }

        var sections = new List<LaneReportSection>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT ord, lane, problems FROM run_lanes WHERE run_id = @id ORDER BY ord";
            command.Parameters.AddWithValue("@id", runId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var problems = reader.IsDBNull(2) || reader.GetString(2).Length == 0
                    ? new List<ReportErrorCode>()
                    : reader.GetString(2).Split(',').Select(c => (ReportErrorCode)int.Parse(c, CultureInfo.InvariantCulture)).ToList();
                sections.Add(new LaneReportSection
                {
                    LaneDisplayName = reader.GetString(1),
                    Results = filesBySection.GetValueOrDefault(reader.GetInt32(0)) ?? new List<ConversionResult>(),
                    LaneProblems = problems
                });
            }
        }

        Rollups? rollups = null;
        if (rollupsJson is not null)
        {
            try { rollups = JsonSerializer.Deserialize<Rollups>(rollupsJson); }
            catch (JsonException) { /* the History table is simply left off an unreadable one */ }
        }

        return new ReportModel
        {
            GeneratedAt = endedAt,
            RunTime = TimeSpan.FromSeconds(seconds),
            RunNumber = runNumber,
            Lanes = sections,
            SummaryLogFilePath = summaryLog,
            RetriesSucceeded = retries,
            Today = rollups?.Today,
            ThisMonth = rollups?.ThisMonth,
            ThisYear = rollups?.ThisYear
        };
    }

    public byte[]? LoadLegacyReportHtml(long runId)
    {
        if (!Ready(null)) return null;
        using var connection = HistoryDatabase.Open(_databasePath());
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT legacy_report FROM runs WHERE id = @id AND legacy_report IS NOT NULL";
        command.Parameters.AddWithValue("@id", runId);
        if (command.ExecuteScalar() is not byte[] packed) return null;

        using var input = new MemoryStream(packed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return System.Text.Encoding.UTF8.GetBytes(ExpandPictures(System.Text.Encoding.UTF8.GetString(output.ToArray())));
    }

    public IReadOnlyList<ReportListEntry> ListReports(DateTime since) => QueryReports("", since, null, null);

    // A run with problems whose problem files have all been scrubbed (3.1) is resolved: it drops out of the problem lists and the sidebar badges.
    // (A run with no stored files, such as one imported from an old saved report, has nothing to scrub and keeps counting.)
    private const string ResolvedByScrub = """
        (EXISTS (SELECT 1 FROM run_files f WHERE f.run_id = runs.id AND (f.success = 0 OR f.warning_text IS NOT NULL))
         AND NOT EXISTS (SELECT 1 FROM run_files f WHERE f.run_id = runs.id AND (f.success = 0 OR f.warning_text IS NOT NULL) AND f.scrubbed = 0))
        """;

    public IReadOnlyList<ReportListEntry> ListProblemReports(int? newerThanRunNumber, DateTime since, int limit) =>
        QueryReports($"AND (errors > 0 OR warnings > 0) AND NOT {ResolvedByScrub}", since, newerThanRunNumber, limit);

    public string? ScrubFile(long runId, long fileRowId)
    {
        if (!Ready(null)) return null;
        using var connection = HistoryDatabase.Open(_databasePath());
        using var transaction = connection.BeginTransaction();
        string? detailLog;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT detail_log FROM run_files WHERE id = @file AND run_id = @run";
            read.Parameters.AddWithValue("@file", fileRowId);
            read.Parameters.AddWithValue("@run", runId);
            var found = read.ExecuteScalar();
            if (found is null) return null;                                   // no such file in that run
            detailLog = found is DBNull ? "" : (string)found;
        }

        Run(connection, transaction, """
            UPDATE run_files SET
                name = 'Removed',
                failure_reason = CASE WHEN failure_reason IS NULL THEN NULL ELSE 'Removed' END,
                warning_text   = CASE WHEN warning_text   IS NULL THEN NULL ELSE 'Removed' END,
                arr_status     = CASE WHEN arr_status     IS NULL THEN NULL ELSE 'Removed' END,
                detail_log = NULL,
                scrubbed = 1
            WHERE id = @file AND run_id = @run
            """, ("@file", fileRowId), ("@run", runId));
        transaction.Commit();
        return detailLog;
    }

    private IReadOnlyList<ReportListEntry> QueryReports(string extraCondition, DateTime since, int? newerThanRunNumber, int? limit)
    {
        if (!Ready(null)) return Array.Empty<ReportListEntry>();
        using var connection = HistoryDatabase.Open(_databasePath());
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, run_number, files, before_gb, after_gb, ended_date, errors, warnings, redirects
            FROM runs WHERE kind = 'run' AND has_report = 1 AND ended_date >= @since {extraCondition}
              AND (@newer IS NULL OR run_number > @newer)
            ORDER BY run_number DESC, id DESC
            {(limit is null ? "" : "LIMIT @limit")}
            """;
        command.Parameters.AddWithValue("@since", since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("@newer", newerThanRunNumber is { } n ? n : DBNull.Value);
        if (limit is { } l) command.Parameters.AddWithValue("@limit", l);
        var entries = new List<ReportListEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            double before = reader.GetDouble(3), after = reader.GetDouble(4);
            entries.Add(new ReportListEntry(
                RunNumber: reader.GetInt32(1),
                ReportFileName: reader.GetInt64(0).ToString(CultureInfo.InvariantCulture),
                FileCount: reader.GetInt32(2),
                BeforeGb: before,
                AfterGb: after,
                SavedPercent: before > 0 ? Math.Round(100 - after / before * 100, 1) : 0,
                Date: DateTime.ParseExact(reader.GetString(5), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                ErrorCount: reader.GetInt32(6),
                WarningCount: reader.GetInt32(7),
                RedirectCount: reader.GetInt32(8)));
        }
        return entries;
    }

    public (int ErrorRuns, int WarningRuns, int UnseenRuns) CountUnseen(int viewedThroughRunNumber, DateTime since)
    {
        if (!Ready(null)) return (0, 0, 0);
        using var connection = HistoryDatabase.Open(_databasePath());
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT COALESCE(SUM(errors > 0 AND NOT {ResolvedByScrub}), 0), COALESCE(SUM(warnings > 0 AND NOT {ResolvedByScrub}), 0), COUNT(*) FROM runs
            WHERE kind = 'run' AND has_report = 1 AND run_number > @viewed AND ended_date >= @since
            """;
        command.Parameters.AddWithValue("@viewed", viewedThroughRunNumber);
        command.Parameters.AddWithValue("@since", since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    public IReadOnlyList<FileSample> RecentFileSamples(int runs)
    {
        if (!Ready(null)) return Array.Empty<FileSample>();
        using var connection = HistoryDatabase.Open(_databasePath());
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT preset, before_gb, seconds FROM run_files
            WHERE run_id IN (SELECT id FROM runs WHERE has_report = 1 ORDER BY id DESC LIMIT @runs)
              AND success = 1 AND passthrough = 0 AND seconds > 0
            """;
        command.Parameters.AddWithValue("@runs", runs);
        var samples = new List<FileSample>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) samples.Add(new FileSample(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2)));
        return samples;
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------------------------

    private static DateTime ParseTime(string text) =>
        DateTime.TryParseExact(text, MillisecondFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var withMs) ? withMs
        : DateTime.ParseExact(text, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    private static void Run(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
