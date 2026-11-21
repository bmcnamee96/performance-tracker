using Microsoft.Extensions.Hosting;
using PerformanceTracker.Core.Interfaces;
using PerformanceTracker.Core.Models;
using PerformanceTracker.Core.Services;

namespace PerformanceTracker.Desktop.Services;

/// <summary>
/// Keeps a running picture of which apps are busy or quiet. It observes cheaply in the
/// background (so "quiet for 40 min" is meaningful when you open the Apps tab) and does a
/// detailed capture when the UI asks.
/// </summary>
public sealed class ProcessOverviewService : BackgroundService
{
    private static readonly TimeSpan BackgroundInterval = TimeSpan.FromSeconds(30);

    private readonly IProcessInventory _inventory;
    private readonly ProcessActivityTracker _tracker = new(Environment.ProcessorCount);
    private readonly object _lock = new();

    public ProcessOverviewService(IProcessInventory inventory)
    {
        _inventory = inventory;
    }

    /// <summary>Captures the current process list with details and groups it into apps.</summary>
    public Task<IReadOnlyList<AppGroup>> GetAppsAsync() =>
        Task.Run(() =>
        {
            IReadOnlyList<AppGroup> groups = Observe(includeDetails: true);
            AppRowViewModel.CacheIcons(groups);
            return groups;
        });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(BackgroundInterval);
        do
        {
            try
            {
                _ = Observe(includeDetails: false);
            }
            catch
            {
                // Observation is best effort; try again next tick.
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private IReadOnlyList<AppGroup> Observe(bool includeDetails)
    {
        lock (_lock)
        {
            IReadOnlyList<RunningProcessInfo> processes = _inventory.Capture(includeDetails);
            IReadOnlyDictionary<int, ProcessActivity> activity = _tracker.Observe(processes, DateTimeOffset.UtcNow);
            return includeDetails ? AppGroupBuilder.Build(processes, activity, Environment.ProcessId) : [];
        }
    }
}
