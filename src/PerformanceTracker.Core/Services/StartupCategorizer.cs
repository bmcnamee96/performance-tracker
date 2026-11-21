namespace PerformanceTracker.Core.Services;

/// <summary>Sections of the Startup Apps tab, in display order.</summary>
public enum StartupCategory
{
    /// <summary>Apps the user installed and can usually switch off without trouble.</summary>
    YourApps,

    /// <summary>Windows components, drivers, security and hardware/sync tools that are risky to switch off.</summary>
    WindowsAndDrivers,

    /// <summary>Background services and scheduled logon tasks (read-only here, hidden by default).</summary>
    ServicesAndTasks
}

public static class StartupCategorizer
{
    public static StartupCategory Categorize(string source, string? location, string? publisher, bool recommendedToKeep)
    {
        if (source.Contains("service", StringComparison.OrdinalIgnoreCase) ||
            source.Contains("task", StringComparison.OrdinalIgnoreCase))
        {
            return StartupCategory.ServicesAndTasks;
        }

        if (recommendedToKeep || IsWindowsComponent(location, publisher))
        {
            return StartupCategory.WindowsAndDrivers;
        }

        return StartupCategory.YourApps;
    }

    public static string Title(StartupCategory category) => category switch
    {
        StartupCategory.YourApps => "Your apps",
        StartupCategory.WindowsAndDrivers => "Windows & drivers",
        _ => "Background services & scheduled tasks"
    };

    public static string Description(StartupCategory category) => category switch
    {
        StartupCategory.YourApps => "Apps you installed. Usually safe to switch off - you can still open them yourself.",
        StartupCategory.WindowsAndDrivers => "Parts of Windows, security, drivers and device or sync tools. Best left on.",
        _ => "Run quietly in the background. Shown for reference; change them in Services or Task Scheduler."
    };

    private static bool IsWindowsComponent(string? location, string? publisher)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        bool inWindowsFolder = !string.IsNullOrEmpty(location) && !string.IsNullOrEmpty(windows) &&
            location.StartsWith(windows, StringComparison.OrdinalIgnoreCase);
        bool microsoftWindows = publisher is not null &&
            publisher.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(location) &&
            location.Contains(@"\Windows", StringComparison.OrdinalIgnoreCase);
        return inWindowsFolder || microsoftWindows;
    }
}
