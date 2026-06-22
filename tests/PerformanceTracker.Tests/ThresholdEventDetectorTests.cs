using PerformanceTracker.Core.Configuration;
using PerformanceTracker.Core.Models;
using PerformanceTracker.Core.Services;
using Xunit;

namespace PerformanceTracker.Tests;

public sealed class ThresholdEventDetectorTests
{
    private static readonly CollectorSettings Settings = new()
    {
        SampleIntervalSeconds = 5,
        CpuHighPercent = 90,
        CpuHighDurationSeconds = 10,
        MemoryHighPercent = 90,
        DiskActiveHighPercent = 90
    };

    [Fact]
    public void CreatesCpuPressureEventWhenThresholdIsSustained()
    {
        ThresholdEventDetector detector = new(Settings, new SimpleDiagnosisService());
        List<MetricSample> samples =
        [
            CreateSample(secondsAgo: 10, cpu: 92, memory: 70, disk: 20),
            CreateSample(secondsAgo: 5, cpu: 94, memory: 72, disk: 25),
            CreateSample(secondsAgo: 0, cpu: 95, memory: 73, disk: 30)
        ];

        IReadOnlyList<PerformanceEvent> events = detector.Evaluate(samples, "game");

        PerformanceEvent performanceEvent = Assert.Single(events);
        Assert.Equal(EventSeverity.Critical, performanceEvent.Severity);
        Assert.Contains("CPU pressure", performanceEvent.Summary);
    }

    [Fact]
    public void CreatesRamPressureEventWhenMemoryThresholdIsExceeded()
    {
        ThresholdEventDetector detector = new(Settings, new SimpleDiagnosisService());
        List<MetricSample> samples =
        [
            CreateSample(secondsAgo: 5, cpu: 50, memory: 82, disk: 40),
            CreateSample(secondsAgo: 0, cpu: 55, memory: 93, disk: 35)
        ];

        IReadOnlyList<PerformanceEvent> events = detector.Evaluate(samples, "excel");

        PerformanceEvent performanceEvent = Assert.Single(events);
        Assert.Equal(EventSeverity.Warning, performanceEvent.Severity);
        Assert.Contains("RAM pressure", performanceEvent.Summary);
    }

    [Fact]
    public void CreatesDiskPressureEventWhenDiskThresholdIsExceeded()
    {
        ThresholdEventDetector detector = new(Settings, new SimpleDiagnosisService());
        List<MetricSample> samples =
        [
            CreateSample(secondsAgo: 5, cpu: 40, memory: 65, disk: 50),
            CreateSample(secondsAgo: 0, cpu: 42, memory: 68, disk: 96)
        ];

        IReadOnlyList<PerformanceEvent> events = detector.Evaluate(samples, "discord");

        PerformanceEvent performanceEvent = Assert.Single(events);
        Assert.Equal(EventSeverity.Critical, performanceEvent.Severity);
        Assert.Contains("Disk pressure", performanceEvent.Summary);
    }

    private static MetricSample CreateSample(int secondsAgo, double cpu, double memory, double disk)
    {
        return new MetricSample
        {
            TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(-secondsAgo),
            CpuUsagePercent = cpu,
            MemoryUsedPercent = memory,
            AvailableMemoryMb = 1024,
            DiskActiveTimePercent = disk,
            Uptime = TimeSpan.FromHours(4)
        };
    }
}
