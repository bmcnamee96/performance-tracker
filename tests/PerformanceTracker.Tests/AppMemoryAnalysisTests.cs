using PerformanceTracker.Core.Models;
using PerformanceTracker.Core.Services;
using Xunit;

namespace PerformanceTracker.Tests;

public sealed class AppMemoryAnalysisTests
{
    private const long Mb = 1024 * 1024;
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, false)]
    [InlineData(20, true)]
    [InlineData(1, false)]
    public void FindsSustainedMeaningfulGrowth(int gainPerMinute, bool expected)
    {
        var samples = Enumerable.Range(0, 61)
            .Select(minute => Sample(minute, 1000 + minute * gainPerMinute))
            .ToArray();

        Assert.Equal(expected, AppMemoryAnalysis.DetectGrowth(samples) is not null);
    }

    [Fact]
    public void IgnoresSingleJumpAndGaps()
    {
        var samples = Enumerable.Range(0, 61)
            .Select(minute => Sample(minute, minute < 30 ? 1000 : 3000))
            .ToArray();

        Assert.Null(AppMemoryAnalysis.DetectGrowth(samples));
        Assert.Null(AppMemoryAnalysis.DetectGrowth(Enumerable.Range(0, 31)
            .Select(minute => Sample(minute < 15 ? minute : minute + 20, 1000 + minute * 100)).ToArray()));
    }

    [Fact]
    public void DescribesSteadyAndRisingMemory()
    {
        Assert.Equal("Steady around 500 MB.", AppMemoryAnalysis.DescribeTrend([Sample(0, 500), Sample(60, 500)]));
        Assert.Equal("Grew from 500 MB to 2 GB in the last 2 hours.",
            AppMemoryAnalysis.DescribeTrend([Sample(0, 500), Sample(120, 2048)]));
    }

    [Fact]
    public void FindsLargestJumpRatherThanLargestApp()
    {
        AppMemorySample steady = Sample(0, 4000) with { Key = "other", DisplayName = "Other", CpuPercent = 60 };
        Assert.Null(AppMemoryAnalysis.DescribeContributor([steady], [steady]));
        Assert.Equal("Example jumped to 40% CPU. It also grew by 200 MB.",
            AppMemoryAnalysis.DescribeContributor([steady, Sample(0, 200) with { CpuPercent = 5 }],
                [steady, Sample(1, 400) with { CpuPercent = 40 }]));
    }

    private static AppMemorySample Sample(int minute, long memoryMb) =>
        new("example", "Example", Start.AddMinutes(minute), memoryMb * Mb, 0, 1);
}
