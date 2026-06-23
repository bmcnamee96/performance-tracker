namespace PerformanceTracker.Core.Configuration;

public sealed class CollectorSettings
{
    public int SampleIntervalSeconds { get; set; } = 5;

    public bool EnableExtendedWindowsMetrics { get; set; } = true;

    public bool EnableGamingTelemetry { get; set; }

    public bool CapturePerProcessGpuMetrics { get; set; }

    public bool CaptureNetworkLatencyMetrics { get; set; }

    public double CpuHighPercent { get; set; } = 90;

    public int CpuHighDurationSeconds { get; set; } = 10;

    public double MemoryHighPercent { get; set; } = 90;

    public double DiskActiveHighPercent { get; set; } = 90;

    public double DiskQueueHighThreshold { get; set; } = 2;

    public double DiskResponseHighMs { get; set; } = 25;

    public double GpuHighPercent { get; set; } = 95;

    public double GpuMemoryHighPercent { get; set; } = 90;

    public double NetworkLatencyHighMs { get; set; } = 100;

    public double FpsLowThreshold { get; set; } = 50;

    public double FrameTimeHighMs { get; set; } = 25;

    public double FrameTimeP95HighMs { get; set; } = 33.3;

    public double InputLatencyHighMs { get; set; } = 50;

    public double RenderLatencyHighMs { get; set; } = 25;

    public int EventCooldownSeconds { get; set; } = 30;

    public int EventContextWindowSeconds { get; set; } = 30;

    public int TopProcessLimit { get; set; } = 5;
}
