using PerformanceTracker.Core.Models;
using PerformanceTracker.Core.Services;
using Xunit;

namespace PerformanceTracker.Tests;

public sealed class SlowdownHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 22, 14, 0, 0, TimeSpan.Zero);

    private static PerformanceEvent Event(int startSecond, int endSecond, string summary = "Processor maxed out", string cause = "Something was using a lot of processing power.") =>
        new()
        {
            StartedAtUtc = Start.AddSeconds(startSecond),
            EndedAtUtc = Start.AddSeconds(endSecond),
            Summary = summary,
            LikelyCause = cause,
            Severity = EventSeverity.Warning
        };

    [Fact]
    public void RewritesLegacyWordyEvents()
    {
        PerformanceEvent legacy = Event(0, 25,
            "CPU pressure built up over 25s while explorer was active: CPU usage stayed high across the recent window.",
            "Microsoft Defender jumped to 45.1% CPU (up 24.3 percentage points). CPU demand built up across recent samples, which points to compute-heavy work.");

        PerformanceEvent normalized = SlowdownHistory.Normalize(legacy);

        Assert.Equal("Processor maxed out", normalized.Summary);
        Assert.Equal("Microsoft Defender jumped to 45% CPU.", normalized.LikelyCause);
    }

    [Fact]
    public void LegacyEventWithoutContributorGetsShortGenericCause()
    {
        PerformanceEvent legacy = Event(0, 25,
            "Disk pressure built up over 25s while T3 Code was active: storage stayed saturated.",
            "Storage activity became the dominant bottleneck, which points to scans, indexing, patching, or asset loads competing for disk time.");

        PerformanceEvent normalized = SlowdownHistory.Normalize(legacy);

        Assert.Equal("Disk very busy", normalized.Summary);
        Assert.Equal("Often an update, a virus scan, indexing or a big file copy.", normalized.LikelyCause);
    }

    [Fact]
    public void CurrentEventsPassThrough()
    {
        PerformanceEvent current = Event(0, 30);
        Assert.Same(current, SlowdownHistory.Normalize(current));
    }

    [Fact]
    public void ConsolidatesSavedReportsIntoOneSlowdownPerRun()
    {
        PerformanceEvent[] saved =
        [
            Event(0, 30), Event(30, 60), Event(60, 90),
            Event(40, 70, summary: "Disk very busy"),
            Event(600, 630)
        ];

        IReadOnlyList<PerformanceEvent> result = SlowdownHistory.Consolidate(saved);

        Assert.Equal(2, result.Count);
        Assert.Equal("Processor maxed out", result[0].Summary);
        Assert.Equal(Start.AddSeconds(90), result[0].EndedAtUtc);
        Assert.Equal(Start.AddSeconds(600), result[1].StartedAtUtc);
    }

    [Fact]
    public void TotalsCountTimeAndMostCommonKind()
    {
        SlowdownTotals totals = SlowdownHistory.Totals(
        [
            Event(0, 60), Event(600, 720), Event(1000, 1030, summary: "Disk very busy")
        ]);

        Assert.Equal(3, totals.Count);
        Assert.Equal(TimeSpan.FromSeconds(210), totals.TotalTime);
        Assert.Equal("Processor maxed out", totals.MostCommon);
    }

    [Fact]
    public void SplitsMinutesAcrossBuckets()
    {
        // 13:50 to 14:20 spans two hourly buckets starting at 13:00.
        PerformanceEvent spanning = new()
        {
            StartedAtUtc = Start.AddMinutes(-10),
            EndedAtUtc = Start.AddMinutes(20),
            Summary = "Processor maxed out"
        };

        double[] minutes = SlowdownHistory.MinutesPerBucket([spanning], Start.AddHours(-1), TimeSpan.FromHours(1), 3);

        Assert.Equal(new double[] { 10, 20, 0 }, minutes);
    }
}
