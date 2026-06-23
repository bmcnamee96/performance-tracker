namespace PerformanceTracker.Core.Models;

public sealed class MetricSample
{
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public double? CpuUsagePercent { get; init; }

    public double? CpuFrequencyMHz { get; init; }

    public double? CpuQueueLength { get; init; }

    public double MemoryUsedPercent { get; init; }

    public long AvailableMemoryMb { get; init; }

    public double? CommitUsedPercent { get; init; }

    public double? DiskActiveTimePercent { get; init; }

    public double? DiskReadMbPerSec { get; init; }

    public double? DiskWriteMbPerSec { get; init; }

    public double? DiskQueueLength { get; init; }

    public double? DiskAverageResponseMs { get; init; }

    public double? NetworkReceiveKbps { get; init; }

    public double? NetworkSendKbps { get; init; }

    public double? NetworkLatencyMs { get; init; }

    public double? NetworkRetransmitsPerSec { get; init; }

    public double? GpuUsagePercent { get; init; }

    public double? GpuMemoryUsedPercent { get; init; }

    public double? GpuDedicatedMemoryMb { get; init; }

    public double? GpuSharedMemoryMb { get; init; }

    public double? GpuTemperatureCelsius { get; init; }

    public string? TopProcessName { get; init; }

    public double? TopProcessMemoryMb { get; init; }

    public string? GameProcessName { get; init; }

    public bool GamingTelemetryActive { get; init; }

    public double? FramesPerSecond { get; init; }

    public double? FrameTimeMs { get; init; }

    public double? FrameTimeP95Ms { get; init; }

    public double? InputLatencyMs { get; init; }

    public double? RenderLatencyMs { get; init; }

    public double? DisplayRefreshRateHz { get; init; }

    public bool? VariableRefreshRateEnabled { get; init; }

    public bool? GameModeEnabled { get; init; }

    public TimeSpan Uptime { get; init; }
}
