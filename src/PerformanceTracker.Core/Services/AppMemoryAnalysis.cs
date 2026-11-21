using System.Globalization;
using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Core.Services;

public sealed record AppMemoryGrowth(AppMemorySample Latest, string Reason);

public static class AppMemoryAnalysis
{
    private const long MiB = 1024 * 1024;

    public static AppMemoryGrowth? DetectGrowth(IReadOnlyList<AppMemorySample> samples)
    {
        if (samples.Count < 5)
        {
            return null;
        }

        AppMemorySample latest = samples[^1];
        TimeSpan span = latest.TimestampUtc - samples[0].TimestampUtc;
        if (span < TimeSpan.FromMinutes(30) || span > TimeSpan.FromMinutes(60))
        {
            return null;
        }

        if (samples.Zip(samples.Skip(1), (before, after) => after.TimestampUtc - before.TimestampUtc)
            .Any(gap => gap > TimeSpan.FromMinutes(3)))
        {
            return null;
        }

        // Compare medians across six time buckets to smooth minute-to-minute churn.
        var buckets = samples.GroupBy(sample => Math.Min(5,
            (int)((sample.TimestampUtc - samples[0].TimestampUtc).Ticks * 6 / span.Ticks)))
            .Select(bucket => Median(bucket)).ToArray();
        if (buckets.Length != 6)
        {
            return null;
        }

        long gain = buckets[^1] - buckets[0];
        if (gain < 1024 * MiB && (gain < 100 * MiB || gain < buckets[0] / 2))
        {
            return null;
        }

        long[] changes = buckets.Zip(buckets.Skip(1), (before, after) => after - before).ToArray();
        return changes.Count(change => change >= gain / 12) >= 4 &&
            changes.All(change => change >= -gain / 10) && changes.Max() <= gain / 2
            ? new(latest, DescribeChange(buckets[0], buckets[^1], span))
            : null;
    }

    public static string DescribeTrend(IReadOnlyList<AppMemorySample> samples)
    {
        if (samples.Count < 2)
        {
            return "Collecting memory history.";
        }

        long first = samples[0].MemoryBytes;
        long last = samples[^1].MemoryBytes;
        return Math.Abs(last - first) < Math.Max(25 * MiB, first / 10)
            ? $"Steady around {FormatMemory(Median(samples))}."
            : DescribeChange(first, last, samples[^1].TimestampUtc - samples[0].TimestampUtc);
    }

    public static string? DescribeContributor(IReadOnlyList<AppMemorySample> previous, IReadOnlyList<AppMemorySample> current)
    {
        var prior = previous.ToDictionary(sample => sample.Key, StringComparer.Ordinal);
        var changes = current.Where(sample => sample.ProcessCount > 0).Select(sample =>
        {
            prior.TryGetValue(sample.Key, out AppMemorySample? before);
            return (Sample: sample, Cpu: sample.CpuPercent - (before?.CpuPercent ?? 0),
                Memory: sample.MemoryBytes - (before?.MemoryBytes ?? 0));
        }).ToArray();

        var cpu = changes.Where(change => change.Cpu >= 5).DefaultIfEmpty().MaxBy(change => change.Cpu);
        var memory = changes.Where(change => change.Memory >= 100 * MiB).DefaultIfEmpty().MaxBy(change => change.Memory);
        List<string> reasons = [];
        if (cpu.Sample is not null)
        {
            reasons.Add($"{cpu.Sample.DisplayName} jumped to {cpu.Sample.CpuPercent:0.#}% CPU (up {cpu.Cpu:0.#} percentage points).");
        }

        if (memory.Sample is not null)
        {
            reasons.Add($"{memory.Sample.DisplayName} used {FormatMemory(memory.Memory)} more memory.");
        }

        return reasons.Count == 0 ? null : string.Join(' ', reasons);
    }

    private static long Median(IEnumerable<AppMemorySample> samples)
    {
        long[] values = samples.Select(sample => sample.MemoryBytes).Order().ToArray();
        return values[values.Length / 2];
    }

    private static string DescribeChange(long first, long last, TimeSpan elapsed)
    {
        int minutes = Math.Max(1, (int)Math.Round(elapsed.TotalMinutes));
        string window = minutes >= 120 ? $"{elapsed.TotalHours:0.#} hours" : $"{minutes} minutes";
        return $"{(last > first ? "Grew" : "Fell")} from {FormatMemory(first)} to {FormatMemory(last)} in the last {window}.";
    }

    private static string FormatMemory(long bytes) => bytes >= 1024 * MiB
        ? string.Create(CultureInfo.CurrentCulture, $"{(double)bytes / (1024 * MiB):0.#} GB")
        : string.Create(CultureInfo.CurrentCulture, $"{(double)bytes / MiB:0.#} MB");
}
