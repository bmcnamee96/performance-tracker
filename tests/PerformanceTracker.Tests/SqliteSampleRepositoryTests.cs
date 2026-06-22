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
                MemoryUsedPercent = 86.2,
                AvailableMemoryMb = 2048,
                DiskActiveTimePercent = 95,
                TopProcessName = "game",
                TopProcessMemoryMb = 1024,
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

            long sampleCount = (long)(await sampleCountCommand.ExecuteScalarAsync() ?? 0L);
            long eventCount = (long)(await eventCountCommand.ExecuteScalarAsync() ?? 0L);

            Assert.Equal(1, sampleCount);
            Assert.Equal(1, eventCount);
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
}
