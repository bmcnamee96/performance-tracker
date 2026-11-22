using System.Globalization;
using Microsoft.Data.Sqlite;
using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Infrastructure.Storage;

public sealed partial class SqliteSampleRepository
{
    /// <summary>Full path of the SQLite database file.</summary>
    public string DatabasePath => _databasePath;

    /// <summary>Saved slowdowns that ended at or after <paramref name="since"/>, oldest first.</summary>
    public IReadOnlyList<PerformanceEvent> ReadPerformanceEvents(DateTimeOffset since)
    {
        if (!File.Exists(_databasePath))
        {
            return [];
        }

        using SqliteConnection connection = CreateConnection();
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT started_at_utc, ended_at_utc, severity, summary, likely_cause, foreground_app,
                   trigger_cpu_percent, trigger_memory_percent, trigger_disk_percent
            FROM performance_events
            WHERE ended_at_utc >= $since
            ORDER BY started_at_utc;
            """;
        command.Parameters.AddWithValue("$since", since.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

        using SqliteDataReader reader = command.ExecuteReader();
        List<PerformanceEvent> events = [];
        while (reader.Read())
        {
            events.Add(new PerformanceEvent
            {
                StartedAtUtc = ParseUtc(reader.GetString(0)),
                EndedAtUtc = ParseUtc(reader.GetString(1)),
                Severity = (EventSeverity)reader.GetInt32(2),
                Summary = reader.GetString(3),
                LikelyCause = reader.GetString(4),
                ForegroundApp = reader.IsDBNull(5) ? null : reader.GetString(5),
                TriggerSample = new MetricSample
                {
                    TimestampUtc = ParseUtc(reader.GetString(1)),
                    CpuUsagePercent = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    MemoryUsedPercent = reader.GetDouble(7),
                    DiskActiveTimePercent = reader.IsDBNull(8) ? null : reader.GetDouble(8)
                }
            });
        }

        return events;
    }

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
}
