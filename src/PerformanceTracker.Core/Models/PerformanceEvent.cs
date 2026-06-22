namespace PerformanceTracker.Core.Models;

public sealed class PerformanceEvent
{
    public DateTimeOffset StartedAtUtc { get; init; }

    public DateTimeOffset EndedAtUtc { get; init; }

    public EventSeverity Severity { get; init; }

    public string Summary { get; init; } = string.Empty;

    public string LikelyCause { get; init; } = string.Empty;

    public string? ForegroundApp { get; init; }

    public MetricSample TriggerSample { get; init; } = new();
}

