using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Core.Interfaces;

public interface IMetricCollector
{
    Task<MetricSample> CollectAsync(CancellationToken cancellationToken);
}

