namespace PerformanceTracker.Core.Models;

public sealed class MetricSample
{
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public double? CpuUsagePercent { get; init; }

    public double MemoryUsedPercent { get; init; }

    public long AvailableMemoryMb { get; init; }

    public double? DiskActiveTimePercent { get; init; }

    public double? NetworkReceiveKbps { get; init; }

    public double? NetworkSendKbps { get; init; }

    public string? TopProcessName { get; init; }

    public double? TopProcessMemoryMb { get; init; }

    public TimeSpan Uptime { get; init; }
}

