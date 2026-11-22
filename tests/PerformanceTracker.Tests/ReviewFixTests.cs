using Microsoft.Data.Sqlite;
using PerformanceTracker.Core.Configuration;
using PerformanceTracker.Core.Models;
using PerformanceTracker.Core.Services;
using PerformanceTracker.Infrastructure.Storage;
using Xunit;

namespace PerformanceTracker.Tests;

/// <summary>Covers retention, slowdown timing, disk and network diagnosis, and project attribution.</summary>
public sealed class ReviewFixTests
{
    private static readonly CollectorSettings Settings = new()
    {
        SampleIntervalSeconds = 5,
        CpuHighPercent = 90,
        CpuHighDurationSeconds = 10,
        MemoryHighPercent = 90,
        DiskActiveHighPercent = 90
    };

    private static MetricSample Sample(int secondsAgo, double? cpu = 10, double memory = 50, double? disk = 5,
        double? diskResponseMs = null, double? diskQueue = null, double? down = 0, double? up = 0) => new()
        {
            TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(-secondsAgo),
            CpuUsagePercent = cpu,
            MemoryUsedPercent = memory,
            AvailableMemoryMb = 8000,
            DiskActiveTimePercent = disk,
            DiskAverageResponseMs = diskResponseMs,
            DiskQueueLength = diskQueue,
            NetworkReceiveKbps = down,
            NetworkSendKbps = up
        };

