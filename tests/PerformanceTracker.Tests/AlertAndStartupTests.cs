using System.Buffers.Binary;
using PerformanceTracker.Core.Services;
using Xunit;

namespace PerformanceTracker.Tests;

public sealed class AlertAndStartupTests
{
    [Fact]
    public void MemoryRequiresSustainedPressureAndWorseningWithinAnEpisode()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        AlertRules rules = new();
        Assert.False(rules.Memory(now, 90, 90, false));
        Assert.False(rules.Memory(now.AddSeconds(1), 93, 90, false));
        Assert.False(rules.Memory(now.AddMinutes(1), 93, 90, false));
        Assert.False(rules.Memory(now.AddSeconds(61), 93, 90, true));
        Assert.True(rules.Memory(now.AddSeconds(62), 93, 90, false));
        Assert.False(rules.Memory(now.AddMinutes(10), 97, 90, false));
        Assert.False(rules.Memory(now.AddMinutes(32), 95, 90, false));
        Assert.True(rules.Memory(now.AddMinutes(33), 96, 90, false));
        Assert.False(rules.Memory(now.AddMinutes(64), 89, 90, false));
        Assert.False(rules.Memory(now.AddMinutes(65), 91, 90, false));
        Assert.True(rules.Memory(now.AddMinutes(66), 91, 90, false));
    }

    [Fact]
    public void GrowthCooldownIsPerAppAndQuietDoesNotConsumeIt()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        AlertRules rules = new();
        Assert.False(rules.Growth(now, "vite", true));
        Assert.True(rules.Growth(now, "vite", false));
        Assert.True(rules.Growth(now, "chrome", false));
        Assert.False(rules.Growth(now.AddMinutes(29), "vite", false));
        Assert.True(rules.Growth(now.AddMinutes(30), "vite", false));
    }

    [Fact]
    public void StartupApprovalUsesWindowsBinaryLayout()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;
        byte[] disabled = StartupApproval.Write(false, now);
        Assert.Equal(12, disabled.Length);
        Assert.Equal(3, disabled[0]);
        Assert.Equal(now.ToFileTime(), BinaryPrimitives.ReadInt64LittleEndian(disabled.AsSpan(4)));
        Assert.False(StartupApproval.Read(disabled));
        Assert.Equal(new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, StartupApproval.Write(true, now, disabled));
        Assert.True(StartupApproval.Read(null));
        Assert.Null(StartupApproval.Read([3]));
        byte[] alternate = StartupApproval.Write(false, now, [6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        Assert.Equal(7, alternate[0]);
        Assert.False(StartupApproval.Read(alternate));
    }

    [Fact]
    public void MergesApprovalWithItsOwnSourceAndIgnoresOrphans()
    {
        var approvals = new[]
        {
            new StartupApprovalRecord("Run", "Example", StartupApproval.Write(false, DateTimeOffset.UnixEpoch)),
            new StartupApprovalRecord("Run32", "Other", StartupApproval.Write(false, DateTimeOffset.UnixEpoch))
        }.ToLookup(record => record.Bucket, StringComparer.OrdinalIgnoreCase);

        Assert.False(StartupApproval.Match(approvals, "run", "example"));
        Assert.True(StartupApproval.Match(approvals, "Run32", "Example"));
        Assert.True(StartupApproval.Match(approvals, "StartupFolder", "Example"));
    }

    [Theory]
    [InlineData("SecurityHealthSystray", true)]
    [InlineData("OneDrive", true)]
    [InlineData("RtkAudUService64", true)]
    [InlineData("lghub", true)]
    [InlineData("Spotify", false)]
    public void StartupRiskUsesPlainKeepRecommendation(string process, bool expected) =>
        Assert.Equal(expected, StartupApproval.RecommendedToKeep(process, process));
}
