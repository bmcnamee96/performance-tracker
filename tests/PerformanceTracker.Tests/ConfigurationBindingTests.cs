using System.Text;
using Microsoft.Extensions.Configuration;
using PerformanceTracker.Core.Configuration;
using Xunit;

namespace PerformanceTracker.Tests;

public sealed class ConfigurationBindingTests
{
    [Fact]
    public void BindsCollectorAndRetentionSettings()
    {
        const string json =
            """
            {
              "Collector": {
                "SampleIntervalSeconds": 3,
                "EnableExtendedWindowsMetrics": true,
                "EnableGamingTelemetry": true,
                "CapturePerProcessGpuMetrics": true,
                "CaptureNetworkLatencyMetrics": true,
                "CpuHighPercent": 88,
                "CpuHighDurationSeconds": 12,
                "MemoryHighPercent": 91,
                "DiskActiveHighPercent": 93,
                "DiskQueueHighThreshold": 3.5,
                "DiskResponseHighMs": 30,
                "GpuHighPercent": 97,
                "GpuMemoryHighPercent": 92,
                "NetworkLatencyHighMs": 125,
                "FpsLowThreshold": 55,
                "FrameTimeHighMs": 22,
                "FrameTimeP95HighMs": 35,
                "InputLatencyHighMs": 45,
                "RenderLatencyHighMs": 28,
                "EventCooldownSeconds": 45,
                "EventContextWindowSeconds": 60,
                "TopProcessLimit": 7
              },
              "Retention": {
                "RawSampleRetentionHours": 48,
                "EventRetentionDays": 30,
                "DailySummaryRetentionDays": 180
              }
            }
            """;

        using MemoryStream stream = new(Encoding.UTF8.GetBytes(json));
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();

        CollectorSettings collector = configuration.GetSection("Collector").Get<CollectorSettings>()!;
        RetentionSettings retention = configuration.GetSection("Retention").Get<RetentionSettings>()!;

        Assert.Equal(3, collector.SampleIntervalSeconds);
        Assert.True(collector.EnableExtendedWindowsMetrics);
        Assert.True(collector.EnableGamingTelemetry);
        Assert.True(collector.CapturePerProcessGpuMetrics);
        Assert.True(collector.CaptureNetworkLatencyMetrics);
        Assert.Equal(88, collector.CpuHighPercent);
        Assert.Equal(3.5, collector.DiskQueueHighThreshold);
        Assert.Equal(97, collector.GpuHighPercent);
        Assert.Equal(55, collector.FpsLowThreshold);
        Assert.Equal(7, collector.TopProcessLimit);
        Assert.Equal(48, retention.RawSampleRetentionHours);
        Assert.Equal(30, retention.EventRetentionDays);
        Assert.Equal(180, retention.DailySummaryRetentionDays);
    }
}
