using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Core.Interfaces;

public interface IEventDetector
{
    IReadOnlyList<PerformanceEvent> Evaluate(IReadOnlyList<MetricSample> recentSamples, string? foregroundApp);
}

