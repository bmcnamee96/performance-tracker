using System.Globalization;
using Microsoft.Data.Sqlite;
using PerformanceTracker.Core.Configuration;

namespace PerformanceTracker.Infrastructure.Storage;

/// <summary>What one retention pass removed.</summary>
public sealed record RetentionResult(long SamplesRemoved, long EventsRemoved, long SummariesRemoved, bool Compacted);

public sealed partial class SqliteSampleRepository
{
    private const int PruneBatchSize = 20_000;
    private const long CompactAfterRows = 100_000;

    /// <summary>Creates the daily summary table and the indexes retention relies on. Safe to call repeatedly.</summary>
    public void EnsureRetentionSchema()
    {
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS daily_summaries (
              day TEXT NOT NULL PRIMARY KEY,
              sample_count INTEGER NOT NULL,
              cpu_count INTEGER NOT NULL DEFAULT 0,
              cpu_avg REAL NULL,
              cpu_max REAL NULL,
              memory_avg REAL NULL,
              memory_max REAL NULL,
              disk_count INTEGER NOT NULL DEFAULT 0,
              disk_avg REAL NULL,
              disk_max REAL NULL
            );
            CREATE INDEX IF NOT EXISTS ix_metric_samples_timestamp ON metric_samples (timestamp_utc);
            CREATE INDEX IF NOT EXISTS ix_performance_events_ended ON performance_events (ended_at_utc);
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Deletes data older than the retention settings. Detailed readings are folded into one row per local day
    /// before they are deleted, so long-term averages survive. Runs in small batches so the collector is never
    /// blocked for long, and compacts the file once after a large clean-up.
    /// </summary>
    public RetentionResult ApplyRetention(RetentionSettings settings, DateTimeOffset now, CancellationToken cancellationToken)
    {
        EnsureRetentionSchema();
        string sampleCutoff = ToStoredTimestamp(now.AddHours(-Math.Max(1, settings.RawSampleRetentionHours)));
        string eventCutoff = ToStoredTimestamp(now.AddDays(-Math.Max(1, settings.EventRetentionDays)));
        string summaryCutoff = now.LocalDateTime.Date.AddDays(-Math.Max(1, settings.DailySummaryRetentionDays))
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        long samplesRemoved = 0;
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        while (!cancellationToken.IsCancellationRequested)
        {
            int removed = SummarizeAndDeleteBatch(connection, sampleCutoff);
            samplesRemoved += removed;
            if (removed < PruneBatchSize)
            {
                break;
            }
        }

        long eventsRemoved = Execute(connection, "DELETE FROM performance_events WHERE ended_at_utc < $cutoff;", eventCutoff);
        long summariesRemoved = Execute(connection, "DELETE FROM daily_summaries WHERE day < $cutoff;", summaryCutoff);

        bool compacted = false;
        if (samplesRemoved + eventsRemoved >= CompactAfterRows && !cancellationToken.IsCancellationRequested)
        {
            using SqliteCommand vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM;";
            vacuum.CommandTimeout = 600;
            vacuum.ExecuteNonQuery();
            compacted = true;
        }

        return new RetentionResult(samplesRemoved, eventsRemoved, summariesRemoved, compacted);
    }

    /// <summary>Daily averages kept after detailed readings are deleted, newest first.</summary>
    public IReadOnlyList<DailySummary> ReadDailySummaries(int days)
    {
        if (!File.Exists(_databasePath))
        {
            return [];
        }

        EnsureRetentionSchema();
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT day, sample_count, cpu_avg, cpu_max, memory_avg, memory_max, disk_avg, disk_max FROM daily_summaries ORDER BY day DESC LIMIT $days;";
        command.Parameters.AddWithValue("$days", days);
        using SqliteDataReader reader = command.ExecuteReader();
        List<DailySummary> result = [];
        while (reader.Read())
        {
            result.Add(new DailySummary(
                DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                reader.GetInt64(1),
                NullableDouble(reader, 2), NullableDouble(reader, 3),
                NullableDouble(reader, 4), NullableDouble(reader, 5),
                NullableDouble(reader, 6), NullableDouble(reader, 7)));
        }

        return result;
    }

    private static int SummarizeAndDeleteBatch(SqliteConnection connection, string cutoff)
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$cutoff", cutoff);
        command.Parameters.AddWithValue("$limit", PruneBatchSize);

        command.CommandText =
            """
            CREATE TEMP TABLE IF NOT EXISTS prune_batch (id INTEGER PRIMARY KEY);
            DELETE FROM prune_batch;
            INSERT INTO prune_batch (id)
              SELECT rowid FROM metric_samples WHERE timestamp_utc < $cutoff ORDER BY timestamp_utc LIMIT $limit;
            """;
        command.ExecuteNonQuery();

        // Fold the batch into per-day rows, combining with what earlier batches already stored for that day.
        command.CommandText =
            """
            INSERT INTO daily_summaries (day, sample_count, cpu_count, cpu_avg, cpu_max, memory_avg, memory_max, disk_count, disk_avg, disk_max)
            SELECT date(s.timestamp_utc, 'localtime'), COUNT(*),
                   COUNT(s.cpu_usage_percent), AVG(s.cpu_usage_percent), MAX(s.cpu_usage_percent),
                   AVG(s.memory_used_percent), MAX(s.memory_used_percent),
                   COUNT(s.disk_active_time_percent), AVG(s.disk_active_time_percent), MAX(s.disk_active_time_percent)
            FROM metric_samples s JOIN prune_batch b ON b.id = s.rowid
            WHERE date(s.timestamp_utc, 'localtime') IS NOT NULL
            GROUP BY date(s.timestamp_utc, 'localtime')
            ON CONFLICT(day) DO UPDATE SET
              cpu_avg = CASE WHEN cpu_count + excluded.cpu_count = 0 THEN NULL
                ELSE (COALESCE(cpu_avg, 0) * cpu_count + COALESCE(excluded.cpu_avg, 0) * excluded.cpu_count) / (cpu_count + excluded.cpu_count) END,
              cpu_max = MAX(COALESCE(cpu_max, excluded.cpu_max), COALESCE(excluded.cpu_max, cpu_max)),
              memory_avg = (COALESCE(memory_avg, 0) * sample_count + COALESCE(excluded.memory_avg, 0) * excluded.sample_count) / (sample_count + excluded.sample_count),
              memory_max = MAX(COALESCE(memory_max, excluded.memory_max), COALESCE(excluded.memory_max, memory_max)),
              disk_avg = CASE WHEN disk_count + excluded.disk_count = 0 THEN NULL
                ELSE (COALESCE(disk_avg, 0) * disk_count + COALESCE(excluded.disk_avg, 0) * excluded.disk_count) / (disk_count + excluded.disk_count) END,
              disk_max = MAX(COALESCE(disk_max, excluded.disk_max), COALESCE(excluded.disk_max, disk_max)),
              sample_count = sample_count + excluded.sample_count,
              cpu_count = cpu_count + excluded.cpu_count,
              disk_count = disk_count + excluded.disk_count;
            """;
        command.ExecuteNonQuery();

        command.CommandText = "DELETE FROM metric_samples WHERE rowid IN (SELECT id FROM prune_batch);";
        int removed = command.ExecuteNonQuery();
        transaction.Commit();
        return removed;
    }

    private static long Execute(SqliteConnection connection, string sql, string cutoff)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$cutoff", cutoff);
        return command.ExecuteNonQuery();
    }

    private static string ToStoredTimestamp(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static double? NullableDouble(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
}

/// <summary>Averages and peaks for one local day, kept after the detailed readings are deleted.</summary>
public sealed record DailySummary(
    DateOnly Day,
    long SampleCount,
    double? CpuAverage,
    double? CpuPeak,
    double? MemoryAverage,
    double? MemoryPeak,
    double? DiskAverage,
    double? DiskPeak);
