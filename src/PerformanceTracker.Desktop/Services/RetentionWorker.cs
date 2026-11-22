using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PerformanceTracker.Core.Configuration;
using PerformanceTracker.Infrastructure.Storage;

namespace PerformanceTracker.Desktop.Services;

/// <summary>Deletes data older than the retention settings: a few minutes after start, then every few hours.</summary>
public sealed partial class RetentionWorker(
    SqliteSampleRepository repository,
    IOptions<RetentionSettings> retention,
    ILogger<RetentionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan FirstRunDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstRunDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    RetentionResult result = await Task.Run(
                        () => repository.ApplyRetention(retention.Value, DateTimeOffset.UtcNow, stoppingToken), stoppingToken);
                    LogRetention(logger, result.SamplesRemoved, result.EventsRemoved, result.SummariesRemoved, result.Compacted);
                }
                catch (SqliteException exception)
                {
                    LogRetentionFailed(logger, exception);
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(EventId = 1100, Level = LogLevel.Information,
        Message = "Retention removed {Samples} readings, {Events} slowdowns, {Summaries} daily summaries (compacted: {Compacted}).")]
    private static partial void LogRetention(ILogger logger, long samples, long events, long summaries, bool compacted);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning, Message = "Retention clean-up failed; it will retry later.")]
    private static partial void LogRetentionFailed(ILogger logger, Exception exception);
}
