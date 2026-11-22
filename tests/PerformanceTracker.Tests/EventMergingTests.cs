using PerformanceTracker.Core.Models;
using PerformanceTracker.Core.Services;
using Xunit;

namespace PerformanceTracker.Tests;

public sealed class EventMergingTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 22, 14, 0, 0, TimeSpan.Zero);

    private static PerformanceEvent Event(int startSecond, int endSecond, string summary = "Processor maxed out",
        string cause = "Something was using a lot of processing power.", EventSeverity severity = EventSeverity.Warning) =>
        new()
        {
            StartedAtUtc = Start.AddSeconds(startSecond),
            EndedAtUtc = Start.AddSeconds(endSecond),
            Summary = summary,
            LikelyCause = cause,
            Severity = severity
        };

    [Fact]
    public void OverlappingReportsOfSameKindBecomeOneSlowdown()
    {
        PerformanceEvent? merged = EventMerging.TryMerge(Event(0, 30), Event(5, 35, severity: EventSeverity.Critical));

        Assert.NotNull(merged);
        Assert.Equal(Start, merged.StartedAtUtc);
        Assert.Equal(Start.AddSeconds(35), merged.EndedAtUtc);
        Assert.Equal(EventSeverity.Critical, merged.Severity);
    }

    [Fact]
    public void OverlappingKindsMergeAndKeepTheFirstTitle()
    {
        PerformanceEvent? merged = EventMerging.TryMerge(Event(0, 30), Event(5, 35, summary: "Disk very busy"));

        Assert.Equal("Processor maxed out", merged?.Summary);
    }

    [Fact]
    public void WorseKindTakesOverTheTitle()
    {
        PerformanceEvent? merged = EventMerging.TryMerge(
            Event(0, 30),
            Event(5, 35, summary: "Memory full, PC slowed down", severity: EventSeverity.Critical));

        Assert.Equal("Memory full, PC slowed down", merged?.Summary);
        Assert.Equal(EventSeverity.Critical, merged?.Severity);
    }

    [Fact]
    public void LongGapStartsANewSlowdown()
    {
        Assert.Null(EventMerging.TryMerge(Event(0, 30), Event(120, 150)));
    }

    [Fact]
    public void KeepsNamedCulpritOverGenericText()
    {
        PerformanceEvent? merged = EventMerging.TryMerge(
            Event(0, 30, cause: "Microsoft Defender jumped to 45% CPU."),
            Event(5, 35));

        Assert.Equal("Microsoft Defender jumped to 45% CPU.", merged?.LikelyCause);
    }

    [Theory]
    [InlineData(40, "under a minute")]
    [InlineData(180, "3 min")]
    [InlineData(3600, "1 h")]
    [InlineData(3900, "1 h 5 min")]
    public void DescribesDuration(int seconds, string expected)
    {
        Assert.Equal(expected, EventMerging.DescribeDuration(Event(0, seconds)));
    }
}
