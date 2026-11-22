using System.Globalization;
using System.Text.RegularExpressions;
using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Core.Services;

/// <summary>Summary numbers for a range of slowdowns.</summary>
public sealed record SlowdownTotals(int Count, TimeSpan TotalTime, string? MostCommon);

/// <summary>Cleans up stored slowdowns and summarises them for the Event Timeline.</summary>
public static partial class SlowdownHistory
{
    // Titles written by older versions ("CPU pressure built up over 25s while ...") and their plain replacements.
    private static readonly (string Legacy, string Title, string Cause)[] LegacyTitles =
    [
        ("CPU pressure during network burst", "Processor busy during a big download", "Often a game update, cloud sync or streaming."),
        ("CPU and disk contention", "Processor and disk both busy", "Often an update, a virus scan or something installing."),
        ("Memory pressure with disk contention", "Memory full, PC slowed down", "Windows ran low on memory and started using the disk instead."),
        ("Background transfer contention", "Big download while PC was busy", "A download, sync or stream added to the load."),
        ("CPU pressure", "Processor maxed out", "Something was using a lot of processing power."),
        ("RAM pressure", "Memory almost full", "Free memory ran low. Closing apps you aren't using helps."),
        ("Disk pressure", "Disk very busy", "Often an update, a virus scan, indexing or a big file copy.")
    ];

    /// <summary>Rewrites wordy text saved by older versions into the current short form. Current events pass through.</summary>
    public static PerformanceEvent Normalize(PerformanceEvent performanceEvent)
    {
        int marker = performanceEvent.Summary.IndexOf(" built up over ", StringComparison.Ordinal);
        if (marker < 0)
        {
            return performanceEvent;
        }

        string legacyTitle = performanceEvent.Summary[..marker];
        (string Legacy, string Title, string Cause) match = LegacyTitles.FirstOrDefault(entry => entry.Legacy == legacyTitle);
        string title = match.Title ?? legacyTitle;

        string cause = match.Cause ?? "The cause wasn't clear.";
        Match cpu = LegacyCpuContributor().Match(performanceEvent.LikelyCause);
        Match memory = LegacyMemoryContributor().Match(performanceEvent.LikelyCause);
        if (cpu.Success)
        {
            double percent = double.Parse(cpu.Groups[2].Value, CultureInfo.InvariantCulture);
            cause = string.Create(CultureInfo.CurrentCulture, $"{cpu.Groups[1].Value} jumped to {percent:0}% CPU.");
        }
        else if (memory.Success)
        {
            cause = $"{memory.Groups[1].Value} grew by {memory.Groups[2].Value}.";
        }

        return performanceEvent with { Summary = title, LikelyCause = cause };
    }

    /// <summary>Normalizes, sorts and merges overlapping reports into one entry per slowdown (oldest first).</summary>
    public static IReadOnlyList<PerformanceEvent> Consolidate(IEnumerable<PerformanceEvent> events)
    {
        List<PerformanceEvent> result = [];
        foreach (PerformanceEvent next in events.Select(Normalize).OrderBy(e => e.StartedAtUtc).ThenBy(e => e.EndedAtUtc))
        {
            if (result.Count > 0 && EventMerging.TryMerge(result[^1], next) is { } merged)
            {
                result[^1] = merged;
            }
            else if (result.Count > 0 && next.EndedAtUtc <= result[^1].EndedAtUtc)
            {
                // Fully inside the slowdown we already have.
            }
            else
            {
                result.Add(next);
            }
        }

        return result;
    }

    public static SlowdownTotals Totals(IReadOnlyList<PerformanceEvent> events)
    {
        TimeSpan total = events.Aggregate(TimeSpan.Zero, (sum, e) => sum + Length(e));
        string? mostCommon = events
            .GroupBy(e => e.Summary)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Aggregate(TimeSpan.Zero, (sum, e) => sum + Length(e)))
            .Select(group => group.Key)
            .FirstOrDefault();
        return new SlowdownTotals(events.Count, total, mostCommon);
    }

    /// <summary>Minutes of slowdown that fall inside each bucket from <paramref name="start"/>, clipped to bucket edges.</summary>
    public static double[] MinutesPerBucket(IReadOnlyList<PerformanceEvent> events, DateTimeOffset start, TimeSpan bucket, int count)
    {
        double[] minutes = new double[count];
        foreach (PerformanceEvent performanceEvent in events)
        {
            for (int i = 0; i < count; i++)
            {
                DateTimeOffset from = start + bucket * i;
                DateTimeOffset to = from + bucket;
                DateTimeOffset overlapStart = performanceEvent.StartedAtUtc > from ? performanceEvent.StartedAtUtc : from;
                DateTimeOffset overlapEnd = performanceEvent.EndedAtUtc < to ? performanceEvent.EndedAtUtc : to;
                if (overlapEnd > overlapStart)
                {
                    minutes[i] += (overlapEnd - overlapStart).TotalMinutes;
                }
            }
        }

        return minutes;
    }

    private static TimeSpan Length(PerformanceEvent performanceEvent) =>
        performanceEvent.EndedAtUtc > performanceEvent.StartedAtUtc ? performanceEvent.EndedAtUtc - performanceEvent.StartedAtUtc : TimeSpan.Zero;

    [GeneratedRegex(@"^(.+?) jumped to ([\d.]+)% CPU")]
    private static partial Regex LegacyCpuContributor();

    [GeneratedRegex(@"^(.+?) used (.+?) more memory")]
    private static partial Regex LegacyMemoryContributor();
}
