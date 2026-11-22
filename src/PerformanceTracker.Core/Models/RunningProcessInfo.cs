namespace PerformanceTracker.Core.Models;

/// <summary>
/// A point-in-time reading of one OS process, as captured by the process inventory.
/// </summary>
public sealed record RunningProcessInfo
{
    public required int Id { get; init; }

    /// <summary>Executable name without extension, e.g. "chrome".</summary>
    public required string Name { get; init; }

    /// <summary>Private working set in bytes (full working set for VM hosts; see ProcessMemory.Effective).</summary>
    public long MemoryBytes { get; init; }

    public long WorkingSetBytes { get; init; }

    public long PrivateCommitBytes { get; init; }

    /// <summary>Total CPU time consumed since the process started, when readable.</summary>
    public TimeSpan? TotalCpuTime { get; init; }

    public DateTimeOffset? StartTimeUtc { get; init; }

    /// <summary>Windows session id. Session 0 hosts services rather than desktop apps.</summary>
    public bool IsProtected { get; init; }

    public int SessionId { get; init; }

    /// <summary>Title of a visible top-level window owned by the process, if any.</summary>
    public string? WindowTitle { get; init; }

    /// <summary>File description from the executable's version info, e.g. "Google Chrome".</summary>
    public string? FileDescription { get; init; }

    public string? ExecutablePath { get; init; }

    public string? CommandLine { get; init; }

    public int ParentId { get; init; }

    public string? WorkingDirectory { get; init; }

    public string? ParentDescription { get; init; }

    public IReadOnlyList<string> ListeningPorts { get; init; } = [];

    public bool HasWindow => !string.IsNullOrWhiteSpace(WindowTitle);
}
