using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Compressarr.Core.Logging;

public sealed partial class SqliteRunHistoryStore : IRunQueries
{
    private const string RunColumns = """
        id, run_number, ended_date, ended_at, seconds, files, before_gb, after_gb, errors, warnings, redirects, movies, tvs, avg_fps,
        hb_files, ff_files, pass_files, counted, has_report, videos
        """;

    private static string Day(DateTime value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public (IReadOnlyList<RunSummary> Runs, int Total, int Page) ListRuns(DateTime? fromDay, DateTime? toDay, int page, int pageSize)
    {
        if (!Ready(null)) return (Array.Empty<RunSummary>(), 0, 1);

        pageSize = Math.Clamp(pageSize, 1, 200);
        using var connection = HistoryDatabase.Open(_databasePath());

        const string where = "kind = 'run' AND (@from IS NULL OR ended_date >= @from) AND (@to IS NULL OR ended_date <= @to)";
        void Bind(SqliteCommand command)
        {
            command.Parameters.AddWithValue("@from", fromDay is { } f ? Day(f) : DBNull.Value);
            command.Parameters.AddWithValue("@to", toDay is { } t ? Day(t) : DBNull.Value);
        }

        int total;
        using (var count = connection.CreateCommand())
        {
            count.CommandText = $"SELECT COUNT(*) FROM runs WHERE {where};";
            Bind(count);
            total = Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        var lastPage = Math.Max(1, (total + pageSize - 1) / pageSize);
        var shown = Math.Clamp(page, 1, lastPage);

        var runs = new List<RunSummary>();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {RunColumns} FROM runs WHERE {where} ORDER BY id DESC LIMIT @take OFFSET @skip;";
        Bind(command);
        command.Parameters.AddWithValue("@take", pageSize);
        command.Parameters.AddWithValue("@skip", (shown - 1) * pageSize);
        using var reader = command.ExecuteReader();
        while (reader.Read()) runs.Add(ReadSummary(reader));
        return (runs, total, shown);
    }

    public RunDetail? GetRun(long id)
    {
        if (!Ready(null)) return null;

        using var connection = HistoryDatabase.Open(_databasePath());
        RunSummary? run;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {RunColumns} FROM runs WHERE id = @id AND kind = 'run';";
            command.Parameters.AddWithValue("@id", id);
            using var reader = command.ExecuteReader();
            run = reader.Read() ? ReadSummary(reader) : null;
        }

        if (run is null) return null;

        var sections = new List<(int Ord, string Name, string? Problems)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT ord, lane, problems FROM run_lanes WHERE run_id = @id ORDER BY ord;";
            command.Parameters.AddWithValue("@id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read()) sections.Add((reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        var filesByLane = new Dictionary<int, List<RunFileDetail>>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT lane_ord, name, content_type, preset, encoder_label, engine, passthrough, before_gb, after_gb, seconds, started_at, ended_at, success, counted,
                       avg_fps, error_code, failure_reason, warning_text, arr_status, redirected_to_lane, home_lane, disk_full
                FROM run_files WHERE run_id = @id ORDER BY id;
                """;
            command.Parameters.AddWithValue("@id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string? Text(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                DateTime? Time(int i) => reader.IsDBNull(i) ? null : DateTime.Parse(reader.GetString(i), CultureInfo.InvariantCulture, DateTimeStyles.None);

                var file = new RunFileDetail(
                    Name: reader.GetString(1), ContentType: reader.GetString(2), Preset: Text(3), Encoder: Text(4), Engine: reader.GetString(5),
                    Passthrough: reader.GetInt32(6) != 0, BeforeGb: reader.GetDouble(7), AfterGb: reader.GetDouble(8), Seconds: reader.GetDouble(9),
                    StartedAt: Time(10), EndedAt: Time(11), Success: reader.GetInt32(12) != 0, Counted: reader.GetInt32(13) != 0,
                    AvgFps: reader.IsDBNull(14) ? null : reader.GetDouble(14), ErrorCode: reader.IsDBNull(15) ? null : reader.GetInt32(15),
                    FailureReason: Text(16), Warning: Text(17), ArrStatus: Text(18), RedirectedToLane: Text(19), HomeLane: Text(20), DiskFull: reader.GetInt32(21) != 0);
                var ord = reader.GetInt32(0);
                if (!filesByLane.TryGetValue(ord, out var list)) filesByLane[ord] = list = new List<RunFileDetail>();
                list.Add(file);
            }
        }

        var lanes = sections.Select(s => new RunLaneDetail(
            s.Name,
            string.IsNullOrWhiteSpace(s.Problems) ? Array.Empty<string>() : s.Problems.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            filesByLane.GetValueOrDefault(s.Ord) ?? new List<RunFileDetail>())).ToList();
        return new RunDetail(run, lanes);
    }

    public PeriodStats GetStats(DateTime? fromDay, DateTime? toDay)
    {
        var empty = new PeriodStats(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, Array.Empty<NameCount>(), Array.Empty<NameCount>());
        if (!Ready(null)) return empty;

        using var connection = HistoryDatabase.Open(_databasePath());
        const string window = "(@from IS NULL OR day >= @from) AND (@to IS NULL OR day <= @to)";
        void Bind(SqliteCommand command)
        {
            command.Parameters.AddWithValue("@from", fromDay is { } f ? Day(f) : DBNull.Value);
            command.Parameters.AddWithValue("@to", toDay is { } t ? Day(t) : DBNull.Value);
        }

        PeriodStats totals;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT COALESCE(SUM(runs), 0), COALESCE(SUM(files), 0), COALESCE(SUM(before_gb), 0), COALESCE(SUM(after_gb), 0), COALESCE(SUM(seconds), 0),
                       COALESCE(SUM(movies), 0), COALESCE(SUM(tvs), 0), COALESCE(SUM(unknown_files), 0), COALESCE(SUM(errors), 0), COALESCE(SUM(warnings), 0),
                       COALESCE(SUM(redirects), 0), COALESCE(SUM(hb_files), 0), COALESCE(SUM(ff_files), 0), COALESCE(SUM(pass_files), 0),
                       COALESCE(SUM(fps_weighted), 0), COALESCE(SUM(fps_seconds), 0), COALESCE(SUM(videos), 0)
                FROM daily_totals WHERE {window};
                """;
            Bind(command);
            using var reader = command.ExecuteReader();
            reader.Read();
            var fpsSeconds = reader.GetDouble(15);
            totals = empty with
            {
                Runs = reader.GetInt32(0), Files = reader.GetInt32(1), BeforeGb = Math.Round(reader.GetDouble(2), 3), AfterGb = Math.Round(reader.GetDouble(3), 3),
                Seconds = reader.GetInt64(4), Movies = reader.GetInt32(5), Tvs = reader.GetInt32(6), UnknownSplit = reader.GetInt32(7), Errors = reader.GetInt32(8),
                Warnings = reader.GetInt32(9), Redirects = reader.GetInt32(10), HandBrakeFiles = reader.GetInt32(11), FfmpegFiles = reader.GetInt32(12),
                PassthroughFiles = reader.GetInt32(13), AvgFps = fpsSeconds > 0 ? Math.Round(reader.GetDouble(14) / fpsSeconds, 1) : null,
                Videos = reader.GetInt32(16)
            };
        }

        IReadOnlyList<NameCount> Names(string table, string column)
        {
            var list = new List<NameCount>();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {column}, SUM(files) AS total FROM {table} WHERE {window} GROUP BY {column} ORDER BY total DESC, {column};";
            Bind(command);
            using var reader = command.ExecuteReader();
            while (reader.Read()) list.Add(new NameCount(reader.GetString(0), reader.GetInt32(1)));
            return list;
        }

        return totals with { Lanes = Names("daily_lanes", "lane"), Profiles = Names("daily_profiles", "profile") };
    }

    private static RunSummary ReadSummary(SqliteDataReader reader)
    {
        int? Int(int i) => reader.IsDBNull(i) ? null : reader.GetInt32(i);
        var day = DateTime.ParseExact(reader.GetString(2), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endedAt = reader.IsDBNull(3) ? day : DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.None);
        return new RunSummary(
            Id: reader.GetInt64(0), Number: reader.GetInt32(1), EndedAt: endedAt, DurationSeconds: reader.GetInt32(4), Files: reader.GetInt32(5),
            BeforeGb: Math.Round(reader.GetDouble(6), 3), AfterGb: Math.Round(reader.GetDouble(7), 3), Errors: reader.GetInt32(8), Warnings: reader.GetInt32(9),
            Redirects: reader.GetInt32(10), Movies: Int(11), Tvs: Int(12), AvgFps: reader.IsDBNull(13) ? null : Math.Round(reader.GetDouble(13), 1),
            HandBrakeFiles: Int(14), FfmpegFiles: Int(15), PassthroughFiles: Int(16), Counted: reader.GetInt32(17) != 0, HasReport: reader.GetInt32(18) != 0, Videos: reader.GetInt32(19));
    }
}
