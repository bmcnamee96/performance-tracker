using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Core.Services;

/// <summary>
/// Remembers CPU time per process between observations so callers can tell
/// how busy each process is now and how long it has been quiet.
/// </summary>
public sealed class ProcessActivityTracker
{
    /// <summary>CPU share (of the whole machine) below which a process counts as quiet.</summary>
    public const double QuietCpuPercent = 0.2;

    private readonly int _processorCount;
    private readonly Dictionary<(int Id, DateTimeOffset? Start), Entry> _entries = [];

    public ProcessActivityTracker(int processorCount)
    {
        _processorCount = Math.Max(1, processorCount);
    }

    /// <summary>
    /// Records a new observation and returns activity for every process in it.
    /// Processes that are no longer running are forgotten.
    /// </summary>
    public IReadOnlyDictionary<int, ProcessActivity> Observe(IReadOnlyList<RunningProcessInfo> processes, DateTimeOffset nowUtc)
    {
        Dictionary<int, ProcessActivity> result = new(processes.Count);
        HashSet<(int, DateTimeOffset?)> seen = [];

        foreach (RunningProcessInfo process in processes)
        {
            (int, DateTimeOffset?) key = (process.Id, process.StartTimeUtc);
            seen.Add(key);

            if (!_entries.TryGetValue(key, out Entry? entry))
            {
                entry = new Entry(nowUtc, process.TotalCpuTime, nowUtc, nowUtc);
                _entries[key] = entry;
                result[process.Id] = new ProcessActivity(0, null, ObservedFor: TimeSpan.Zero);
                continue;
            }

            double cpuPercent = 0;
            bool valid = process.StartTimeUtc <= nowUtc && process.TotalCpuTime >= entry.LastCpuTime && entry.LastCpuTime is not null && nowUtc > entry.LastSeenUtc;
            TimeSpan wall = nowUtc - entry.LastSeenUtc;
            if (process.TotalCpuTime is TimeSpan cpu && entry.LastCpuTime is TimeSpan lastCpu && wall > TimeSpan.Zero)
            {
                double cpuDelta = Math.Max(0, (cpu - lastCpu).TotalMilliseconds);
                cpuPercent = Math.Min(100, cpuDelta / (wall.TotalMilliseconds * _processorCount) * 100);
            }

            DateTimeOffset lastActive = !valid || cpuPercent >= QuietCpuPercent ? nowUtc : entry.LastActiveUtc;
            entry = entry with { LastSeenUtc = nowUtc, LastCpuTime = process.TotalCpuTime, LastActiveUtc = lastActive };
            _entries[key] = entry;

            TimeSpan? quietFor = valid && process.StartTimeUtc is DateTimeOffset start
                ? nowUtc - (start > lastActive ? start : lastActive)
                : null;
            result[process.Id] = new ProcessActivity(cpuPercent, quietFor, nowUtc - entry.FirstSeenUtc);
        }

        foreach ((int, DateTimeOffset?) stale in _entries.Keys.Where(key => !seen.Contains(key)).ToArray())
        {
            _entries.Remove(stale);
        }

        return result;
    }

    private sealed record Entry(DateTimeOffset FirstSeenUtc, TimeSpan? LastCpuTime, DateTimeOffset LastSeenUtc, DateTimeOffset LastActiveUtc);
}

/// <param name="CpuPercent">Share of total machine CPU since the previous observation.</param>
/// <param name="QuietFor">Time since the process last used meaningful CPU, or null when CPU time is unreadable.</param>
/// <param name="ObservedFor">How long the tracker has been watching this process.</param>
public sealed record ProcessActivity(double CpuPercent, TimeSpan? QuietFor, TimeSpan ObservedFor);
