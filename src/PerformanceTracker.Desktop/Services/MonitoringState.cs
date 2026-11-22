using PerformanceTracker.Core.Models;
using PerformanceTracker.Core.Services;

namespace PerformanceTracker.Desktop.Services;

public sealed class MonitoringState
{
    private const int MaxRecentSamples = 120;
    private const int MaxRecentEvents = 40;

    private readonly object _syncRoot = new();
    private readonly List<MetricSample> _recentSamples = [];
    private readonly List<PerformanceEvent> _recentEvents = [];
    private readonly DateTimeOffset _sessionStartedAtUtc = DateTimeOffset.UtcNow;
    private MetricSample? _latestSample;
    private PerformanceEvent? _latestEvent;
    private string? _foregroundApp;
    private long _sampleCount;

    public void Update(MetricSample sample, PerformanceEvent? performanceEvent, string? foregroundApp)
    {
        lock (_syncRoot)
        {
            _latestSample = sample;
            _foregroundApp = foregroundApp;
            _sampleCount++;

            _recentSamples.Add(sample);
            TrimRecentSamples();

            if (performanceEvent is not null)
            {
                _latestEvent = performanceEvent;
                UpsertRecentEvent(performanceEvent);
            }
        }
    }

    public MonitoringSnapshot GetSnapshot()
    {
        lock (_syncRoot)
        {
            return new MonitoringSnapshot(
                _latestSample,
                _latestEvent,
                _foregroundApp,
                _sessionStartedAtUtc,
                _sampleCount,
                _recentSamples.ToArray(),
                _recentEvents.ToArray());
        }
    }

    private void TrimRecentSamples()
    {
        int overflow = _recentSamples.Count - MaxRecentSamples;
        if (overflow > 0)
        {
            _recentSamples.RemoveRange(0, overflow);
        }
    }

    private void UpsertRecentEvent(PerformanceEvent performanceEvent)
    {
        int lastIndex = _recentEvents.Count - 1;
        if (lastIndex >= 0 && EventMerging.TryMerge(_recentEvents[lastIndex], performanceEvent) is { } merged)
        {
            _recentEvents[lastIndex] = merged;
            _latestEvent = merged;
            return;
        }

        _recentEvents.Add(performanceEvent);
        if (_recentEvents.Count > MaxRecentEvents)
        {
            _recentEvents.RemoveAt(0);
        }
    }
}

public sealed record MonitoringSnapshot(
    MetricSample? LatestSample,
    PerformanceEvent? LatestEvent,
    string? ForegroundApp,
    DateTimeOffset SessionStartedAtUtc,
    long SampleCount,
    IReadOnlyList<MetricSample> RecentSamples,
    IReadOnlyList<PerformanceEvent> RecentEvents);
