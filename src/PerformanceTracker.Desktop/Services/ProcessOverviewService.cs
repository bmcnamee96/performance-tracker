using Microsoft.Extensions.Options;
using PerformanceTracker.Core.Configuration;
using PerformanceTracker.Core.Interfaces;
using PerformanceTracker.Core.Models;
using PerformanceTracker.Core.Services;
using PerformanceTracker.Infrastructure.Storage;

namespace PerformanceTracker.Desktop.Services;

public sealed class ProcessOverviewService(
    IProcessInventory inventory,
    SqliteSampleRepository repository,
    IOptions<RetentionSettings> retention)
{
    private readonly ProcessActivityTracker _tracker = new(Environment.ProcessorCount);
    private readonly ProcessActivityTracker _eventTracker = new(Environment.ProcessorCount);
    private readonly object _lock = new();
    private Dictionary<string, AppMemoryGrowth> _growth = new(StringComparer.Ordinal);
    private AppMemorySample[] _previous = [];
    private long _lastMinute = -1;
    private DateTimeOffset _nextPrune;
    private DateTimeOffset _lastActivityObservation;

    public event EventHandler<AppMemoryGrowth>? GrowthDetected;

    public AppMemoryGrowth? GetGrowth(string key) => Volatile.Read(ref _growth).GetValueOrDefault(key);

    public Task<IReadOnlyList<AppMemorySample>> ReadHistoryAsync(string key) =>
        Task.Run(() => repository.ReadAppMemorySamples(key, DateTimeOffset.UtcNow.AddHours(-3)));

    public Task<IReadOnlyList<AppGroup>> GetAppsAsync() =>
        Task.Run(() =>
        {
            IReadOnlyList<AppGroup> groups;
            lock (_lock)
            {
                IReadOnlyList<RunningProcessInfo> processes = inventory.Capture(includeDetails: true);
                _lastActivityObservation = DateTimeOffset.UtcNow;
                var activity = _tracker.Observe(processes, _lastActivityObservation);
                groups = AppGroupBuilder.Build(processes, activity, Environment.ProcessId);
            }

            AppRowViewModel.CacheIcons(groups);
            return groups;
        });

    private static string DescribeForHistory(string key, RunningProcessInfo[] members)
    {
        string name = ProcessCatalog.Lookup(key)?.DisplayName ?? members[0].FileDescription ?? members[0].Name;
        if (!DevProcessRecognizer.IsHost(key))
        {
            return name;
        }

        RunningProcessInfo heaviest = members.MaxBy(process => process.MemoryBytes) ?? members[0];
        return AppGroupBuilder.DescribeDevActivity(heaviest) is { } activity ? $"{name} ({activity})" : name;
    }

    public string? Observe(MetricSample sample, bool attribute)
    {
        bool hasBaseline = _previous.Length != 0;
        IReadOnlyDictionary<int, ProcessActivity> activity;
        lock (_lock)
        {
            activity = _eventTracker.Observe(sample.Processes, sample.TimestampUtc);
            if (sample.TimestampUtc > _lastActivityObservation && sample.Processes.Count > 0)
            {
                _tracker.Observe(sample.Processes, sample.TimestampUtc);
                _lastActivityObservation = sample.TimestampUtc;
            }
        }

        AppMemorySample[] current = sample.Processes
            .GroupBy(process => process.Name.Trim().ToLowerInvariant())
            .Select(group => new AppMemorySample(
                group.Key,
                DescribeForHistory(group.Key, group.ToArray()),
                sample.TimestampUtc,
                group.Sum(process => process.MemoryBytes),
                group.Sum(process => activity[process.Id].CpuPercent),
                group.Count()))
            .ToArray();

        string? contributor = attribute && hasBaseline ? AppMemoryAnalysis.DescribeContributor(_previous, current) : null;
        _previous = current;
        long minute = sample.TimestampUtc.ToUnixTimeSeconds() / 60;
        if (minute <= _lastMinute || current.Length == 0)
        {
            return contributor;
        }

        DateTimeOffset? cutoff = sample.TimestampUtc >= _nextPrune
            ? sample.TimestampUtc.AddDays(-Math.Max(1, retention.Value.AppMemoryRetentionDays))
            : null;
        repository.SaveAppMemorySamples(current, cutoff);
        if (cutoff.HasValue)
        {
            _nextPrune = sample.TimestampUtc.AddHours(1);
        }

        _lastMinute = minute;
        HashSet<string> running = current.Select(app => app.Key).ToHashSet(StringComparer.Ordinal);
        var next = repository.ReadAppMemorySamples(null, sample.TimestampUtc.AddMinutes(-60))
            .GroupBy(point => point.Key)
            .Where(group => running.Contains(group.Key))
            .Select(group => AppMemoryAnalysis.DetectGrowth(group.ToArray()))
            .OfType<AppMemoryGrowth>()
            .ToDictionary(growth => growth.Latest.Key, StringComparer.Ordinal);
        var previous = Interlocked.Exchange(ref _growth, next);
        foreach (AppMemoryGrowth growth in next.Values)
        {
            if (!previous.ContainsKey(growth.Latest.Key))
            {
                GrowthDetected?.Invoke(this, growth);
            }
        }

        return contributor;
    }
}
