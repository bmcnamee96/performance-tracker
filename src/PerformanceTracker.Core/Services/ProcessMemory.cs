namespace PerformanceTracker.Core.Services;

/// <summary>Chooses which memory figure to show for a process and splits the system total into parts.</summary>
public static class ProcessMemory
{
    // Processes whose real memory Windows labels as shared, so their private working set is misleadingly small:
    // virtual machine hosts map the guest's RAM from a file, and Memory Compression holds compressed pages.
    private static readonly HashSet<string> WorkingSetProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "vmware-vmx",
        "VirtualBoxVM",
        "VBoxHeadless",
        "qemu-system-x86_64",
        "Memory Compression"
    };

    /// <summary>True for processes whose shown memory is the full working set (VM hosts, Memory Compression).</summary>
    public static bool UsesWorkingSet(string name) =>
        WorkingSetProcesses.Contains(name);

    /// <summary>Memory to show for one process: private working set, or the full working set for VM hosts.</summary>
    public static long Effective(string name, long privateWorkingSet, long workingSet)
    {
        long privateBytes = Math.Max(0, privateWorkingSet);
        return WorkingSetProcesses.Contains(name) ? Math.Max(privateBytes, workingSet) : privateBytes;
    }

    /// <summary>
    /// Splits memory in use into apps shown in the list, apps hidden by filters, and what Windows itself holds
    /// (kernel, drivers, file cache, shared code) that no single app accounts for.
    /// </summary>
    public static MemoryBreakdown Breakdown(long usedBytes, long allAppsBytes, long shownAppsBytes)
    {
        long shown = Math.Clamp(shownAppsBytes, 0, Math.Max(0, allAppsBytes));
        long hidden = Math.Max(0, allAppsBytes - shown);
        long windows = Math.Max(0, usedBytes - allAppsBytes);
        return new MemoryBreakdown(shown, hidden, windows);
    }
}

public sealed record MemoryBreakdown(long ShownBytes, long HiddenBytes, long WindowsBytes);
