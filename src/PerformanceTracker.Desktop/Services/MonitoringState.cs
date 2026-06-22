using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Desktop.Services;

public sealed class MonitoringState
{
    private readonly object _syncRoot = new();
    private MetricSample? _latestSample;
    private PerformanceEvent? _latestEvent;
    private string? _foregroundApp;

    public void Update(MetricSample sample, PerformanceEvent? performanceEvent, string? foregroundApp)
    {
        lock (_syncRoot)
        {
            _latestSample = sample;
            _foregroundApp = foregroundApp;

            if (performanceEvent is not null)
            {
                _latestEvent = performanceEvent;
            }
        }
    }

    public MonitoringSnapshot GetSnapshot()
    {
        lock (_syncRoot)
        {
            return new MonitoringSnapshot(_latestSample, _latestEvent, _foregroundApp);
        }
    }
}

public sealed record MonitoringSnapshot(
    MetricSample? LatestSample,
    PerformanceEvent? LatestEvent,
    string? ForegroundApp);

