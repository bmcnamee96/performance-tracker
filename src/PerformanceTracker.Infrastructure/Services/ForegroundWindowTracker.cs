using System.Diagnostics;
using System.Runtime.InteropServices;
using PerformanceTracker.Core.Interfaces;

namespace PerformanceTracker.Infrastructure.Services;

public sealed class ForegroundWindowTracker : IForegroundAppTracker
{
    public string? GetForegroundProcessName()
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

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
}

