using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PerformanceTracker.Core.Interfaces;
using PerformanceTracker.Core.Models;
using PerformanceTracker.Core.Services;

namespace PerformanceTracker.Infrastructure.Services;

/// <summary>
/// Reads the running process list using cheap Win32 calls. Slow per-process details
/// (file description, command line) are cached for the lifetime of each process.
/// </summary>
public sealed class WindowsProcessInventory : IProcessInventory
{
    private readonly object _cacheLock = new();
    private readonly Dictionary<(int, DateTimeOffset?), Details> _detailsCache = [];
    private readonly Dictionary<string, string?> _descriptionByPath = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<RunningProcessInfo> Capture(bool includeDetails)
    {
        Dictionary<int, string> windowTitles = includeDetails ? ReadVisibleWindowTitles() : [];
        DateTimeOffset portCutoff = DateTimeOffset.UtcNow;
        var ports = includeDetails ? ProcessDetailsReader.ReadListeningPorts() : [];
        IReadOnlyList<RunningProcessInfo> processes = WindowsProcessSnapshot.Read();
        var byId = includeDetails ? processes.ToDictionary(process => process.Id) : [];
        List<RunningProcessInfo> result = new(processes.Count);
        HashSet<(int, DateTimeOffset?)> alive = [];

        foreach (RunningProcessInfo process in processes)
        {
            var key = (process.Id, process.StartTimeUtc);
            alive.Add(key);
            // Developer tools (node, python, ...) always get details so slowdowns can name the project involved.
            Details? details = includeDetails || DevProcessRecognizer.IsHost(process.Name)
                ? GetDetails(key, process.Id, process.Name)
                : TryGetCachedDetails(key);
            result.Add(process with
            {
                WindowTitle = windowTitles.GetValueOrDefault(process.Id),
                ExecutablePath = details?.Path,
                FileDescription = details?.Description,
                CommandLine = details?.CommandLine,
                WorkingDirectory = details?.WorkingDirectory,
                ParentDescription = includeDetails && DevProcessRecognizer.IsHost(process.Name)
                    ? DevProcessRecognizer.DescribeParent(process, byId) : null,
                ListeningPorts = process.StartTimeUtc < portCutoff ? ports.GetValueOrDefault(process.Id) ?? [] : [],
                IsProtected = details?.IsProtected ?? false
            });
        }

        lock (_cacheLock)
        {
            foreach ((int, DateTimeOffset?) stale in _detailsCache.Keys.Where(k => !alive.Contains(k)).ToArray())
            {
                _detailsCache.Remove(stale);
            }
        }

        return result;
    }

    private Details? TryGetCachedDetails((int, DateTimeOffset?) key)
    {
        lock (_cacheLock)
        {
            return _detailsCache.GetValueOrDefault(key);
        }
    }

    private Details GetDetails((int, DateTimeOffset?) key, int id, string name)
    {
        lock (_cacheLock)
        {
            if (_detailsCache.TryGetValue(key, out Details? cached))
            {
                return cached;
            }
        }

        string? path = null;
        string? commandLine = null;
        bool isProtected = true;
        using (SafeFileHandle handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)id))
        {
            if (!handle.IsInvalid && key.Item2 is DateTimeOffset expected &&
                GetProcessTimes(handle, out long creation, out _, out _, out _) && creation == expected.ToFileTime())
            {
                path = TryGetImagePath(handle);
                isProtected = !IsProcessCritical(handle, out bool critical) || critical;
                if (DevProcessRecognizer.IsHost(name) || AppGroupBuilder.IsChromium(name.ToLowerInvariant()))
                {
                    commandLine = TryGetCommandLine(handle);
                }
            }
        }

        string? description = path is null ? null : GetDescription(path);
        string? directory = DevProcessRecognizer.IsHost(name) && key.Item2 is DateTimeOffset start
            ? ProcessDetailsReader.ReadWorkingDirectory(id, start) : null;
        var details = new Details(path, description, commandLine, directory, isProtected);

        lock (_cacheLock)
        {
            _detailsCache[key] = details;
        }

        return details;
    }

    private string? GetDescription(string path)
    {
        lock (_cacheLock)
        {
            if (_descriptionByPath.TryGetValue(path, out string? cached))
            {
                return cached;
            }
        }

        string? description = TryRead(() =>
        {
            string? text = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        });

        lock (_cacheLock)
        {
            _descriptionByPath[path] = description;
        }

        return description;
    }

    private static string? TryGetImagePath(SafeFileHandle handle)
    {
        char[] buffer = new char[1024];
        uint capacity = (uint)buffer.Length;
        return QueryFullProcessImageName(handle, 0, buffer, ref capacity) ? new string(buffer, 0, (int)capacity) : null;
    }

    /// <summary>Reads a process command line via NtQueryInformationProcess (ProcessCommandLineInformation).</summary>
    private static string? TryGetCommandLine(SafeFileHandle handle)
    {
        const int ProcessCommandLineInformation = 60;
        const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

        int status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, nint.Zero, 0, out int needed);
        if (status != StatusInfoLengthMismatch || needed <= 0 || needed > 64 * 1024)
        {
            return null;
        }

        nint buffer = Marshal.AllocHGlobal(needed);
        try
        {
            status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, needed, out _);
            if (status != 0)
            {
                return null;
            }

            // UNICODE_STRING { ushort Length; ushort MaximumLength; [padding]; PWSTR Buffer; }
            int lengthBytes = (ushort)Marshal.ReadInt16(buffer);
            nint text = Marshal.ReadIntPtr(buffer, nint.Size);
            return lengthBytes == 0 || text == nint.Zero ? null : Marshal.PtrToStringUni(text, lengthBytes / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Maps process id to the title of its first visible, unowned, uncloaked top-level window.</summary>
    private static Dictionary<int, string> ReadVisibleWindowTitles()
    {
        Dictionary<int, string> titles = [];
        EnumWindows((hwnd, lParam) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != nint.Zero || IsCloaked(hwnd))
            {
                return true;
            }

            int length = GetWindowTextLength(hwnd);
            if (length <= 0)
            {
                return true;
            }

            char[] buffer = new char[length + 1];
            int copied = GetWindowText(hwnd, buffer, buffer.Length);
            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (copied > 0 && pid != 0 && !titles.ContainsKey((int)pid))
            {
                string title = new string(buffer, 0, copied).Trim();
                if (title.Length > 0 && title != "Program Manager")
                {
                    titles[(int)pid] = title;
                }
            }

            return true;
        }, nint.Zero);

        return titles;
    }

    private static bool IsCloaked(nint hwnd)
    {
        const int DwmwaCloaked = 14;
        return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    private static T TryRead<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return default!;
        }
    }

    private sealed record Details(string? Path, string? Description, string? CommandLine, string? WorkingDirectory, bool IsProtected);

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint GwOwner = 4;

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(nint hwnd, char[] text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(nint hwnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeFileHandle process, uint flags, char[] exeName, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessCritical(SafeFileHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeFileHandle process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeFileHandle process, int infoClass, nint info, int infoLength, out int returnLength);
}
