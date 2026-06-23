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
                Summary = $"{insight.Title} built up over {FormatWindow(recentSamples)} while {DescribeForegroundApp(foregroundApp)} was active: {insight.SummaryFragment}.",
                LikelyCause = BuildLikelyCause(recentSamples, latest, insight),
                ForegroundApp = foregroundApp,
                TriggerSample = latest
            }
        ];
    }

    private string BuildLikelyCause(
        IReadOnlyList<MetricSample> recentSamples,
        MetricSample latest,
        DiagnosisAnalyzer.DiagnosisInsight insight)
    {
        string cause = insight.LikelyCause;
        string? fallback = _diagnosisService.DescribeLikelyCause(latest);

        if (string.IsNullOrWhiteSpace(cause) && !string.IsNullOrWhiteSpace(fallback))
        {
            cause = fallback;
        }
        else if (!string.IsNullOrWhiteSpace(fallback)
            && !string.Equals(cause, fallback, StringComparison.OrdinalIgnoreCase)
            && ShouldAppendFallback(cause))
        {
            cause = $"{cause} Latest-sample fallback: {fallback}";
        }

        if (HasSparseMetrics(recentSamples))
        {
            cause = $"{cause} Some optional metrics were unavailable in this window, so the diagnosis favors conservative cross-signal inference.";
        }

        return cause;
    }

    private static string DescribeForegroundApp(string? foregroundApp) =>
        string.IsNullOrWhiteSpace(foregroundApp) ? "the system" : foregroundApp;

    private static string FormatWindow(IReadOnlyList<MetricSample> recentSamples)
    {
        if (recentSamples.Count < 2)
        {
            return "the latest sample";
        }

        TimeSpan window = recentSamples[^1].TimestampUtc - recentSamples[0].TimestampUtc;
        int roundedSeconds = Math.Max(1, (int)Math.Round(window.TotalSeconds));
        return $"{roundedSeconds}s";
    }

    private static bool ShouldAppendFallback(string cause)
    {
        return cause.Length < 260 && !cause.Contains("fallback", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSparseMetrics(IReadOnlyList<MetricSample> recentSamples)
    {
        return recentSamples.Any(sample =>
            sample.CpuUsagePercent is null
            || sample.DiskActiveTimePercent is null
            || sample.NetworkReceiveKbps is null && sample.NetworkSendKbps is null);
    }
}
