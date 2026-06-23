using Microsoft.Data.Sqlite;
using PerformanceTracker.Core.Models;
using PerformanceTracker.Infrastructure.Storage;
using Xunit;

namespace PerformanceTracker.Tests;

public sealed class SqliteSampleRepositoryTests
{
    [Fact]
    public async Task CreatesSchemaAndPersistsRows()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        SqliteSampleRepository repository = new(databasePath);

        try
        {
            await repository.EnsureCreatedAsync(CancellationToken.None);

            MetricSample sample = new()
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                CpuUsagePercent = 91.5,
                CpuFrequencyMHz = 5125,
                CpuQueueLength = 1.75,
                MemoryUsedPercent = 86.2,
                AvailableMemoryMb = 2048,
                CommitUsedPercent = 84.4,
                DiskActiveTimePercent = 95,
                DiskReadMbPerSec = 128.5,
                DiskWriteMbPerSec = 42.2,
                DiskQueueLength = 2.5,
                DiskAverageResponseMs = 17.3,
                NetworkReceiveKbps = 1200,
                NetworkSendKbps = 240,
                NetworkLatencyMs = 38.5,
                NetworkRetransmitsPerSec = 0.2,
                GpuUsagePercent = 98.1,
                GpuMemoryUsedPercent = 87.4,
                GpuDedicatedMemoryMb = 6144,
                GpuSharedMemoryMb = 512,
                GpuTemperatureCelsius = 76.5,
                TopProcessName = "game",
                TopProcessMemoryMb = 1024,
                GameProcessName = "game.exe",
                GamingTelemetryActive = true,
                FramesPerSecond = 141.7,
                FrameTimeMs = 7.1,
                FrameTimeP95Ms = 11.8,
                InputLatencyMs = 18.6,
                RenderLatencyMs = 8.9,
                DisplayRefreshRateHz = 165,
                VariableRefreshRateEnabled = true,
                GameModeEnabled = true,
                Uptime = TimeSpan.FromHours(3)
            };

            PerformanceEvent performanceEvent = new()
            {
                StartedAtUtc = sample.TimestampUtc.AddSeconds(-10),
                EndedAtUtc = sample.TimestampUtc,
                Severity = EventSeverity.Critical,
                Summary = "CPU pressure detected while game was active.",
                LikelyCause = "Likely CPU bottleneck.",
                ForegroundApp = "game",
                TriggerSample = sample
            };

            await repository.SaveMetricSampleAsync(sample, CancellationToken.None);
            await repository.SavePerformanceEventAsync(performanceEvent, CancellationToken.None);

            await using SqliteConnection connection = new($"Data Source={databasePath}");
            await connection.OpenAsync();

            SqliteCommand sampleCountCommand = connection.CreateCommand();
            sampleCountCommand.CommandText = "SELECT COUNT(*) FROM metric_samples;";

            SqliteCommand eventCountCommand = connection.CreateCommand();
            eventCountCommand.CommandText = "SELECT COUNT(*) FROM performance_events;";

            SqliteCommand sampleValuesCommand = connection.CreateCommand();
            sampleValuesCommand.CommandText =
                """
                SELECT
                  cpu_frequency_mhz,
                  gpu_usage_percent,
                  frames_per_second,
                  gaming_telemetry_active,
                  variable_refresh_rate_enabled,
                  game_mode_enabled
                FROM metric_samples
                LIMIT 1;
                """;

            SqliteCommand eventValuesCommand = connection.CreateCommand();
            eventValuesCommand.CommandText =
                """
                SELECT
                  trigger_gpu_percent,
                  trigger_fps,
                  trigger_gaming_telemetry_active,
                  trigger_game_mode_enabled
                FROM performance_events
                LIMIT 1;
                """;

            long sampleCount = (long)(await sampleCountCommand.ExecuteScalarAsync() ?? 0L);
            long eventCount = (long)(await eventCountCommand.ExecuteScalarAsync() ?? 0L);

            Assert.Equal(1, sampleCount);
            Assert.Equal(1, eventCount);

            await using (SqliteDataReader sampleReader = await sampleValuesCommand.ExecuteReaderAsync())
            {
                Assert.True(await sampleReader.ReadAsync());
                Assert.Equal(5125d, sampleReader.GetDouble(0));
                Assert.Equal(98.1d, sampleReader.GetDouble(1));
                Assert.Equal(141.7d, sampleReader.GetDouble(2));
                Assert.Equal(1L, sampleReader.GetInt64(3));
                Assert.Equal(1L, sampleReader.GetInt64(4));
                Assert.Equal(1L, sampleReader.GetInt64(5));
            }

            await using (SqliteDataReader eventReader = await eventValuesCommand.ExecuteReaderAsync())
            {
                Assert.True(await eventReader.ReadAsync());
                Assert.Equal(98.1d, eventReader.GetDouble(0));
                Assert.Equal(141.7d, eventReader.GetDouble(1));
                Assert.Equal(1L, eventReader.GetInt64(2));
                Assert.Equal(1L, eventReader.GetInt64(3));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    [Fact]
    public async Task EnsureCreatedAsyncAddsNewColumnsToLegacySchema()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");

        try
        {
            await using (SqliteConnection legacyConnection = new($"Data Source={databasePath}"))
            {
                await legacyConnection.OpenAsync();

                SqliteCommand command = legacyConnection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE metric_samples (
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

                    CREATE TABLE performance_events (
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

                await command.ExecuteNonQueryAsync();
            }

            SqliteSampleRepository repository = new(databasePath);
            await repository.EnsureCreatedAsync(CancellationToken.None);

            await using SqliteConnection connection = new($"Data Source={databasePath}");
            await connection.OpenAsync();

            HashSet<string> metricColumns = await GetColumnNamesAsync(connection, "metric_samples");
            HashSet<string> eventColumns = await GetColumnNamesAsync(connection, "performance_events");

            Assert.Contains("gpu_usage_percent", metricColumns);
            Assert.Contains("frames_per_second", metricColumns);
            Assert.Contains("gaming_telemetry_active", metricColumns);
            Assert.Contains("trigger_gpu_percent", eventColumns);
            Assert.Contains("trigger_fps", eventColumns);
            Assert.Contains("trigger_gaming_telemetry_active", eventColumns);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath))
            {
                File.Delete(databasePath);
            }
        }
    }

    private static async Task<HashSet<string>> GetColumnNamesAsync(SqliteConnection connection, string tableName)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }
}
