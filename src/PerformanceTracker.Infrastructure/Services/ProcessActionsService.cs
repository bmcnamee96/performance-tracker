using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PerformanceTracker.Core.Models;
using PerformanceTracker.Core.Services;

namespace PerformanceTracker.Infrastructure.Services;

public enum ProcessAction
{
    Pause, Resume, Trim, LowerPriority, RestorePriority
}

public sealed record ProcessActionState(int Id, DateTimeOffset Start, bool Paused, uint? OriginalPriority);

public sealed record ProcessActionResult(bool Success, long BeforeBytes = 0, long AfterBytes = 0);

public sealed class ProcessActionsService(string statePath)
{
    public static bool IsEligible(RunningProcessInfo target, int selfId) =>
        target.Id > 0 && target.Id != selfId && target.StartTimeUtc.HasValue &&
        !target.IsProtected && !ProcessCatalog.IsProtected(target.Name);

    public IReadOnlyList<ProcessActionState> ReadState()
    {
        try
        {
            using var stream = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<List<ProcessActionState>>(stream) ?? [];
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
    }

    public ProcessActionResult Apply(RunningProcessInfo target, ProcessAction action)
    {
        if (!IsEligible(target, Environment.ProcessId) || target.StartTimeUtc is not DateTimeOffset start)
        {
            return new(false);
        }

        uint permission = action switch
        {
            ProcessAction.Pause or ProcessAction.Resume => 0x0800,
            ProcessAction.Trim => 0x0100 | 0x0400,
            _ => 0x0200
        };

        using SafeFileHandle process = OpenProcess(0x1000 | permission, false, (uint)target.Id);
        char[] image = new char[1024];
        uint length = (uint)image.Length;
        if (process.IsInvalid || !QueryFullProcessImageName(process, 0, image, ref length) ||
            ProcessCatalog.IsProtected(Path.GetFileNameWithoutExtension(new string(image, 0, (int)length))) ||
            !GetProcessTimes(process, out long created, out long exited, out _, out _) ||
            created != start.ToFileTime() || exited != 0 ||
            !IsProcessCritical(process, out bool critical) || critical)
        {
            return new(false);
        }

        if (action == ProcessAction.Trim)
        {
            return Trim(process);
        }

        using var mutex = new Mutex(false, @"Local\PerformanceTracker.ProcessActions");
        try
        {
            try
            {
                _ = mutex.WaitOne();
            }
            catch (AbandonedMutexException)
            {
                // The previous owner exited. The state file was replaced atomically.
            }

            return ChangeTrackedState(process, target.Id, start, action);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private ProcessActionResult ChangeTrackedState(SafeFileHandle process, int id, DateTimeOffset start, ProcessAction action)
    {
        List<ProcessActionState> state = [.. ReadState()];
        ProcessActionState current = state.Find(value => value.Id == id && value.Start == start) ?? new(id, start, false, null);
        var updated = current;
        switch (action)
        {
            case ProcessAction.Pause when !current.Paused:
                updated = current with { Paused = true };
                break;
            case ProcessAction.Resume when current.Paused:
                updated = current with { Paused = false };
                break;
            case ProcessAction.LowerPriority when current.OriginalPriority is null:
                uint priority = GetPriorityClass(process);
                if (priority == 0)
                {
                    return new(false);
                }

                updated = current with { OriginalPriority = priority };
                break;
            case ProcessAction.RestorePriority when current.OriginalPriority.HasValue:
                updated = current with { OriginalPriority = null };
                break;
            default:
                return new(false);
        }

        bool recording = action is ProcessAction.Pause or ProcessAction.LowerPriority;
        if (recording)
        {
            SaveState(state, updated);
        }

        bool success = action switch
        {
            ProcessAction.Pause => NtSuspendProcess(process) >= 0,
            ProcessAction.Resume => NtResumeProcess(process) >= 0,
            ProcessAction.LowerPriority => updated.OriginalPriority != 0x40 && SetPriorityClass(process, updated.OriginalPriority == 0x4000 ? 0x40u : 0x4000u),
            ProcessAction.RestorePriority => SetPriorityClass(process, current.OriginalPriority!.Value),
            _ => false
        };

        if (success != recording)
        {
            SaveState(state, success ? updated : current);
        }

        return new(success);
    }

    private void SaveState(List<ProcessActionState> state, ProcessActionState value)
    {
        state.RemoveAll(item => item.Id == value.Id);
        if (value.Paused || value.OriginalPriority.HasValue)
        {
            state.Add(value);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(statePath))!);
        string temporary = statePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state));
        File.Move(temporary, statePath, overwrite: true);
    }

    private static ProcessActionResult Trim(SafeFileHandle process)
    {
        uint size = (uint)Marshal.SizeOf<MemoryCounters>();
        if (!K32GetProcessMemoryInfo(process, out MemoryCounters before, size) || !K32EmptyWorkingSet(process))
        {
            return new(false);
        }

        return new(true, (long)before.WorkingSet,
            K32GetProcessMemoryInfo(process, out MemoryCounters after, size) ? (long)after.WorkingSet : -1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public uint Size;
        public uint PageFaults;
        public nuint PeakWorkingSet;
        public nuint WorkingSet;
        public nuint PeakPagedPool;
        public nuint PagedPool;
        public nuint PeakNonPagedPool;
        public nuint NonPagedPool;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [DllImport("kernel32.dll")]
    private static extern SafeFileHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint id);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeFileHandle process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessCritical(SafeFileHandle process, [MarshalAs(UnmanagedType.Bool)] out bool critical);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeFileHandle process, uint flags, char[] image, ref uint length);

    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(SafeFileHandle process);

    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(SafeFileHandle process);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32EmptyWorkingSet(SafeFileHandle process);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(SafeFileHandle process, out MemoryCounters counters, uint size);

    [DllImport("kernel32.dll")]
    private static extern uint GetPriorityClass(SafeFileHandle process);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(SafeFileHandle process, uint priority);
}
