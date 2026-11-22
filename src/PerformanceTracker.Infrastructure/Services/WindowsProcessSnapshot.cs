using System.ComponentModel;
using System.Runtime.InteropServices;
using PerformanceTracker.Core.Models;
using PerformanceTracker.Core.Services;

namespace PerformanceTracker.Infrastructure.Services;

internal static class WindowsProcessSnapshot
{
    private const int SystemProcessInformation = 5;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private static int _capacity = 1024 * 1024;

    public static IReadOnlyList<RunningProcessInfo> Read()
    {
        int capacity = Volatile.Read(ref _capacity);
        while (true)
        {
            nint buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                int status = NtQuerySystemInformation(SystemProcessInformation, buffer, capacity, out int needed);
                if (status == StatusInfoLengthMismatch)
                {
                    capacity = checked(Math.Max(needed, capacity * 2));
                    Volatile.Write(ref _capacity, capacity);
                    continue;
                }

                if (status < 0)
                {
                    throw new Win32Exception(unchecked((int)RtlNtStatusToDosError(status)));
                }

                return Parse(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static List<RunningProcessInfo> Parse(nint buffer)
    {
        List<RunningProcessInfo> processes = [];
        while (true)
        {
            ProcessEntry entry = Marshal.PtrToStructure<ProcessEntry>(buffer);
            if (entry.Id > 0)
            {
                string image = entry.ImageBuffer == 0 ? "System" :
                    Marshal.PtrToStringUni(entry.ImageBuffer, entry.ImageLength / sizeof(char));
                string name = Path.GetFileNameWithoutExtension(image);
                processes.Add(new RunningProcessInfo
                {
                    Id = (int)entry.Id,
                    Name = name,
                    MemoryBytes = ProcessMemory.Effective(name, entry.PrivateWorkingSet, (long)entry.WorkingSet),
                    WorkingSetBytes = (long)entry.WorkingSet,
                    PrivateCommitBytes = (long)entry.PrivatePageCount,
                    TotalCpuTime = TimeSpan.FromTicks(entry.UserTime + entry.KernelTime),
                    StartTimeUtc = entry.CreateTime > 0 ? DateTimeOffset.FromFileTime(entry.CreateTime).ToUniversalTime() : null,
                    ParentId = (int)entry.ParentId,
                    SessionId = entry.SessionId
                });
            }

            if (entry.NextOffset == 0)
            {
                return processes;
            }

            buffer += entry.NextOffset;
        }
    }

    // SYSTEM_PROCESS_INFORMATION prefix. Native pointers and SIZE_T fields follow process bitness.
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessEntry
    {
        public int NextOffset;
        public int ThreadCount;
        public long PrivateWorkingSet;
        public int HardFaultCount;
        public int ThreadHighWatermark;
        public ulong CycleTime;
        public long CreateTime;
        public long UserTime;
        public long KernelTime;
        public ushort ImageLength;
        public ushort ImageMaximumLength;
        public nint ImageBuffer;
        public int BasePriority;
        public nint Id;
        public nint ParentId;
        public int HandleCount;
        public int SessionId;
        public nuint ProcessKey;
        public nuint PeakVirtualSize;
        public nuint VirtualSize;
        public int PageFaultCount;
        public nuint PeakWorkingSet;
        public nuint WorkingSet;
        public nuint PeakPagedPool;
        public nuint PagedPool;
        public nuint PeakNonPagedPool;
        public nuint NonPagedPool;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivatePageCount;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int informationClass, nint buffer, int length, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
}
