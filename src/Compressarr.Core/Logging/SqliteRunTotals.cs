using System.Globalization;
using Compressarr.Core.Reporting;

namespace Compressarr.Core.Logging;

public sealed partial class SqliteRunHistoryStore : IRunTotals
{
    public HistoryRollup? Sum(DateTime? from, DateTime? to)
    {
        if (!Ready(null)) return null;
        using var connection = HistoryDatabase.Open(_databasePath());
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(files), 0), COALESCE(SUM(before_gb), 0), COALESCE(SUM(after_gb), 0), COALESCE(SUM(seconds), 0),
                   COALESCE(SUM(movies), 0), COALESCE(SUM(tvs), 0), COALESCE(SUM(unknown_files), 0)
            FROM daily_totals WHERE (@from IS NULL OR day >= @from) AND (@to IS NULL OR day <= @to)
            """;
        command.Parameters.AddWithValue("@from", from is { } f ? f.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("@to", to is { } t ? t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : DBNull.Value);
        using var reader = command.ExecuteReader();
        reader.Read();
        return new HistoryRollup(
            FileCount: reader.GetInt32(0),
            BeforeGb: Math.Round(reader.GetDouble(1), 3),
            AfterGb: Math.Round(reader.GetDouble(2), 3),
            TotalTimeSeconds: reader.GetInt64(3),
            MovieCount: reader.GetInt32(4),
            TvCount: reader.GetInt32(5),
            UnknownCount: reader.GetInt32(6));
    }
}
