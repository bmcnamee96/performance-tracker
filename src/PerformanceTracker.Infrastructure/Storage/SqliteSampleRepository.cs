using Microsoft.Data.Sqlite;
using PerformanceTracker.Core.Interfaces;
using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Infrastructure.Storage;

public sealed class SqliteSampleRepository : ISampleRepository
{
    private readonly string _databasePath;
    private static bool _sqliteInitialized;
    private static readonly object SqliteInitLock = new();

    public SqliteSampleRepository(string databasePath)
    {
        EnsureSqliteInitialized();
        _databasePath = databasePath;
    }

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS metric_samples (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              timestamp_utc TEXT NOT NULL,
              cpu_usage_percent REAL NULL,
              memory_used_percent REAL NOT NULL,
              available_memory_mb INTEGER NOT NULL,
              disk_active_time_percent REAL NULL,
              network_receive_kbps REAL NULL,
              network_send_kbps REAL NULL,
              top_process_name TEXT NULL,
              top_process_memory_mb REAL NULL,
              uptime_seconds INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS performance_events (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              started_at_utc TEXT NOT NULL,
              ended_at_utc TEXT NOT NULL,
              severity INTEGER NOT NULL,
              summary TEXT NOT NULL,
              likely_cause TEXT NOT NULL,
              foreground_app TEXT NULL,
              trigger_cpu_percent REAL NULL,
              trigger_memory_percent REAL NOT NULL,
              trigger_disk_percent REAL NULL
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveMetricSampleAsync(MetricSample sample, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO metric_samples (
              timestamp_utc,
              cpu_usage_percent,
              memory_used_percent,
              available_memory_mb,
              disk_active_time_percent,
              network_receive_kbps,
              network_send_kbps,
              top_process_name,
              top_process_memory_mb,
              uptime_seconds
            )
            VALUES (
              $timestampUtc,
              $cpuUsagePercent,
              $memoryUsedPercent,
              $availableMemoryMb,
              $diskActiveTimePercent,
              $networkReceiveKbps,
              $networkSendKbps,
              $topProcessName,
              $topProcessMemoryMb,
              $uptimeSeconds
            );
            """;

        AddNullable(command, "$cpuUsagePercent", sample.CpuUsagePercent);
        AddNullable(command, "$diskActiveTimePercent", sample.DiskActiveTimePercent);
        AddNullable(command, "$networkReceiveKbps", sample.NetworkReceiveKbps);
        AddNullable(command, "$networkSendKbps", sample.NetworkSendKbps);
        AddNullable(command, "$topProcessMemoryMb", sample.TopProcessMemoryMb);

        command.Parameters.AddWithValue("$timestampUtc", sample.TimestampUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$memoryUsedPercent", sample.MemoryUsedPercent);
        command.Parameters.AddWithValue("$availableMemoryMb", sample.AvailableMemoryMb);
        command.Parameters.AddWithValue("$topProcessName", (object?)sample.TopProcessName ?? DBNull.Value);
        command.Parameters.AddWithValue("$uptimeSeconds", (long)sample.Uptime.TotalSeconds);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SavePerformanceEventAsync(PerformanceEvent performanceEvent, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO performance_events (
              started_at_utc,
              ended_at_utc,
              severity,
              summary,
              likely_cause,
              foreground_app,
              trigger_cpu_percent,
              trigger_memory_percent,
              trigger_disk_percent
            )
            VALUES (
              $startedAtUtc,
              $endedAtUtc,
              $severity,
              $summary,
              $likelyCause,
              $foregroundApp,
              $triggerCpuPercent,
              $triggerMemoryPercent,
              $triggerDiskPercent
            );
            """;

        AddNullable(command, "$triggerCpuPercent", performanceEvent.TriggerSample.CpuUsagePercent);
        AddNullable(command, "$triggerDiskPercent", performanceEvent.TriggerSample.DiskActiveTimePercent);

        command.Parameters.AddWithValue("$startedAtUtc", performanceEvent.StartedAtUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$endedAtUtc", performanceEvent.EndedAtUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$severity", (int)performanceEvent.Severity);
        command.Parameters.AddWithValue("$summary", performanceEvent.Summary);
        command.Parameters.AddWithValue("$likelyCause", performanceEvent.LikelyCause);
        command.Parameters.AddWithValue("$foregroundApp", (object?)performanceEvent.ForegroundApp ?? DBNull.Value);
        command.Parameters.AddWithValue("$triggerMemoryPercent", performanceEvent.TriggerSample.MemoryUsedPercent);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection CreateConnection() => new($"Data Source={_databasePath}");

    private static void AddNullable(SqliteCommand command, string parameterName, double? value)
    {
        command.Parameters.AddWithValue(parameterName, value is null ? DBNull.Value : value);
    }

    private static void EnsureSqliteInitialized()
    {
        if (_sqliteInitialized)
        {
            return;
        }

        lock (SqliteInitLock)
        {
            if (_sqliteInitialized)
            {
                return;
            }

            SQLitePCL.Batteries_V2.Init();
            _sqliteInitialized = true;
        }
    }
}
