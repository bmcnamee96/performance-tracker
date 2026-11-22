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

    /// <summary>
    /// Count, time and most common kind. When a window is given, only the part of each slowdown inside it counts,
    /// so a slowdown from 11:55 p.m. to 12:05 a.m. adds 5 minutes to each day.
    /// </summary>
    public static SlowdownTotals Totals(IReadOnlyList<PerformanceEvent> events, DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        DateTimeOffset start = from ?? DateTimeOffset.MinValue;
        DateTimeOffset end = to ?? DateTimeOffset.MaxValue;
        PerformanceEvent[] inside = events.Where(e => e.EndedAtUtc >= start && e.StartedAtUtc <= end).ToArray();
        TimeSpan total = inside.Aggregate(TimeSpan.Zero, (sum, e) => sum + Overlap(e, start, end));
        string? mostCommon = inside
            .GroupBy(e => e.Summary)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => group.Aggregate(TimeSpan.Zero, (sum, e) => sum + Overlap(e, start, end)))
            .Select(group => group.Key)
            .FirstOrDefault();
        return new SlowdownTotals(inside.Length, total, mostCommon);
    }

    /// <summary>
    /// Minutes of slowdown inside each bucket. <paramref name="boundaries"/> holds bucket edges in order
    /// (n + 1 edges for n buckets), so callers can use local midnights and clock-change days of 23 or 25 hours.
    /// </summary>
    public static double[] MinutesPerBucket(IReadOnlyList<PerformanceEvent> events, IReadOnlyList<DateTimeOffset> boundaries)
    {
        int count = Math.Max(0, boundaries.Count - 1);
        double[] minutes = new double[count];
        foreach (PerformanceEvent performanceEvent in events)
        {
            for (int i = 0; i < count; i++)
            {
                minutes[i] += Overlap(performanceEvent, boundaries[i], boundaries[i + 1]).TotalMinutes;
            }
        }

        return minutes;
    }

    /// <summary>Bucket edges for one local day, one per hour of real time (23, 24 or 25 buckets).</summary>
    public static IReadOnlyList<DateTimeOffset> HourBoundaries(DateTime localDay, TimeZoneInfo zone)
    {
        DateTimeOffset start = LocalMidnight(localDay, zone);
        DateTimeOffset end = LocalMidnight(localDay.AddDays(1), zone);
        List<DateTimeOffset> edges = [];
        for (DateTimeOffset edge = start; edge < end; edge = edge.AddHours(1))
        {
            edges.Add(edge);
        }

        edges.Add(end);
        return edges;
    }

    /// <summary>Bucket edges at each local midnight from <paramref name="firstLocalDay"/> for <paramref name="days"/> days.</summary>
    public static IReadOnlyList<DateTimeOffset> DayBoundaries(DateTime firstLocalDay, int days, TimeZoneInfo zone) =>
        Enumerable.Range(0, days + 1).Select(i => LocalMidnight(firstLocalDay.AddDays(i), zone)).ToArray();

    /// <summary>The instant a local calendar day begins in <paramref name="zone"/>.</summary>
    public static DateTimeOffset LocalMidnight(DateTime localDay, TimeZoneInfo zone)
    {
        DateTime midnight = DateTime.SpecifyKind(localDay.Date, DateTimeKind.Unspecified);
        // If midnight itself is skipped by a clock change, step forward to the first valid local time.
        while (zone.IsInvalidTime(midnight))
        {
            midnight = midnight.AddMinutes(30);
        }

        return new DateTimeOffset(midnight, zone.GetUtcOffset(midnight));
    }

    private static TimeSpan Overlap(PerformanceEvent performanceEvent, DateTimeOffset from, DateTimeOffset to)
    {
        DateTimeOffset start = performanceEvent.StartedAtUtc > from ? performanceEvent.StartedAtUtc : from;
        DateTimeOffset end = performanceEvent.EndedAtUtc < to ? performanceEvent.EndedAtUtc : to;
        return end > start ? end - start : TimeSpan.Zero;
    }

    [GeneratedRegex(@"^(.+?) jumped to ([\d.]+)% CPU")]
    private static partial Regex LegacyCpuContributor();

    [GeneratedRegex(@"^(.+?) used (.+?) more memory")]
    private static partial Regex LegacyMemoryContributor();
}
