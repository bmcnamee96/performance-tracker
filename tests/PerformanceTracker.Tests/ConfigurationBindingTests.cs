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
                "CpuHighPercent": 88,
                "CpuHighDurationSeconds": 12,
                "MemoryHighPercent": 91,
                "DiskActiveHighPercent": 93,
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
        Assert.Equal(88, collector.CpuHighPercent);
        Assert.Equal(7, collector.TopProcessLimit);
        Assert.Equal(48, retention.RawSampleRetentionHours);
        Assert.Equal(30, retention.EventRetentionDays);
        Assert.Equal(180, retention.DailySummaryRetentionDays);
    }
}
