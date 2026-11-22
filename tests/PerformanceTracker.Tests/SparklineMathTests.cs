using PerformanceTracker.Core.Services;
using Xunit;

namespace PerformanceTracker.Tests;

public sealed class SparklineMathTests
{
    [Fact]
    public void ScalesToBoxAndSkipsGaps()
    {
        var points = SparklineMath.Points([0, null, 50, 100], width: 300, height: 40, max: 100);

        Assert.Equal(3, points.Count);
        Assert.Equal((0d, 40d), points[0]);
        Assert.Equal((200d, 20d), points[1]);
        Assert.Equal((300d, 0d), points[2]);
    }

    [Fact]
    public void ClampsValuesAboveMax()
    {
        var points = SparklineMath.Points([150], width: 100, height: 40, max: 100);
        Assert.Equal((0d, 0d), points[0]);
    }

    [Fact]
    public void EmptyInputGivesNoPoints()
    {
        Assert.Empty(SparklineMath.Points([], width: 100, height: 40, max: 100));
    }
}