    [Fact]
    public async Task RetentionDeletesOldDataAndKeepsDailyAverages()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        var repository = new SqliteSampleRepository(databasePath);
        try
        {
            await repository.EnsureCreatedAsync(CancellationToken.None);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset oldDay = now.AddDays(-5);
            await repository.SaveMetricSampleAsync(new MetricSample { TimestampUtc = oldDay, CpuUsagePercent = 10, MemoryUsedPercent = 40 }, CancellationToken.None);
            await repository.SaveMetricSampleAsync(new MetricSample { TimestampUtc = oldDay.AddMinutes(1), CpuUsagePercent = 30, MemoryUsedPercent = 60 }, CancellationToken.None);
            await repository.SaveMetricSampleAsync(new MetricSample { TimestampUtc = now.AddHours(-1), CpuUsagePercent = 99, MemoryUsedPercent = 99 }, CancellationToken.None);
            await repository.SavePerformanceEventAsync(new PerformanceEvent { StartedAtUtc = now.AddDays(-100), EndedAtUtc = now.AddDays(-100), Summary = "Old" }, CancellationToken.None);
            await repository.SavePerformanceEventAsync(new PerformanceEvent { StartedAtUtc = now.AddDays(-1), EndedAtUtc = now.AddDays(-1), Summary = "Recent" }, CancellationToken.None);

            RetentionResult result = repository.ApplyRetention(new RetentionSettings(), now, CancellationToken.None);

            Assert.Equal(2, result.SamplesRemoved);
            Assert.Equal(1, result.EventsRemoved);
            DailySummary summary = Assert.Single(repository.ReadDailySummaries(30));
            Assert.Equal(2, summary.SampleCount);
            Assert.Equal(20, summary.CpuAverage);
            Assert.Equal(30, summary.CpuPeak);
            Assert.Equal(50, summary.MemoryAverage);
            Assert.Equal("Recent", Assert.Single(repository.ReadPerformanceEvents(now.AddDays(-365))).Summary);

            // A later pass for the same day adds to the summary instead of replacing it.
            await repository.SaveMetricSampleAsync(new MetricSample { TimestampUtc = oldDay.AddMinutes(2), CpuUsagePercent = 50, MemoryUsedPercent = 80 }, CancellationToken.None);
            repository.ApplyRetention(new RetentionSettings(), now, CancellationToken.None);
            summary = Assert.Single(repository.ReadDailySummaries(30));
            Assert.Equal(3, summary.SampleCount);
            Assert.Equal(30, summary.CpuAverage!.Value, 6);
            Assert.Equal(50, summary.CpuPeak);
            Assert.Equal(60, summary.MemoryAverage!.Value, 6);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task SavedSlowdownIsExtendedInPlace()
    {
        string databasePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");
        var repository = new SqliteSampleRepository(databasePath);
        try
        {
            await repository.EnsureCreatedAsync(CancellationToken.None);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            PerformanceEvent first = new() { StartedAtUtc = now, EndedAtUtc = now.AddSeconds(5), Summary = "Processor maxed out", LikelyCause = "a" };
            long id = await repository.SavePerformanceEventAsync(first, CancellationToken.None);
            await repository.UpdatePerformanceEventAsync(id, first with { EndedAtUtc = now.AddMinutes(3), LikelyCause = "b" }, CancellationToken.None);

            PerformanceEvent saved = Assert.Single(repository.ReadPerformanceEvents(now.AddHours(-1)));
            Assert.Equal(now.AddMinutes(3).ToUnixTimeSeconds(), saved.EndedAtUtc.ToUnixTimeSeconds());
            Assert.Equal("b", saved.LikelyCause);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    [Fact]
    public void SlowdownStartsAtFirstElevatedReading()
    {
        ThresholdEventDetector detector = new(Settings, new SimpleDiagnosisService());
        MetricSample[] samples = [Sample(20, cpu: 20), Sample(15, cpu: 30), Sample(10, cpu: 92), Sample(5, cpu: 94), Sample(0, cpu: 96)];

        PerformanceEvent slowdown = Assert.Single(detector.Evaluate(samples, "game"));

        Assert.Equal(samples[2].TimestampUtc, slowdown.StartedAtUtc);
    }

    [Fact]
    public void BusyButFastDiskIsNotASlowdown()
    {
        ThresholdEventDetector detector = new(Settings, new SimpleDiagnosisService());
        MetricSample[] samples = [Sample(10, disk: 99, diskResponseMs: 3, diskQueue: 0.4), Sample(5, disk: 99, diskResponseMs: 4, diskQueue: 0.5), Sample(0, disk: 100, diskResponseMs: 5, diskQueue: 0.6)];

        Assert.Empty(detector.Evaluate(samples, "explorer"));
    }

    [Fact]
    public void BusyAndSlowDiskIsASlowdownWithTheResponseTime()
    {
        ThresholdEventDetector detector = new(Settings, new SimpleDiagnosisService());
        MetricSample[] samples = [Sample(10, disk: 99, diskResponseMs: 40), Sample(5, disk: 99, diskResponseMs: 45), Sample(0, disk: 100, diskResponseMs: 48)];

        PerformanceEvent slowdown = Assert.Single(detector.Evaluate(samples, "explorer"));

        Assert.Equal("Disk very busy", slowdown.Summary);
        Assert.Contains("48 ms", slowdown.LikelyCause);
    }

    [Fact]
    public void DiskCheckUsesConfiguredLimits()
    {
        CollectorSettings lenient = new()
        {
            SampleIntervalSeconds = 5,
            CpuHighPercent = 90,
            CpuHighDurationSeconds = 10,
            MemoryHighPercent = 90,
            DiskActiveHighPercent = 90,
            DiskResponseHighMs = 100,
            DiskQueueHighThreshold = 10
        };
        ThresholdEventDetector detector = new(lenient, new SimpleDiagnosisService());
        MetricSample[] samples = [Sample(10, disk: 99, diskResponseMs: 40), Sample(5, disk: 99, diskResponseMs: 45), Sample(0, disk: 100, diskResponseMs: 48)];

        Assert.Empty(detector.Evaluate(samples, "explorer"));
    }

    [Fact]
    public void ShortDiskBurstIsNotASlowdown()
    {
        ThresholdEventDetector detector = new(Settings, new SimpleDiagnosisService());
        MetricSample[] samples = [Sample(10, disk: 20), Sample(5, disk: 30), Sample(0, disk: 100, diskResponseMs: 60)];

        Assert.Empty(detector.Evaluate(samples, "explorer"));
    }

    [Theory]
    [InlineData(9000, 100, "download")]
    [InlineData(100, 9000, "upload")]
    [InlineData(5000, 4000, "network transfer")]
    public void NetworkSlowdownsNameTheDirection(double down, double up, string expected)
    {
        ThresholdEventDetector detector = new(Settings, new SimpleDiagnosisService());
        MetricSample[] samples = [Sample(10, cpu: 71, down: 100, up: 30), Sample(5, cpu: 83, down: 120, up: 40), Sample(0, cpu: 88, down: down, up: up)];

        PerformanceEvent slowdown = Assert.Single(detector.Evaluate(samples, "launcher"));

        Assert.Equal($"Processor busy during a big {expected}", slowdown.Summary);
    }

    [Fact]
    public void DeveloperToolGroupsNameTheirBusiestProject()
    {
        RunningProcessInfo vite = new()
        {
            Id = 1,
            Name = "node",
            MemoryBytes = 4_000_000_000,
            StartTimeUtc = DateTimeOffset.UnixEpoch,
            CommandLine = @"node C:\Dev\Fantasy-Valorant\node_modules\vite\bin\vite.js"
        };
        RunningProcessInfo small = vite with { Id = 2, MemoryBytes = 50_000_000, CommandLine = "node tsserver.js", WorkingDirectory = @"C:\Dev\Other" };

        AppGroup node = Assert.Single(AppGroupBuilder.Build([vite, small], new Dictionary<int, ProcessActivity>(), selfProcessId: 99));

        Assert.Equal("Fantasy-Valorant · Vite dev server", node.TopActivity);
        Assert.Equal($"{node.DisplayName} (Fantasy-Valorant · Vite dev server)", node.NameWithActivity);
    }

    [Fact]
    public void OrdinaryAppsHaveNoActivityLabel()
    {
        RunningProcessInfo chrome = new() { Id = 5, Name = "chrome", MemoryBytes = 1_000_000_000, StartTimeUtc = DateTimeOffset.UnixEpoch };

        AppGroup group = Assert.Single(AppGroupBuilder.Build([chrome], new Dictionary<int, ProcessActivity>(), selfProcessId: 99));

        Assert.Null(group.TopActivity);
        Assert.Equal(group.DisplayName, group.NameWithActivity);
    }
}
