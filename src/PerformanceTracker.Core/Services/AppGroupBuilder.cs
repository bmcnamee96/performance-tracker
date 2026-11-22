using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Core.Services;

/// <summary>
/// Turns a flat process list into one row per app with a plain-language explanation and advice.
/// </summary>
public static class AppGroupBuilder
{
    /// <summary>Minimum private working set for an app to be suggested for closing.</summary>
    public const long SuggestMinimumBytes = 150L * 1024 * 1024;

    /// <summary>How long a background app must be quiet before it is suggested.</summary>
    public static readonly TimeSpan BackgroundQuietThreshold = TimeSpan.FromMinutes(10);

    /// <summary>How long a process must be observed before it can be called quiet.</summary>
    public static readonly TimeSpan MinimumObservation = TimeSpan.FromMinutes(1);

    public static IReadOnlyList<AppGroup> Build(
        IReadOnlyList<RunningProcessInfo> processes,
        IReadOnlyDictionary<int, ProcessActivity> activity,
        int selfProcessId)
    {
        List<AppGroup> groups = [];

        foreach (IGrouping<string, RunningProcessInfo> grouping in processes.GroupBy(p => p.Name.Trim().ToLowerInvariant()))
        {
            RunningProcessInfo[] members = grouping.ToArray();
            groups.Add(BuildGroup(grouping.Key, members, activity, selfProcessId, includeProcesses: true));
        }

        return groups
            .OrderByDescending(group => group.MemoryBytes)
            .ThenBy(group => group.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static AppGroup BuildGroup(
        string key,
        RunningProcessInfo[] members,
        IReadOnlyDictionary<int, ProcessActivity> activity,
        int selfProcessId,
        bool includeProcesses, string? subgroupKey = null)
    {
        bool subgroup = subgroupKey is not null;
        ProcessCatalog.Entry? entry = ProcessCatalog.Lookup(key);
        bool hasWindow = members.Any(m => m.HasWindow);
        bool hasService = members.Any(m => m.SessionId == 0);
        bool isSelf = members.Any(m => m.Id == selfProcessId);

        string displayName = entry?.DisplayName ?? MostCommon(members.Select(m => m.FileDescription)) ?? members[0].Name;
        string explanation = entry?.Explanation ?? DescribeUnknown(hasWindow, hasService);
        AppSafety safety = entry?.Safety == AppSafety.Protected ? AppSafety.Protected
            : hasService ? AppSafety.Service : entry?.Safety ?? AppSafety.Normal;
        string? caution = entry?.Caution;

        if (isSelf || members.Any(m => m.IsProtected || m.StartTimeUtc is null))
        {
            displayName = isSelf ? "Performance Tracker" : displayName;
            explanation = isSelf ? "This app." : explanation;
            safety = AppSafety.Protected;
            caution = null;
        }
        else if (safety == AppSafety.Service && caution is null)
        {
            caution = "Background service: ending it may need admin rights, and Windows may restart it.";
        }

        long memory = members.Sum(m => m.MemoryBytes);
        double cpu = 0;
        TimeSpan? quietFor = null;
        bool anyUnknown = false;
        foreach (RunningProcessInfo member in members)
        {
            if (!activity.TryGetValue(member.Id, out ProcessActivity? memberActivity) ||
                memberActivity.QuietFor is null ||
                memberActivity.ObservedFor < MinimumObservation)
            {
                anyUnknown = true;
                cpu += memberActivity?.CpuPercent ?? 0;
                continue;
            }

            cpu += memberActivity.CpuPercent;
            quietFor = quietFor is null || memberActivity.QuietFor < quietFor ? memberActivity.QuietFor : quietFor;
        }

        AppActivity state = cpu >= ProcessActivityTracker.QuietCpuPercent
            ? AppActivity.Busy
            : anyUnknown || quietFor is null ? AppActivity.Unknown : AppActivity.Quiet;

        if (state != AppActivity.Quiet)
        {
            quietFor = null;
        }

        bool suggested = safety == AppSafety.Normal && !hasWindow && !DevProcessRecognizer.IsHost(key) &&
                         state == AppActivity.Quiet &&
                         memory >= SuggestMinimumBytes &&
                         quietFor >= BackgroundQuietThreshold;

        BrowserProcessRole? role = includeProcesses ? null : ClassifyBrowser(key, members[0].CommandLine);
        if (role is not null && safety != AppSafety.Protected)
        {
            caution = role.Consequence;
        }

        RunningProcessInfo heaviest = members.MaxBy(m => m.MemoryBytes) ?? members[0];
        string? topActivity = includeProcesses && !subgroup && DevProcessRecognizer.IsHost(key)
            ? DescribeDevActivity(heaviest)
            : null;

        return new AppGroup
        {
            TopActivity = topActivity,
            Key = subgroupKey ?? (includeProcesses ? key : $"{members[0].Id}/{members[0].StartTimeUtc:O}"),
            DisplayName = subgroup ? DescribeTool(members[0], includeProject: true)
                : includeProcesses ? displayName
                : role?.Name ?? (DevProcessRecognizer.IsHost(key) ? DescribeTool(members[0], includeProject: false) : members[0].Name),
            Targets = members,
            ExecutablePath = includeProcesses ? members.Select(m => m.ExecutablePath).FirstOrDefault(p => p is not null) : null,
            Processes = includeProcesses
                ? subgroup || !DevProcessRecognizer.IsHost(key)
                    ? members.OrderByDescending(m => m.MemoryBytes)
                        .Select(m => BuildGroup(key, [m], activity, selfProcessId, includeProcesses: false)).ToArray()
                    : members.GroupBy(m =>
                        {
                            var (tool, project, _) = DevProcessRecognizer.Identify(m);
                            return (tool.ToUpperInvariant(), project?.Replace('\\', '/').ToUpperInvariant(),
                                UnknownProjectProcess: project is null ? m.Id : 0);
                        })
                        .OrderByDescending(g => g.Sum(m => m.MemoryBytes))
                        .Select(g => BuildGroup(key, g.ToArray(), activity, selfProcessId, includeProcesses: true, subgroupKey: $"tool/{key}/{g.Key}")).ToArray()
                : [],

            Explanation = explanation,
            Caution = subgroup ? "Child processes outside this group may keep running." : caution,
            Detail = subgroup ? $"{members.Length} {(members.Length == 1 ? "process" : "processes")}"
                : role is not null ? $"Process {members[0].Id}"
                : DescribeDetail(key, members, includeProcesses),
            Safety = safety,
            Activity = state,
            ProcessIds = members.Select(m => m.Id).ToArray(),
            MemoryBytes = memory,
            CpuPercent = Math.Round(cpu, 1),
            QuietFor = quietFor,
            HasWindow = hasWindow,
            IsSuggested = suggested
        };
    }

    public static bool IsChromium(string name) => name is "chrome" or "msedge" or "brave" or "msedgewebview2";

    public static BrowserProcessRole? ClassifyBrowser(string name, string? commandLine)
    {
        if (!IsChromium(name))
        {
            return null;
        }

        string[] args = commandLine is null ? [] : Tokenize(commandLine).Skip(1).TakeWhile(arg => arg != "--").ToArray();
        string? type = args.FirstOrDefault(arg => arg.StartsWith("--type=", StringComparison.Ordinal));
        return type switch
        {
            "--type=renderer" when args.Contains("--extension-process") =>
                new("Extension", "Ending this process can interrupt browser extensions."),
            "--type=renderer" =>
                new("Tab or site", "Ending this renderer can crash one or more tabs or parts of a page. The affected pages may show an error until reloaded."),
            "--type=gpu-process" => new("Graphics (GPU)", "Browser graphics may go blank or restart."),
            "--type=utility" => new("Utility", "A shared browser feature such as networking or audio may stop."),
            "--type=crashpad-handler" => new("Crash reporting", "Browser crash reports may stop."),
            null when !string.IsNullOrWhiteSpace(commandLine) =>
                new("Browser core", "Ending the browser core can close the entire browser, including its windows and tabs."),
            _ => new("Browser process", "Its role is unknown. Ending it may affect the entire browser.")
        };
    }

    public sealed record BrowserProcessRole(string Name, string Consequence);

    private static string DescribeUnknown(bool hasWindow, bool hasService) =>
        hasService ? "Includes a Windows background service."
        : hasWindow ? "App with an open window."
        : "Program running in the background with no window.";

    /// <summary>
    /// "Project · tool" for a developer-tool process (e.g. "Fantasy-Valorant · Vite dev server", "Python: atlas_crawl.py"),
    /// or null when nothing more specific than the executable name is known.
    /// </summary>
    public static string? DescribeDevActivity(RunningProcessInfo process)
    {
        var (tool, project, _) = DevProcessRecognizer.Identify(process);
        string? label = project is not null ? $"{DevProcessRecognizer.ProjectName(project)} · {tool}" : tool;
        return string.Equals(label, process.Name, StringComparison.OrdinalIgnoreCase) ? null : label;
    }

    private static string DescribeTool(RunningProcessInfo process, bool includeProject)
    {
        var (tool, project, _) = DevProcessRecognizer.Identify(process);
        return includeProject && project is not null ? $"{DevProcessRecognizer.ProjectName(project)} · {tool}" : tool;
    }

    private static string? DescribeDetail(string key, RunningProcessInfo[] members, bool summary)
    {
        if (DevProcessRecognizer.IsHost(key))
        {
            if (summary)
            {
                string[] counts = members.Select(m => DevProcessRecognizer.Identify(m).Category).OfType<string>()
                    .GroupBy(category => category)
                    .Select(g => $"{g.Count()} {g.Key}{(g.Count() == 1 ? "" : "s")}").ToArray();
                if (counts.Length > 0)
                {
                    return string.Join(", ", counts);
                }

                string[] scripts = members.Select(m => ExtractScript(m.CommandLine)).OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (scripts.Length > 0)
                {
                    return "Running: " + string.Join(", ", scripts.Take(3)) +
                        (scripts.Length > 3 ? $" and {scripts.Length - 3} more" : string.Empty);
                }
            }
            else
            {
                var process = members[0];
                var (_, project, category) = DevProcessRecognizer.Identify(process);
                return string.Join(" · ", new[]
                {
                    project is null ? null : $"in {DevProcessRecognizer.ProjectName(project)}",
                    process.ParentDescription,
                    process.ListeningPorts.Count == 0 ? null : string.Join(", ", process.ListeningPorts.Select(port =>
                        category == "dev server" && port.StartsWith("Local TCP ", StringComparison.Ordinal)
                            ? $"serving http://localhost:{port[10..]}" : port.Replace("Local TCP ", "TCP ", StringComparison.Ordinal)))
                }.OfType<string>());
            }
        }

        string? title = members.Select(m => m.WindowTitle).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        return title is null ? null : $"Window: {title}";
    }

    /// <summary>
    /// Pulls a recognisable script or module name out of a command line,
    /// e.g. <c>python -B scripts\crawl.py --out x</c> becomes <c>crawl.py</c>.
    /// </summary>
    public static string? ExtractScript(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        string[] tokens = Tokenize(commandLine);
        for (int i = 1; i < tokens.Length; i++)
        {
            string token = tokens[i];
            if (token is "-c" or "-Command" or "-EncodedCommand" or "--eval" or "-e")
            {
                return null;
            }

            if (token is "-m" && i + 1 < tokens.Length)
            {
                return tokens[i + 1];
            }

            // Preloaded helpers (node --require x.cjs, --import, --loader) aren't the program being run.
            if (token is "-r" or "--require" or "--import" or "--loader" or "--experimental-loader")
            {
                i++;
                continue;
            }

            if (token.StartsWith('-'))
            {
                continue;
            }

            string extension = Path.GetExtension(token).ToLowerInvariant();
            if (extension is ".py" or ".js" or ".mjs" or ".cjs" or ".ts" or ".jar" or ".dll" or ".ps1" or ".rb")
            {
                return Path.GetFileName(token);
            }
        }

        return null;
    }

    internal static string[] Tokenize(string commandLine)
    {
        List<string> tokens = [];
        System.Text.StringBuilder current = new();
        bool inQuotes = false;

        foreach (char c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens.ToArray();
    }

    private static string? MostCommon(IEnumerable<string?> values) =>
        values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .GroupBy(v => v!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
}
