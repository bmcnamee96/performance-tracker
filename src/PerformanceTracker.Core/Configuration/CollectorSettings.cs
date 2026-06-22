namespace PerformanceTracker.Core.Configuration;

public sealed class CollectorSettings
{
    public int SampleIntervalSeconds { get; set; } = 5;

    public double CpuHighPercent { get; set; } = 90;

    public int CpuHighDurationSeconds { get; set; } = 10;

    public double MemoryHighPercent { get; set; } = 90;

    public double DiskActiveHighPercent { get; set; } = 90;

    public int EventCooldownSeconds { get; set; } = 30;

    public int EventContextWindowSeconds { get; set; } = 30;

    public int TopProcessLimit { get; set; } = 5;
}

