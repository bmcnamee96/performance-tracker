using Microsoft.Win32;
using PerformanceTracker.Core.Services;

namespace PerformanceTracker.Desktop.Services;

/// <summary>Registers this app to start (minimized to the tray) when the user signs in to Windows.</summary>
public static class LaunchAtSignIn
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "PerformanceTracker";

    public static bool IsEnabled
    {
        get
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string)
            {
                return false;
            }

            using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return StartupApproval.Read(approved?.GetValue(ValueName) as byte[]) != false;
        }
    }

    public static void Set(bool enabled)
    {
        using RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (!enabled)
        {
            run.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        string path = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find the app's location.");
        run.SetValue(ValueName, $"\"{path}\" --minimized");

        // Clear an earlier "disabled" flag from Task Manager or Settings so the entry actually runs.
        using RegistryKey approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
        byte[]? previous = approved.GetValue(ValueName) as byte[];
        if (StartupApproval.Read(previous) == false)
        {
            approved.SetValue(ValueName, StartupApproval.Write(true, DateTimeOffset.UtcNow, previous), RegistryValueKind.Binary);
        }
    }
}
