using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Core.Interfaces;

public interface ISampleRepository
{
    Task EnsureCreatedAsync(CancellationToken cancellationToken);

    Task SaveMetricSampleAsync(MetricSample sample, CancellationToken cancellationToken);

    /// <summary>Saves a new slowdown and returns its id, so it can be extended while it lasts.</summary>
    Task<long> SavePerformanceEventAsync(PerformanceEvent performanceEvent, CancellationToken cancellationToken);

    /// <summary>Updates a saved slowdown as it continues (end time, severity, title, cause).</summary>
    Task UpdatePerformanceEventAsync(long id, PerformanceEvent performanceEvent, CancellationToken cancellationToken);
}

