using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Core.Interfaces;

public interface ISampleRepository
{
    Task EnsureCreatedAsync(CancellationToken cancellationToken);

    Task SaveMetricSampleAsync(MetricSample sample, CancellationToken cancellationToken);

    Task SavePerformanceEventAsync(PerformanceEvent performanceEvent, CancellationToken cancellationToken);
}

