using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PerformanceTracker.Core.Interfaces;

namespace PerformanceTracker.Infrastructure.Services;

public sealed class ForegroundWindowTracker : IForegroundAppTracker
{
    public string? GetForegroundProcessName()
    {
        try
        {
            nint hwnd = GetForegroundWindow();
            if (hwnd == nint.Zero)
            {
                return null;
            }

            _ = GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == 0)
            {
                return null;
            }

            string? processName = TryGetProcessName(processId);
            string? windowTitle = TryGetWindowTitle(hwnd);

            if (string.IsNullOrWhiteSpace(processName))
            {
                return NormalizeWindowTitle(windowTitle);
            }

            processName = NormalizeProcessName(processName);
            string? normalizedTitle = NormalizeWindowTitle(windowTitle);

            if (normalizedTitle is not null && TitlePreferredProcesses.Contains(processName))
            {
                return normalizedTitle;
            }

            return processName;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetProcessName(uint processId)
    {
        using SafeFileHandle processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (!processHandle.IsInvalid)
        {
            char[] imagePath = new char[1024];
            uint capacity = (uint)imagePath.Length;
            if (QueryFullProcessImageName(processHandle, 0, imagePath, ref capacity))
            {
                string processName = Path.GetFileNameWithoutExtension(new string(imagePath, 0, (int)capacity)).Trim();
                if (!string.IsNullOrWhiteSpace(processName))
                {
                    return processName;
                }
            }
        }

        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetWindowTitle(nint hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return null;
        }

        char[] titleBuffer = new char[length + 1];
        int copiedCharacters = GetWindowText(hwnd, titleBuffer, titleBuffer.Length);
        if (copiedCharacters <= 0)
        {
            return null;
        }

        return new string(titleBuffer, 0, copiedCharacters);
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

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(nint hWnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(nint hWnd);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(
        SafeFileHandle hProcess,
        uint dwFlags,
        char[] lpExeName,
        ref uint lpdwSize);
}
