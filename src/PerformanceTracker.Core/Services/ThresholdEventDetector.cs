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

        DiagnosisAnalyzer.DiagnosisInsight insight = DiagnosisAnalyzer.Analyze(recentSamples, _settings);
        if (!insight.HasSignal)
        {
            return Array.Empty<PerformanceEvent>();
        }

        MetricSample latest = recentSamples[^1];

        return
        [
            new PerformanceEvent
            {
                StartedAtUtc = FindEpisodeStart(recentSamples),
                EndedAtUtc = latest.TimestampUtc,
                Severity = insight.Severity,
                Summary = insight.Title,
                LikelyCause = string.IsNullOrWhiteSpace(insight.LikelyCause)
                    ? _diagnosisService.DescribeLikelyCause(latest)
                    : insight.LikelyCause,
                ForegroundApp = foregroundApp,
                TriggerSample = latest
            }
        ];
    }

    /// <summary>
    /// Walks back from the latest reading while readings stay elevated, so a slowdown starts when the
    /// pressure began rather than at the oldest reading in the context window.
    /// </summary>
    private DateTimeOffset FindEpisodeStart(IReadOnlyList<MetricSample> recentSamples)
    {
        int start = recentSamples.Count - 1;
        while (start > 0 && IsElevated(recentSamples[start - 1]))
        {
            start--;
        }

        return recentSamples[start].TimestampUtc;
    }

    private bool IsElevated(MetricSample sample) =>
        sample.CpuUsagePercent >= _settings.CpuHighPercent - 7 ||
        sample.MemoryUsedPercent >= _settings.MemoryHighPercent - 5 ||
        sample.DiskActiveTimePercent >= _settings.DiskActiveHighPercent - 10;
}
