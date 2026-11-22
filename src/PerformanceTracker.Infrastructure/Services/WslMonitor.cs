using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using PerformanceTracker.Core.Services;

namespace PerformanceTracker.Infrastructure.Services;

/// <summary>
/// Looks inside running WSL distros (never starts one) to see which Linux commands are busy.
/// Reads happen in the background at most every <see cref="RefreshInterval"/>; callers get the latest result instantly.
/// </summary>
public sealed class WslMonitor
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private IReadOnlyList<WslActivity> _current = [];
    private Dictionary<int, long>? _previousCpuSeconds;
    private long _previousTimestamp;
    private DateTime _nextRefreshUtc = DateTime.MinValue;
    private bool _refreshing;

    /// <summary>Busiest Linux commands from the last reading, busiest first. Empty when WSL isn't running.</summary>
    public IReadOnlyList<WslActivity> Current
    {
        get
        {
            RefreshIfDue();
            lock (_lock)
            {
                return _current;
            }
        }
    }

    private void RefreshIfDue()
    {
        lock (_lock)
        {
            if (_refreshing || DateTime.UtcNow < _nextRefreshUtc)
            {
                return;
            }

            _refreshing = true;
            _nextRefreshUtc = DateTime.UtcNow + RefreshInterval;
        }

        _ = Task.Run(Refresh);
    }

    private void Refresh()
    {
        try
        {
            if (!IsWslVmRunning())
            {
                Store([], null, 0);
                return;
            }

            List<WslProcessRow> rows = [];
            int distroIndex = 0;
            foreach (string distro in RunningDistros())
            {
                string? output = Run("wsl.exe", ["-d", distro, "-e", "ps", "-eo", "pid=,times=,pcpu=,args="], Encoding.UTF8);
                if (output is not null)
                {
                    // Pids are per distro; offset them so two distros can't collide in the CPU-time map.
                    int offset = distroIndex * 10_000_000;
                    rows.AddRange(WslSummary.Parse(output).Select(row => row with { Pid = row.Pid + offset }));
                }

                distroIndex++;
            }

            long now = Stopwatch.GetTimestamp();
            Dictionary<int, long>? previous;
            double elapsed;
            lock (_lock)
            {
                previous = _previousCpuSeconds;
                elapsed = previous is null ? 0 : (double)(now - _previousTimestamp) / Stopwatch.Frequency;
            }

            IReadOnlyList<WslActivity> summary = WslSummary.Summarize(rows, previous, elapsed, Environment.ProcessorCount);
            Store(summary, rows.GroupBy(row => row.Pid).ToDictionary(g => g.Key, g => g.First().CpuSeconds), now);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            Store([], null, 0);
        }
        finally
        {
            lock (_lock)
            {
                _refreshing = false;
            }
        }
    }

    private void Store(IReadOnlyList<WslActivity> summary, Dictionary<int, long>? cpuSeconds, long timestamp)
    {
        lock (_lock)
        {
            _current = summary;
            _previousCpuSeconds = cpuSeconds;
            _previousTimestamp = timestamp;
        }
    }

    // WSL 2 distros run inside one lightweight VM; without it there's nothing to look at, and we must not start it.
    private static bool IsWslVmRunning()
    {
        Process[] vms = Process.GetProcessesByName("vmmemWSL");
        try
        {
            return vms.Length > 0;
        }
        finally
        {
            foreach (Process vm in vms)
            {
                vm.Dispose();
            }
        }
    }

    private static IEnumerable<string> RunningDistros()
    {
        // wsl.exe prints its own messages in UTF-16.
        string? output = Run("wsl.exe", ["--list", "--running", "--quiet"], Encoding.Unicode);
        return (output ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name.Trim('\0'))
            .Where(name => name.Length > 0 && !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase));
    }

    private static string? Run(string fileName, IReadOnlyList<string> arguments, Encoding encoding)
    {
        ProcessStartInfo start = new(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = encoding
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process? process = Process.Start(start);
        if (process is null)
        {
            return null;
        }

        Task<string> output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(CommandTimeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            return null;
        }

        return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
    }
}
