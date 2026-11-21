namespace PerformanceTracker.Core.Models;

/// <summary>How safe it is to end every process in a group.</summary>
public enum AppSafety
{
    /// <summary>An ordinary app or tool; ending it only affects that app.</summary>
    Normal,

    /// <summary>Ending it works, but has side effects worth spelling out (lost tabs, a VM losing power, ...).</summary>
    Caution,

    /// <summary>A background service. Ending it usually needs admin rights and it may restart by itself.</summary>
    Service,

    /// <summary>Part of Windows itself. The app refuses to end these.</summary>
    Protected
}

/// <summary>What a group has been doing recently.</summary>
public enum AppActivity
{
    /// <summary>Not observed long enough to judge.</summary>
    Unknown,

    /// <summary>Using CPU right now.</summary>
    Busy,

    /// <summary>Has used no meaningful CPU for a while.</summary>
    Quiet
}

/// <summary>
/// All running processes that belong to one app (for example every chrome.exe), summarised in plain language.
/// </summary>
public sealed record AppGroup
{
    /// <summary>Stable grouping key (lower-case executable name).</summary>
    public required string Key { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>One sentence describing what this app is.</summary>
    public required string Explanation { get; init; }

    /// <summary>What happens if you end it, when that is not obvious.</summary>
    public string? Caution { get; init; }

    /// <summary>Extra detail such as a script name or window title.</summary>
    public string? Detail { get; init; }

    public required AppSafety Safety { get; init; }

    public required AppActivity Activity { get; init; }

    public required IReadOnlyList<int> ProcessIds { get; init; }

    public IReadOnlyList<AppGroup> Processes { get; init; } = [];

    public IReadOnlyList<RunningProcessInfo> Targets { get; init; } = [];

    public string? ExecutablePath { get; init; }

    public long MemoryBytes { get; init; }

    /// <summary>Share of total machine CPU, 0-100.</summary>
    public double CpuPercent { get; init; }

    /// <summary>How long the group has been quiet, when known.</summary>
    public TimeSpan? QuietFor { get; init; }

    public bool HasWindow { get; init; }

    /// <summary>True when the app looks unused and is worth ending to free memory.</summary>
    public bool IsSuggested { get; init; }

    public bool CanEnd => Safety != AppSafety.Protected;
}
