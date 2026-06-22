using PerformanceTracker.Core.Configuration;
using PerformanceTracker.Core.Interfaces;
using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Core.Services;

public sealed class ThresholdEventDetector : IEventDetector
{
    private readonly CollectorSettings _settings;
    private readonly IDiagnosisService _diagnosisService;

    public ThresholdEventDetector(CollectorSettings settings, IDiagnosisService diagnosisService)
    {
        _settings = settings;
        _diagnosisService = diagnosisService;
    }

    public IReadOnlyList<PerformanceEvent> Evaluate(IReadOnlyList<MetricSample> recentSamples, string? foregroundApp)
    {
        if (recentSamples.Count == 0)
        {
            return Array.Empty<PerformanceEvent>();
        }

        MetricSample latest = recentSamples[^1];
        List<PerformanceEvent> events = new();

        if (HasSustainedCpuPressure(recentSamples))
        {
            events.Add(CreateEvent(recentSamples, latest, foregroundApp, "CPU pressure"));
        }
        else if (latest.MemoryUsedPercent >= _settings.MemoryHighPercent)
        {
            events.Add(CreateEvent(recentSamples, latest, foregroundApp, "RAM pressure"));
        }
        else if (latest.DiskActiveTimePercent is double diskThreshold && diskThreshold >= _settings.DiskActiveHighPercent)
        {
            events.Add(CreateEvent(recentSamples, latest, foregroundApp, "Disk pressure"));
        }

        return events;
    }

    private bool HasSustainedCpuPressure(IReadOnlyList<MetricSample> recentSamples)
    {
        int requiredSamples = Math.Max(
            1,
            (int)Math.Ceiling((double)_settings.CpuHighDurationSeconds / Math.Max(1, _settings.SampleIntervalSeconds)));

        if (recentSamples.Count < requiredSamples)
        {
            return false;
        }

        return recentSamples
            .TakeLast(requiredSamples)
            .All(sample => sample.CpuUsagePercent is double cpu && cpu >= _settings.CpuHighPercent);
    }

    private PerformanceEvent CreateEvent(
        IReadOnlyList<MetricSample> recentSamples,
        MetricSample triggerSample,
        string? foregroundApp,
        string title)
    {
        EventSeverity severity = DetermineSeverity(triggerSample);

        return new PerformanceEvent
        {
            StartedAtUtc = recentSamples[0].TimestampUtc,
            EndedAtUtc = triggerSample.TimestampUtc,
            Severity = severity,
            Summary = $"{title} detected while {foregroundApp ?? "the system"} was active.",
            LikelyCause = _diagnosisService.DescribeLikelyCause(triggerSample),
            ForegroundApp = foregroundApp,
            TriggerSample = triggerSample
        };
    }

    private static EventSeverity DetermineSeverity(MetricSample sample)
    {
        if (sample.CpuUsagePercent is >= 95 || sample.MemoryUsedPercent >= 95 || sample.DiskActiveTimePercent is >= 95)
        {
            return EventSeverity.Critical;
        }

        return EventSeverity.Warning;
    }
}
