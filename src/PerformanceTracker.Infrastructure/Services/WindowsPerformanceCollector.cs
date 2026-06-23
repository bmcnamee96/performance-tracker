using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.Win32;
using PerformanceTracker.Core.Interfaces;
using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Infrastructure.Services;

public sealed class WindowsPerformanceCollector : IMetricCollector
{
    private readonly ForegroundWindowTracker _foregroundWindowTracker = new();

    private ulong _previousCpuIdleTime;
    private ulong _previousCpuKernelTime;
    private ulong _previousCpuUserTime;
    private bool _hasCpuBaseline;
    private double? _lastCpuUsagePercent;

    private MemorySnapshot _lastMemorySnapshot;
    private ProcessSnapshot _lastTopProcessSnapshot;

    private NetworkSnapshot? _networkSnapshot;
    private double? _lastNetworkReceiveKbps;
    private double? _lastNetworkSendKbps;

    private DiskSnapshot? _diskSnapshot;
    private DiskMetricsSnapshot _lastDiskMetricsSnapshot;
    private SafeFileHandle? _systemVolumeHandle;
    private readonly string _systemVolumePath = GetSystemVolumePath();

    public Task<MetricSample> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        MemorySnapshot memory = ReadMemoryUsage();
        ProcessSnapshot topProcess = ReadTopProcess(cancellationToken);
        DiskMetricsSnapshot diskMetrics = ReadDiskMetrics();
        (double? networkReceiveKbps, double? networkSendKbps) = ReadNetworkThroughput();
        GameContextSnapshot gameContext = ReadGameContext(topProcess);

