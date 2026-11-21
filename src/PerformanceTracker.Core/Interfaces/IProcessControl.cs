using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Core.Interfaces;

public interface IProcessInventory
{
    /// <summary>
    /// Captures every running process. When <paramref name="includeDetails"/> is false the
    /// capture skips slower lookups (file descriptions, command lines) to stay cheap.
    /// </summary>
    IReadOnlyList<RunningProcessInfo> Capture(bool includeDetails);
}

public interface IProcessTerminator
{
    /// <summary>
    /// Ends the given processes. Without <paramref name="force"/>, processes with a window are
    /// asked to close first so they can save; background processes are always ended directly.
    /// </summary>
    Task<EndProcessResult> EndAsync(IReadOnlyList<RunningProcessInfo> targets, bool force, CancellationToken cancellationToken);
}

/// <param name="Ended">Processes that are no longer running.</param>
/// <param name="StillRunning">Processes asked to close that are still open (often a save prompt).</param>
/// <param name="AccessDenied">Processes Windows would not let this app end without admin rights.</param>
/// <param name="FreedBytes">Approximate memory those processes were using.</param>
public sealed record EndProcessResult(int Ended, int StillRunning, int AccessDenied, long FreedBytes);
