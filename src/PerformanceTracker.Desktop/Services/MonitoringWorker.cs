using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PerformanceTracker.Core.Configuration;
using PerformanceTracker.Core.Interfaces;
using PerformanceTracker.Core.Models;
using PerformanceTracker.Core.Services;

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
    private PerformanceEvent? _episode;
    private long _episodeId;

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
            try
            {
                await CaptureAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One bad reading (database busy, a counter failing) must never stop monitoring for good.
                LogCaptureFailed(_logger, exception);
            }

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
            // A named app is more useful than the generic explanation, so it replaces it.
            latestDetectedEvent = latestDetectedEvent with { LikelyCause = contributor };
        }

        // Show the reading live first; saving can wait on the database without freezing the dashboard.
        _monitoringState.Update(sample, latestDetectedEvent, foregroundApp);

        try
        {
            await _sampleRepository.SaveMetricSampleAsync(sample, cancellationToken);
            if (latestDetectedEvent is not null)
            {
                await SaveEpisodeAsync(latestDetectedEvent, cancellationToken);
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
        {
            LogSaveFailed(_logger, exception);
        }

        LogCapturedTelemetrySample(_logger, sample.TimestampUtc);
    }

    private async Task SaveEpisodeAsync(PerformanceEvent detected, CancellationToken cancellationToken)
    {
        // One saved row per slowdown, extended in place while it continues.
        if (_episode is not null && EventMerging.TryMerge(_episode, detected) is { } merged)
        {
            _episode = merged;
            await _sampleRepository.UpdatePerformanceEventAsync(_episodeId, merged, cancellationToken);
        }
        else
        {
            _episode = detected;
            _episodeId = await _sampleRepository.SavePerformanceEventAsync(detected, cancellationToken);
        }
    }

    private void TrimWindow(DateTimeOffset latestTimestamp)
    {
        TimeSpan window = TimeSpan.FromSeconds(Math.Max(5, _collectorSettings.EventContextWindowSeconds));

        while (_recentSamples.Count > 0 && latestTimestamp - _recentSamples.Peek().TimestampUtc > window)
        {
            _recentSamples.Dequeue();
        }
    }

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "Taking a reading failed; monitoring continues.")]
    private static partial void LogCaptureFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "Saving a reading failed (database busy?); monitoring continues.")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "App memory history is temporarily unavailable.")]
    private static partial void LogHistoryUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Debug,
        Message = "Captured telemetry sample at {TimestampUtc}")]
    private static partial void LogCapturedTelemetrySample(ILogger logger, DateTimeOffset timestampUtc);
}
