using System.Globalization;

namespace PerformanceTracker.Core.Services;

/// <summary>One Linux process as reported by <c>ps -eo pid=,times=,pcpu=,args=</c> inside WSL.</summary>
public sealed record WslProcessRow(int Pid, long CpuSeconds, double LifetimeCpuPercent, string Command);

/// <summary>What one command inside WSL is doing, e.g. "florida_counties.py" with 9 processes using 24% of the PC.</summary>
public sealed record WslActivity(string Name, int ProcessCount, double CpuPercent);

/// <summary>Parses and summarises what is running inside WSL so the app can name it instead of just "WSL memory".</summary>
public static class WslSummary
{
    private const double MinimumCpuPercent = 0.5;

    /// <summary>Parses <c>ps -eo pid=,times=,pcpu=,args=</c> output. Unparseable lines are skipped.</summary>
    public static IReadOnlyList<WslProcessRow> Parse(string output)
    {
        List<WslProcessRow> rows = [];
        foreach (string rawLine in output.Split('\n'))
        {
            string[] parts = rawLine.Trim().Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 ||
                !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) ||
                !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long cpuSeconds) ||
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double lifetime))
            {
                continue;
            }

            string command = parts[3].Trim();
            // Kernel threads ("[kworker/0:1]") and the ps call itself aren't interesting.
            if (command.StartsWith('[') || command.StartsWith("ps -eo", StringComparison.Ordinal))
            {
                continue;
            }

            rows.Add(new WslProcessRow(pid, cpuSeconds, lifetime, command));
        }

        return rows;
    }

    /// <summary>
    /// Groups processes running the same script or program (e.g. 8 workers of one import) and works out each group's share of the whole PC.
    /// With a previous reading, CPU is measured over the gap between readings; otherwise ps's lifetime average is used.
    /// </summary>
    /// <param name="current">Rows from the latest reading.</param>
    /// <param name="previousCpuSeconds">CPU-seconds per pid from the previous reading, if any.</param>
    /// <param name="elapsedSeconds">Wall-clock seconds between the two readings.</param>
    /// <param name="hostProcessors">Logical processors on the Windows host, so percentages match the rest of the app.</param>
    public static IReadOnlyList<WslActivity> Summarize(
        IReadOnlyList<WslProcessRow> current,
        IReadOnlyDictionary<int, long>? previousCpuSeconds,
        double elapsedSeconds,
        int hostProcessors)
    {
        int processors = Math.Max(1, hostProcessors);
        return current
            .GroupBy(row => Name(row.Command), StringComparer.Ordinal)
            .Select(group =>
            {
                double coresBusy = group.Sum(row =>
                    previousCpuSeconds is not null && elapsedSeconds > 0 && previousCpuSeconds.TryGetValue(row.Pid, out long before)
                        ? Math.Max(0, row.CpuSeconds - before) / elapsedSeconds
                        : row.LifetimeCpuPercent / 100);
                return new WslActivity(group.Key, group.Count(), Math.Round(coresBusy / processors * 100, 1, MidpointRounding.AwayFromZero));
            })
            .Where(activity => activity.CpuPercent >= MinimumCpuPercent)
            .OrderByDescending(activity => activity.CpuPercent)
            .ToArray();
    }

    /// <summary>Short label, e.g. "florida_counties.py (9 processes)".</summary>
    public static string Label(WslActivity activity) =>
        activity.ProcessCount == 1 ? activity.Name : $"{activity.Name} ({activity.ProcessCount} processes)";

    /// <summary>One line for the busiest few, e.g. "florida_counties.py (9 processes) 24% CPU · node 2% CPU".</summary>
    public static string? Describe(IReadOnlyList<WslActivity> activities, int max = 3) =>
        activities.Count == 0
            ? null
            : "Inside Linux: " + string.Join("  ·  ", activities.Take(max).Select(a =>
                string.Create(CultureInfo.CurrentCulture, $"{Label(a)} {a.CpuPercent:0.#}% CPU")));

    /// <summary>A readable name for a Linux command line: the script for interpreters, otherwise the program.</summary>
    public static string Name(string command)
    {
        string? script = AppGroupBuilder.ExtractScript(command);
        if (script is not null)
        {
            return script;
        }

        string first = command.Split(' ', 2)[0];
        return first[(first.LastIndexOf('/') + 1)..];
    }
}
