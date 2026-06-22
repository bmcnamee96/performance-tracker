using System.Diagnostics;
using System.Runtime.InteropServices;
using PerformanceTracker.Core.Interfaces;
using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Infrastructure.Services;

public sealed class WindowsPerformanceCollector : IMetricCollector
{
    private ulong _previousIdleTime;
    private ulong _previousKernelTime;
    private ulong _previousUserTime;
    private bool _hasCpuBaseline;

    public Task<MetricSample> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        (double memoryUsedPercent, long availableMemoryMb) = ReadMemoryUsage();
        (string? topProcessName, double? topProcessMemoryMb) = ReadTopProcess();

        MetricSample sample = new()
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            CpuUsagePercent = ReadCpuUsagePercent(),
            MemoryUsedPercent = memoryUsedPercent,
            AvailableMemoryMb = availableMemoryMb,
            DiskActiveTimePercent = null,
            NetworkReceiveKbps = null,
            NetworkSendKbps = null,
            TopProcessName = topProcessName,
            TopProcessMemoryMb = topProcessMemoryMb,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64)
        };

        return Task.FromResult(sample);
    }

    private double? ReadCpuUsagePercent()
    {
        if (!GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user))
        {
            return null;
        }

        ulong idleValue = idle.ToUInt64();
        ulong kernelValue = kernel.ToUInt64();
        ulong userValue = user.ToUInt64();

        if (!_hasCpuBaseline)
        {
            _previousIdleTime = idleValue;
            _previousKernelTime = kernelValue;
            _previousUserTime = userValue;
            _hasCpuBaseline = true;
            return null;
        }

        ulong idleDelta = idleValue - _previousIdleTime;
        ulong kernelDelta = kernelValue - _previousKernelTime;
        ulong userDelta = userValue - _previousUserTime;
        ulong totalDelta = kernelDelta + userDelta;

        _previousIdleTime = idleValue;
        _previousKernelTime = kernelValue;
        _previousUserTime = userValue;

        if (totalDelta == 0)
        {
            return null;
        }

        double cpuPercent = (1d - ((double)idleDelta / totalDelta)) * 100d;
        return Math.Round(Math.Clamp(cpuPercent, 0, 100), 1);
    }

    private static (double MemoryUsedPercent, long AvailableMemoryMb) ReadMemoryUsage()
    {
        MemoryStatusEx memoryStatus = new()
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };

        if (!GlobalMemoryStatusEx(ref memoryStatus) || memoryStatus.TotalPhys == 0)
        {
            return (0, 0);
        }

        ulong usedBytes = memoryStatus.TotalPhys - memoryStatus.AvailPhys;
        double memoryUsedPercent = Math.Round((double)usedBytes / memoryStatus.TotalPhys * 100d, 1);
        long availableMemoryMb = (long)(memoryStatus.AvailPhys / 1024 / 1024);
        return (memoryUsedPercent, availableMemoryMb);
    }

    private static (string? TopProcessName, double? TopProcessMemoryMb) ReadTopProcess()
    {
        Process[] processes = Process.GetProcesses();
        Process? topProcess = null;
        long highestWorkingSet = 0;

        foreach (Process process in processes)
        {
            try
            {
                long workingSet = process.WorkingSet64;
                if (workingSet > highestWorkingSet)
                {
                    highestWorkingSet = workingSet;
                    topProcess = process;
                }
            }
            catch
            {
                // Skip protected or short-lived processes.
            }
        }

        if (topProcess is null)
        {
            return (null, null);
        }

        return (topProcess.ProcessName, Math.Round(highestWorkingSet / 1024d / 1024d, 1));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime lpIdleTime, out FileTime lpKernelTime, out FileTime lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;

        public ulong ToUInt64() => ((ulong)HighDateTime << 32) | LowDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}

