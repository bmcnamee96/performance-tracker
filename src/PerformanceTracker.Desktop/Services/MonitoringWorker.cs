using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PerformanceTracker.Core.Configuration;
using PerformanceTracker.Core.Interfaces;
using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Desktop.Services;

public sealed partial class MonitoringWorker : BackgroundService
{
    private readonly IMetricCollector _metricCollector;
    private readonly IEventDetector _eventDetector;
    private readonly ISampleRepository _sampleRepository;
    private readonly IForegroundAppTracker _foregroundAppTracker;
    private readonly ProcessOverviewService _processOverview;
    private readonly MonitoringState _monitoringState;
    private readonly ILogger<MonitoringWorker> _logger;
    private readonly CollectorSettings _collectorSettings;
    private readonly Queue<MetricSample> _recentSamples = new();
    private DateTimeOffset? _lastPersistedEventAtUtc;

    public MonitoringWorker(
        IMetricCollector metricCollector,
        IEventDetector eventDetector,
        ISampleRepository sampleRepository,
        IForegroundAppTracker foregroundAppTracker,
        MonitoringState monitoringState,
        ProcessOverviewService processOverview,
        IOptions<CollectorSettings> collectorSettings,
        ILogger<MonitoringWorker> logger)
    {
        _metricCollector = metricCollector;
        _eventDetector = eventDetector;
        _sampleRepository = sampleRepository;
        _foregroundAppTracker = foregroundAppTracker;
        _processOverview = processOverview;
        _monitoringState = monitoringState;
        _collectorSettings = collectorSettings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _sampleRepository.EnsureCreatedAsync(stoppingToken);

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(Math.Max(1, _collectorSettings.SampleIntervalSeconds)));

        while (!stoppingToken.IsCancellationRequested)
        {
            await CaptureAsync(stoppingToken);

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CaptureAsync(CancellationToken cancellationToken)
    {
        MetricSample sample = await _metricCollector.CollectAsync(cancellationToken);
        string? foregroundApp = _foregroundAppTracker.GetForegroundProcessName();

        await _sampleRepository.SaveMetricSampleAsync(sample, cancellationToken);

        _recentSamples.Enqueue(sample);
        TrimWindow(sample.TimestampUtc);

        IReadOnlyList<PerformanceEvent> detectedEvents = _eventDetector.Evaluate(_recentSamples.ToArray(), foregroundApp);
        PerformanceEvent? latestDetectedEvent = detectedEvents.Count > 0 ? detectedEvents[^1] : null;

        string? contributor = null;
        try
        {
            contributor = _processOverview.Observe(sample, latestDetectedEvent is not null);
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
        {
            LogHistoryUnavailable(_logger, exception);
        }
        if (latestDetectedEvent is not null && contributor is not null)
        {
            latestDetectedEvent = latestDetectedEvent with { LikelyCause = $"{contributor} {latestDetectedEvent.LikelyCause}" };
        }

        if (latestDetectedEvent is not null)
        {
            if (_lastPersistedEventAtUtc is null ||
                latestDetectedEvent.EndedAtUtc - _lastPersistedEventAtUtc >= TimeSpan.FromSeconds(_collectorSettings.EventCooldownSeconds))
            {
                await _sampleRepository.SavePerformanceEventAsync(latestDetectedEvent, cancellationToken);
                _lastPersistedEventAtUtc = latestDetectedEvent.EndedAtUtc;
            }
        }

        _monitoringState.Update(sample, latestDetectedEvent, foregroundApp);
        LogCapturedTelemetrySample(_logger, sample.TimestampUtc);
    }

    private void TrimWindow(DateTimeOffset latestTimestamp)
    {
        TimeSpan window = TimeSpan.FromSeconds(Math.Max(5, _collectorSettings.EventContextWindowSeconds));

        while (_recentSamples.Count > 0 && latestTimestamp - _recentSamples.Peek().TimestampUtc > window)
        {
            _recentSamples.Dequeue();
        }
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "App memory history is temporarily unavailable.")]
    private static partial void LogHistoryUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Debug,
        Message = "Captured telemetry sample at {TimestampUtc}")]
    private static partial void LogCapturedTelemetrySample(ILogger logger, DateTimeOffset timestampUtc);
}
