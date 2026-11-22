using PerformanceTracker.Core.Services;
using Xunit;

namespace PerformanceTracker.Tests;

public sealed class ProcessMemoryTests
{
    private const long Mb = 1024L * 1024;

    [Fact]
    public void NormalProcessesUsePrivateWorkingSet()
    {
        Assert.Equal(200 * Mb, ProcessMemory.Effective("chrome", 200 * Mb, 350 * Mb));
    }

    [Theory]
    [InlineData("vmware-vmx")]
    [InlineData("VMWARE-VMX")]
    [InlineData("Memory Compression")]
    public void VirtualMachineHostsUseFullWorkingSet(string name)
    {
        Assert.Equal(11_500 * Mb, ProcessMemory.Effective(name, 67 * Mb, 11_500 * Mb));
    }

    [Fact]
    public void NegativePrivateWorkingSetIsClampedToZero()
    {
        Assert.Equal(0, ProcessMemory.Effective("chrome", -1, 10 * Mb));
    }

    [Fact]
    public void BreakdownSplitsShownHiddenAndWindows()
    {
        MemoryBreakdown breakdown = ProcessMemory.Breakdown(usedBytes: 30_000 * Mb, allAppsBytes: 27_000 * Mb, shownAppsBytes: 25_000 * Mb);

        Assert.Equal(25_000 * Mb, breakdown.ShownBytes);
        Assert.Equal(2_000 * Mb, breakdown.HiddenBytes);
        Assert.Equal(3_000 * Mb, breakdown.WindowsBytes);
    }

    [Fact]
    public void BreakdownNeverGoesNegative()
    {
        MemoryBreakdown breakdown = ProcessMemory.Breakdown(usedBytes: 10 * Mb, allAppsBytes: 12 * Mb, shownAppsBytes: 15 * Mb);

        Assert.Equal(12 * Mb, breakdown.ShownBytes);
        Assert.Equal(0, breakdown.HiddenBytes);
        Assert.Equal(0, breakdown.WindowsBytes);
    }
}