        MetricSample sample = new()
        {
            TimestampUtc = now,
            CpuUsagePercent = ReadCpuUsagePercent(),
            CpuFrequencyMHz = null,
            CpuQueueLength = null,
            MemoryUsedPercent = memory.MemoryUsedPercent,
            AvailableMemoryMb = memory.AvailableMemoryMb,
            CommitUsedPercent = memory.CommitUsedPercent,
            DiskActiveTimePercent = diskMetrics.ActiveTimePercent,
            DiskReadMbPerSec = diskMetrics.ReadMbPerSec,
            DiskWriteMbPerSec = diskMetrics.WriteMbPerSec,
            DiskQueueLength = diskMetrics.QueueLength,
            DiskAverageResponseMs = diskMetrics.AverageResponseMs,
            NetworkReceiveKbps = networkReceiveKbps,
            NetworkSendKbps = networkSendKbps,
            NetworkLatencyMs = null,
            NetworkRetransmitsPerSec = null,
            GpuUsagePercent = null,
            GpuMemoryUsedPercent = null,
            GpuDedicatedMemoryMb = null,
            GpuSharedMemoryMb = null,
            GpuTemperatureCelsius = null,
            TopProcessName = topProcess.Name,
            TopProcessMemoryMb = topProcess.MemoryMb,
            GameProcessName = gameContext.GameProcessName,
            GamingTelemetryActive = gameContext.GamingTelemetryActive,
            FramesPerSecond = null,
            FrameTimeMs = null,
            FrameTimeP95Ms = null,
            InputLatencyMs = null,
            RenderLatencyMs = null,
            DisplayRefreshRateHz = null,
            VariableRefreshRateEnabled = null,
            GameModeEnabled = gameContext.GameModeEnabled,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64)
        };

        return Task.FromResult(sample);
    }

    private double? ReadCpuUsagePercent()
    {
        try
        {
            if (!GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user))
            {
                return _lastCpuUsagePercent;
            }

            ulong idleValue = idle.ToUInt64();
            ulong kernelValue = kernel.ToUInt64();
            ulong userValue = user.ToUInt64();

            if (!_hasCpuBaseline)
            {
                _previousCpuIdleTime = idleValue;
                _previousCpuKernelTime = kernelValue;
                _previousCpuUserTime = userValue;
                _hasCpuBaseline = true;
                return _lastCpuUsagePercent;
            }

            if (idleValue < _previousCpuIdleTime ||
                kernelValue < _previousCpuKernelTime ||
                userValue < _previousCpuUserTime)
            {
                _previousCpuIdleTime = idleValue;
                _previousCpuKernelTime = kernelValue;
                _previousCpuUserTime = userValue;
                return _lastCpuUsagePercent;
            }

            ulong idleDelta = idleValue - _previousCpuIdleTime;
            ulong kernelDelta = kernelValue - _previousCpuKernelTime;
            ulong userDelta = userValue - _previousCpuUserTime;
            ulong totalDelta = kernelDelta + userDelta;

            _previousCpuIdleTime = idleValue;
            _previousCpuKernelTime = kernelValue;
            _previousCpuUserTime = userValue;

            if (totalDelta == 0)
            {
                return _lastCpuUsagePercent;
            }

            double cpuPercent = (1d - ((double)idleDelta / totalDelta)) * 100d;
            _lastCpuUsagePercent = Math.Round(Math.Clamp(cpuPercent, 0, 100), 1);
            return _lastCpuUsagePercent;
        }
        catch
        {
            return _lastCpuUsagePercent;
        }
    }

    private MemorySnapshot ReadMemoryUsage()
    {
        try
        {
            MemoryStatusEx memoryStatus = new()
            {
                Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
            };

            if (!GlobalMemoryStatusEx(ref memoryStatus) || memoryStatus.TotalPhys == 0)
            {
                return _lastMemorySnapshot;
            }

            ulong usedBytes = memoryStatus.TotalPhys - memoryStatus.AvailPhys;
            _lastMemorySnapshot = new MemorySnapshot(
                MemoryUsedPercent: Math.Round((double)usedBytes / memoryStatus.TotalPhys * 100d, 1),
                AvailableMemoryMb: (long)(memoryStatus.AvailPhys / 1024 / 1024),
                CommitUsedPercent: GetCommitUsedPercent(memoryStatus));

            return _lastMemorySnapshot;
        }
        catch
        {
            return _lastMemorySnapshot;
        }
    }

    private ProcessSnapshot ReadTopProcess(CancellationToken cancellationToken)
    {
        try
        {
            Process[] processes = Process.GetProcesses();
            string? topProcessName = null;
            double? topProcessMemoryMb = null;
            long highestWorkingSet = 0;

            foreach (Process process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    long workingSet = process.WorkingSet64;
                    if (workingSet <= highestWorkingSet)
                    {
                        continue;
                    }

                    string? processName = ResolveProcessDisplayName(process);
                    if (string.IsNullOrWhiteSpace(processName))
                    {
                        continue;
                    }

                    highestWorkingSet = workingSet;
                    topProcessName = processName;
                    topProcessMemoryMb = Math.Round(workingSet / 1024d / 1024d, 1);
                }
                catch
                {
                    // Skip protected or short-lived processes.
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (topProcessName is null)
            {
                return _lastTopProcessSnapshot;
            }

            _lastTopProcessSnapshot = new ProcessSnapshot(topProcessName, topProcessMemoryMb);
            return _lastTopProcessSnapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return _lastTopProcessSnapshot;
        }
    }

    private (double? ReceiveKbps, double? SendKbps) ReadNetworkThroughput()
    {
        try
        {
            long bytesReceived = 0;
            long bytesSent = 0;

            foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!ShouldIncludeNetworkInterface(networkInterface))
                {
                    continue;
                }

                try
                {
                    IPv4InterfaceStatistics statistics = networkInterface.GetIPv4Statistics();
                    bytesReceived += statistics.BytesReceived;
                    bytesSent += statistics.BytesSent;
                }
                catch
                {
                    // Ignore transient adapter-state failures and continue with other interfaces.
                }
            }

            long timestamp = Stopwatch.GetTimestamp();

            if (_networkSnapshot is null)
            {
                _networkSnapshot = new NetworkSnapshot(timestamp, bytesReceived, bytesSent);
                return (_lastNetworkReceiveKbps, _lastNetworkSendKbps);
            }

            long elapsedTicks = timestamp - _networkSnapshot.Value.TimestampTicks;
            long receivedDelta = bytesReceived - _networkSnapshot.Value.BytesReceived;
            long sentDelta = bytesSent - _networkSnapshot.Value.BytesSent;

            _networkSnapshot = new NetworkSnapshot(timestamp, bytesReceived, bytesSent);

            if (elapsedTicks <= 0 || receivedDelta < 0 || sentDelta < 0)
            {
                return (_lastNetworkReceiveKbps, _lastNetworkSendKbps);
            }

            double elapsedSeconds = (double)elapsedTicks / Stopwatch.Frequency;
            if (elapsedSeconds <= 0)
            {
                return (_lastNetworkReceiveKbps, _lastNetworkSendKbps);
            }

            _lastNetworkReceiveKbps = Math.Round(((double)receivedDelta * 8d / 1024d) / elapsedSeconds, 1);
            _lastNetworkSendKbps = Math.Round(((double)sentDelta * 8d / 1024d) / elapsedSeconds, 1);

            return (_lastNetworkReceiveKbps, _lastNetworkSendKbps);
        }
        catch
        {
            return (_lastNetworkReceiveKbps, _lastNetworkSendKbps);
        }
    }

    private DiskMetricsSnapshot ReadDiskMetrics()
    {
        try
        {
            SafeFileHandle? volumeHandle = GetSystemVolumeHandle();
            if (volumeHandle is null)
            {
                return _lastDiskMetricsSnapshot;
            }

            if (!DeviceIoControl(
                    volumeHandle,
                    IoctlDiskPerformance,
                    IntPtr.Zero,
                    0,
                    out DiskPerformance diskPerformance,
                    Marshal.SizeOf<DiskPerformance>(),
                    out _,
                    IntPtr.Zero))
            {
                volumeHandle.Dispose();
                _systemVolumeHandle = null;
                return _lastDiskMetricsSnapshot;
            }

            if (_diskSnapshot is null)
            {
                _diskSnapshot = new DiskSnapshot(
                    BytesRead: diskPerformance.BytesRead,
                    BytesWritten: diskPerformance.BytesWritten,
                    ReadTime: diskPerformance.ReadTime,
                    WriteTime: diskPerformance.WriteTime,
                    IdleTime: diskPerformance.IdleTime,
                    ReadCount: diskPerformance.ReadCount,
                    WriteCount: diskPerformance.WriteCount,
                    QueryTime: diskPerformance.QueryTime);

                _lastDiskMetricsSnapshot = _lastDiskMetricsSnapshot with
                {
                    QueueLength = Math.Round((double)diskPerformance.QueueDepth, 1)
                };

                return _lastDiskMetricsSnapshot;
            }

            long idleDelta = diskPerformance.IdleTime - _diskSnapshot.Value.IdleTime;
            long queryDelta = diskPerformance.QueryTime - _diskSnapshot.Value.QueryTime;
            long bytesReadDelta = diskPerformance.BytesRead - _diskSnapshot.Value.BytesRead;
            long bytesWrittenDelta = diskPerformance.BytesWritten - _diskSnapshot.Value.BytesWritten;
            long readTimeDelta = diskPerformance.ReadTime - _diskSnapshot.Value.ReadTime;
            long writeTimeDelta = diskPerformance.WriteTime - _diskSnapshot.Value.WriteTime;
            long readCountDelta = diskPerformance.ReadCount - _diskSnapshot.Value.ReadCount;
            long writeCountDelta = diskPerformance.WriteCount - _diskSnapshot.Value.WriteCount;

            _diskSnapshot = new DiskSnapshot(
                BytesRead: diskPerformance.BytesRead,
                BytesWritten: diskPerformance.BytesWritten,
                ReadTime: diskPerformance.ReadTime,
                WriteTime: diskPerformance.WriteTime,
                IdleTime: diskPerformance.IdleTime,
                ReadCount: diskPerformance.ReadCount,
                WriteCount: diskPerformance.WriteCount,
                QueryTime: diskPerformance.QueryTime);

            if (idleDelta < 0 ||
                queryDelta <= 0 ||
                bytesReadDelta < 0 ||
                bytesWrittenDelta < 0 ||
                readTimeDelta < 0 ||
                writeTimeDelta < 0 ||
                readCountDelta < 0 ||
                writeCountDelta < 0)
            {
                return _lastDiskMetricsSnapshot;
            }

            double elapsedSeconds = queryDelta / (double)TimeSpan.TicksPerSecond;
            if (elapsedSeconds <= 0)
            {
                return _lastDiskMetricsSnapshot;
            }

            double activePercent = (1d - ((double)idleDelta / queryDelta)) * 100d;
            long totalOperations = readCountDelta + writeCountDelta;
            double? averageResponseMs = totalOperations > 0
                ? Math.Round(((readTimeDelta + writeTimeDelta) / 10000d) / totalOperations, 2)
                : _lastDiskMetricsSnapshot.AverageResponseMs;

            _lastDiskMetricsSnapshot = new DiskMetricsSnapshot(
                ActiveTimePercent: Math.Round(Math.Clamp(activePercent, 0, 100), 1),
                ReadMbPerSec: Math.Round((bytesReadDelta / 1024d / 1024d) / elapsedSeconds, 2),
                WriteMbPerSec: Math.Round((bytesWrittenDelta / 1024d / 1024d) / elapsedSeconds, 2),
                QueueLength: Math.Round((double)diskPerformance.QueueDepth, 1),
                AverageResponseMs: averageResponseMs);

            return _lastDiskMetricsSnapshot;
        }
        catch
        {
            return _lastDiskMetricsSnapshot;
        }
    }

    private SafeFileHandle? GetSystemVolumeHandle()
    {
        if (string.IsNullOrWhiteSpace(_systemVolumePath))
        {
            return null;
        }

        if (_systemVolumeHandle is not null && !_systemVolumeHandle.IsInvalid && !_systemVolumeHandle.IsClosed)
        {
            return _systemVolumeHandle;
        }

        _systemVolumeHandle?.Dispose();
        _systemVolumeHandle = CreateFile(
            _systemVolumePath,
            0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            0,
            IntPtr.Zero);

        if (_systemVolumeHandle.IsInvalid)
        {
            _systemVolumeHandle.Dispose();
            _systemVolumeHandle = null;
        }

        return _systemVolumeHandle;
    }

    private static bool ShouldIncludeNetworkInterface(NetworkInterface networkInterface)
    {
        return networkInterface.OperationalStatus == OperationalStatus.Up &&
               networkInterface.NetworkInterfaceType is not NetworkInterfaceType.Loopback &&
               networkInterface.NetworkInterfaceType is not NetworkInterfaceType.Tunnel;
    }

    private static string? ResolveProcessDisplayName(Process process)
    {
        string? processName = TryGetProcessName(process);
        string? windowTitle = TryGetMainWindowTitle(process);

        if (string.IsNullOrWhiteSpace(processName))
        {
            return NormalizeWindowTitle(windowTitle);
        }

        processName = NormalizeProcessName(processName);
        string? normalizedTitle = NormalizeWindowTitle(windowTitle);

        if (normalizedTitle is not null && PrefersWindowTitle(processName))
        {
            return normalizedTitle;
        }

        return processName;
    }

    private static string? TryGetProcessName(Process process)
    {
        try
        {
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetMainWindowTitle(Process process)
    {
        try
        {
            return process.MainWindowTitle;
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeProcessName(string processName)
    {
        string normalized = Path.GetFileNameWithoutExtension(processName).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? processName.Trim() : normalized;
    }

    private static string? NormalizeWindowTitle(string? windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle))
        {
            return null;
        }

        string normalizedTitle = windowTitle.Trim();
        foreach (string suffix in KnownLauncherSuffixes)
        {
            if (normalizedTitle.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                normalizedTitle = normalizedTitle[..^suffix.Length].TrimEnd();
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(normalizedTitle) || IgnoredWindowTitles.Contains(normalizedTitle))
        {
            return null;
        }

        return normalizedTitle;
    }

    private static bool PrefersWindowTitle(string processName) => TitlePreferredProcesses.Contains(processName);

    private GameContextSnapshot ReadGameContext(ProcessSnapshot topProcess)
    {
        string? foregroundApp = _foregroundWindowTracker.GetForegroundProcessName();
        bool gamingTelemetryActive = IsLikelyGameProcess(foregroundApp, topProcess);

        return new GameContextSnapshot(
            GameProcessName: gamingTelemetryActive ? foregroundApp : null,
            GamingTelemetryActive: gamingTelemetryActive,
            GameModeEnabled: ReadGameModeEnabled());
    }

    private static bool IsLikelyGameProcess(string? foregroundApp, ProcessSnapshot topProcess)
    {
        if (string.IsNullOrWhiteSpace(foregroundApp))
        {
            return false;
        }

        if (KnownNonGamingApps.Contains(foregroundApp))
        {
            return false;
        }

        if (topProcess.MemoryMb is >= 1024)
        {
            return true;
        }

        return topProcess.MemoryMb is >= 512 &&
               string.Equals(foregroundApp, topProcess.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool? ReadGameModeEnabled()
    {
        try
        {
            object? configuredValue =
                Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar", "AutoGameModeEnabled", null) ??
                Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\GameBar", "AllowAutoGameMode", null);

            return configuredValue switch
            {
                int intValue => intValue != 0,
                long longValue => longValue != 0,
                string stringValue when int.TryParse(stringValue, out int parsedValue) => parsedValue != 0,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    private static double? GetCommitUsedPercent(MemoryStatusEx memoryStatus)
    {
        if (memoryStatus.TotalPageFile == 0)
        {
            return null;
        }

        ulong usedCommit = memoryStatus.TotalPageFile - memoryStatus.AvailPageFile;
        return Math.Round((double)usedCommit / memoryStatus.TotalPageFile * 100d, 1);
    }

    private static string GetSystemVolumePath()
    {
        string? systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
        if (string.IsNullOrWhiteSpace(systemDrive) || systemDrive.Length < 2)
        {
            return string.Empty;
        }

        return $@"\\.\\{char.ToUpperInvariant(systemDrive[0])}:";
    }

    private static readonly HashSet<string> TitlePreferredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost",
        "Battle.net",
        "EADesktop",
        "EpicGamesLauncher",
        "explorer",
        "GameBar",
        "origin",
        "RiotClientServices",
        "RiotClientUx",
        "RiotClientUxRender",
        "steam",
        "steamwebhelper",
        "UbisoftConnect",
        "XboxPcApp"
    };

    private static readonly HashSet<string> IgnoredWindowTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Battle.net",
        "Epic Games Launcher",
        "Program Manager",
        "Riot Client",
        "Steam",
        "Ubisoft Connect",
        "Xbox"
    };

    private static readonly string[] KnownLauncherSuffixes =
    [
        " - Battle.net",
        " - Blizzard Battle.net",
        " - EA app",
        " - Epic Games Launcher",
        " - Riot Client",
        " - Steam",
        " - Ubisoft Connect",
        " | Steam"
    ];

    private static readonly HashSet<string> KnownNonGamingApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome",
        "Code",
        "cmd",
        "devenv",
        "Discord",
        "explorer",
        "firefox",
        "msedge",
        "notepad",
        "obs64",
        "powershell",
        "pwsh",
        "slack",
        "Spotify",
        "Teams",
        "WindowsTerminal"
    };

    private const uint IoctlDiskPerformance = 0x00070020;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime lpIdleTime, out FileTime lpKernelTime, out FileTime lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        out DiskPerformance lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DiskPerformance
    {
        public long BytesRead;
        public long BytesWritten;
        public long ReadTime;
        public long WriteTime;
        public long IdleTime;
        public uint ReadCount;
        public uint WriteCount;
        public uint QueueDepth;
        public uint SplitCount;
        public long QueryTime;
        public uint StorageDeviceNumber;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)]
        public string StorageManagerName;
    }

    private readonly record struct MemorySnapshot(double MemoryUsedPercent, long AvailableMemoryMb, double? CommitUsedPercent);

    private readonly record struct ProcessSnapshot(string? Name, double? MemoryMb);

    private readonly record struct NetworkSnapshot(long TimestampTicks, long BytesReceived, long BytesSent);

    private readonly record struct DiskSnapshot(
        long BytesRead,
        long BytesWritten,
        long ReadTime,
        long WriteTime,
        long IdleTime,
        long ReadCount,
        long WriteCount,
        long QueryTime);

    private readonly record struct DiskMetricsSnapshot(
        double? ActiveTimePercent,
        double? ReadMbPerSec,
        double? WriteMbPerSec,
        double? QueueLength,
        double? AverageResponseMs);

    private readonly record struct GameContextSnapshot(string? GameProcessName, bool GamingTelemetryActive, bool? GameModeEnabled);
}
