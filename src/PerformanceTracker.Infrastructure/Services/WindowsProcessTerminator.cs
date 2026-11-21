using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PerformanceTracker.Core.Models;
using PerformanceTracker.Core.Interfaces;
using PerformanceTracker.Core.Services;

namespace PerformanceTracker.Infrastructure.Services;

public sealed class WindowsProcessTerminator : IProcessTerminator
{
    private static readonly TimeSpan GracefulWait = TimeSpan.FromSeconds(5);

    public Task<EndProcessResult> EndAsync(IReadOnlyList<RunningProcessInfo> targets, bool force, CancellationToken cancellationToken) =>
        Task.Run(() => EndCoreAsync(targets, force, cancellationToken), cancellationToken);

    private static async Task<EndProcessResult> EndCoreAsync(IReadOnlyList<RunningProcessInfo> snapshots, bool force, CancellationToken cancellationToken)
    {
        List<Process> targets = [];
        Dictionary<int, long> privateMemory = [];
        int blocked = 0;
        foreach (RunningProcessInfo target in snapshots)
        {
            Process? process = null;
            try
            {
                process = Process.GetProcessById(target.Id);
                if (target.Id == Environment.ProcessId || target.IsProtected ||
                    ProcessCatalog.IsProtected(target.Name) || ProcessCatalog.IsProtected(process.ProcessName) ||
                    target.StartTimeUtc is not DateTimeOffset start ||
                    !GetProcessTimes(process.SafeHandle, out long creation, out _, out _, out _) ||
                    creation != start.ToFileTime() ||
                    !IsProcessCritical(process.SafeHandle, out bool critical) || critical)
                {
                    blocked++;
                    continue;
                }

                privateMemory[process.Id] = target.MemoryBytes;
                targets.Add(process);
                process = null;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // Already exited.
            }
            catch (Win32Exception)
            {
                blocked++;
            }
            finally
            {
                process?.Dispose();
            }
        }

        try
        {
            long freed = 0;
            int ended = 0;
            int denied = blocked;
            List<Process> waitingOnClose = [];

            bool closeWindows = !force && targets.Any(HasWindow);
            foreach (Process process in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long memory = privateMemory[process.Id];
                if (closeWindows)
                {
                    TryCloseWindow(process);
                    waitingOnClose.Add(process);
                    continue;
                }

                switch (Kill(process))
                {
                    case KillOutcome.Ended:
                        ended++;
                        freed += memory;
                        break;
                    case KillOutcome.Pending:
                        waitingOnClose.Add(process);
                        break;
                    case KillOutcome.Denied:
                        denied++;
                        break;
                }
            }

            int stillRunning = 0;
            if (waitingOnClose.Count > 0)
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(GracefulWait);
                foreach (Process process in waitingOnClose)
                {
                    long memory = privateMemory[process.Id];
                    try
                    {
                        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                        ended++;
                        freed += memory;
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        if (process.HasExited)
                        {
                            ended++;
                            freed += memory;
                        }
                        else
                        {
                            stillRunning++;
                        }
                    }
                }
            }

            return new EndProcessResult(ended, stillRunning, denied, freed);
        }
        finally
        {
            foreach (Process process in targets)
            {
                process.Dispose();
            }
        }
    }

    private enum KillOutcome { Ended, Denied, Gone, Pending }

    private static KillOutcome Kill(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return KillOutcome.Gone;
            }

            if (!IsProcessCritical(process.SafeHandle, out bool critical) || critical ||
                !TerminateProcess(process.SafeHandle, 1))
            {
                return process.HasExited ? KillOutcome.Gone : KillOutcome.Denied;
            }

            return KillOutcome.Pending;
        }
        catch (Win32Exception)
        {
            return KillOutcome.Denied;
        }
        catch (InvalidOperationException)
        {
            return KillOutcome.Gone;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessCritical(SafeProcessHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    private static void TryCloseWindow(Process process)
    {
        try
        {
            if (!process.HasExited && IsProcessCritical(process.SafeHandle, out bool critical) && !critical)
            {
                _ = process.CloseMainWindow();
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // The target may have exited as the request was being sent.
        }
    }

    private static bool HasWindow(Process process)
    {
        try
        {
            return process.MainWindowHandle != nint.Zero;
        }
        catch
        {
            return false;
        }
    }
}
