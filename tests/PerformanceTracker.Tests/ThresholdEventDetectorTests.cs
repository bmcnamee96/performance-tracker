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
        Assert.Contains("built up over 10s", performanceEvent.Summary);
        Assert.Contains("CPU demand built up", performanceEvent.LikelyCause);
    }

    [Fact]
    public void CreatesMemoryAndDiskContentionEventFromCombinedSignals()
    {
        ThresholdEventDetector detector = new(Settings, new SimpleDiagnosisService());
        List<MetricSample> samples =
        [
            CreateSample(secondsAgo: 10, cpu: 48, memory: 78, disk: 50, availableMemoryMb: 2200),
            CreateSample(secondsAgo: 5, cpu: 52, memory: 84, disk: 76, availableMemoryMb: 1400),
            CreateSample(secondsAgo: 0, cpu: 55, memory: 89, disk: 92, availableMemoryMb: 768, topProcessName: "game.exe", topProcessMemoryMb: 1800)
        ];

        IReadOnlyList<PerformanceEvent> events = detector.Evaluate(samples, "game");

        PerformanceEvent performanceEvent = Assert.Single(events);
        Assert.Equal(EventSeverity.Warning, performanceEvent.Severity);
        Assert.Contains("Memory pressure with disk contention", performanceEvent.Summary);
        Assert.Contains("paging", performanceEvent.LikelyCause);
        Assert.Contains("game.exe", performanceEvent.LikelyCause);
    }

    [Fact]
    public void CreatesCpuPressureEventFromRisingTrendAndNetworkBurst()
    {
        ThresholdEventDetector detector = new(Settings, new SimpleDiagnosisService());
        List<MetricSample> samples =
        [
            CreateSample(secondsAgo: 10, cpu: 71, memory: 62, disk: 35, networkReceiveKbps: 120, networkSendKbps: 25),
            CreateSample(secondsAgo: 5, cpu: 83, memory: 64, disk: 42, networkReceiveKbps: 180, networkSendKbps: 40),
            CreateSample(secondsAgo: 0, cpu: 88, memory: 66, disk: 48, networkReceiveKbps: 4_500, networkSendKbps: 900)
        ];

        IReadOnlyList<PerformanceEvent> events = detector.Evaluate(samples, "launcher");

        PerformanceEvent performanceEvent = Assert.Single(events);
        Assert.Equal(EventSeverity.Warning, performanceEvent.Severity);
        Assert.Contains("CPU pressure during network burst", performanceEvent.Summary);
        Assert.Contains("downloads, sync, streaming, or patching", performanceEvent.LikelyCause);
    }

    [Fact]
    public void AppendsSparseMetricNoteWhenOptionalSignalsAreMissing()
    {
        ThresholdEventDetector detector = new(Settings, new SimpleDiagnosisService());
        List<MetricSample> samples =
        [
            CreateSample(secondsAgo: 5, cpu: null, memory: 88, disk: null, availableMemoryMb: 1500, networkReceiveKbps: null, networkSendKbps: null),
            CreateSample(secondsAgo: 0, cpu: null, memory: 93, disk: null, availableMemoryMb: 900, networkReceiveKbps: null, networkSendKbps: null)
        ];

        IReadOnlyList<PerformanceEvent> events = detector.Evaluate(samples, "editor");

        PerformanceEvent performanceEvent = Assert.Single(events);
        Assert.Contains("RAM pressure", performanceEvent.Summary);
        Assert.Contains("optional metrics were unavailable", performanceEvent.LikelyCause);
    }

    [Fact]
    public void DoesNotCreateEventForShortSpikeWithoutTrendOrCorroboration()
    {
        ThresholdEventDetector detector = new(Settings, new SimpleDiagnosisService());
        List<MetricSample> samples =
        [
            CreateSample(secondsAgo: 10, cpu: 35, memory: 58, disk: 25),
            CreateSample(secondsAgo: 5, cpu: 42, memory: 60, disk: 28),
            CreateSample(secondsAgo: 0, cpu: 88, memory: 61, disk: 32)
        ];

        IReadOnlyList<PerformanceEvent> events = detector.Evaluate(samples, "browser");

        Assert.Empty(events);
    }

    [Fact]
    public void SimpleDiagnosisServiceUsesCombinedSingleSampleSignals()
    {
        SimpleDiagnosisService diagnosisService = new();

        string cause = diagnosisService.DescribeLikelyCause(CreateSample(
            secondsAgo: 0,
            cpu: 55,
            memory: 94,
            disk: 96,
            availableMemoryMb: 700,
            topProcessName: "scanner.exe",
            topProcessMemoryMb: 1500));

        Assert.Contains("paging", cause);
        Assert.Contains("scanner.exe", cause);
    }

    private static MetricSample CreateSample(
        int secondsAgo,
        double? cpu,
        double memory,
        double? disk,
        long availableMemoryMb = 1024,
        double? networkReceiveKbps = 0,
        double? networkSendKbps = 0,
        string? topProcessName = null,
        double? topProcessMemoryMb = null)
    {
        return new MetricSample
        {
            TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(-secondsAgo),
            CpuUsagePercent = cpu,
            MemoryUsedPercent = memory,
            AvailableMemoryMb = availableMemoryMb,
            DiskActiveTimePercent = disk,
            NetworkReceiveKbps = networkReceiveKbps,
            NetworkSendKbps = networkSendKbps,
            TopProcessName = topProcessName,
            TopProcessMemoryMb = topProcessMemoryMb,
            Uptime = TimeSpan.FromHours(4)
        };
    }
}
