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
                StartedAtUtc = recentSamples[0].TimestampUtc,
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
}
