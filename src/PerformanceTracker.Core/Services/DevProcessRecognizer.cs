using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Core.Services;

public static class DevProcessRecognizer
{
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "python", "pythonw", "node", "java", "javaw", "dotnet", "powershell", "pwsh", "bun", "deno", "ruby"
    };

    public static bool IsHost(string name) => Hosts.Contains(name);

    public static (string Tool, string? Project, string? Category) Identify(RunningProcessInfo process)
    {
        string[] arguments = AppGroupBuilder.Tokenize(process.CommandLine ?? string.Empty).Skip(1).ToArray();
        string[] names = arguments.Select(argument => FileName(argument).ToLowerInvariant()).ToArray();
        string? script = AppGroupBuilder.ExtractScript(process.CommandLine);
        var tool = names switch
        {
            _ when names.Contains("vite.js") || names.Contains("vite") => ("Vite dev server", "dev server"),
            _ when names.Any(name => name.StartsWith("next-server", StringComparison.Ordinal)) || (names.Contains("next") && names.Contains("dev")) => ("Next.js dev server", "dev server"),
            _ when names.Contains("tsserver.js") || names.Contains("typescript-language-server") => ("TypeScript language server", "language server"),
            _ when names.Contains("app-server") && names.Any(name => name.StartsWith("codex", StringComparison.Ordinal)) => ("Codex app-server", "agent tool"),
            _ when names.Any(name => name.Contains("update-notifier", StringComparison.Ordinal)) => ("npm (checking for updates)", "package tool"),
            _ when names.Contains("npm-cli.js") || names.Contains("npm") => ("npm", "package tool"),
            _ => (script is null ? process.Name : process.Name.StartsWith("python", StringComparison.OrdinalIgnoreCase) ? $"Python: {script}" : script, null)
        };

        string? project = InferProject(process.WorkingDirectory, arguments, script);
        return (tool.Item1, project, tool.Item2);
    }

    public static string? InferProject(string? directory, IEnumerable<string> arguments, string? script)
    {
        string? path = directory?.TrimEnd('/', '\\');
        if (string.IsNullOrWhiteSpace(path))
        {
            string? scriptPath = arguments.FirstOrDefault(argument => script is not null &&
                FileName(argument).Equals(script, StringComparison.OrdinalIgnoreCase) &&
                (argument.StartsWith('/') || argument.StartsWith(@"\\", StringComparison.Ordinal) || (argument.Length > 2 && argument[1] == ':')));
            if (scriptPath is null)
            {
                return null;
            }

            string normalized = scriptPath.Replace('\\', '/');
            int dependencies = normalized.IndexOf("/node_modules/", StringComparison.OrdinalIgnoreCase);
            path = normalized[..(dependencies >= 0 ? dependencies : normalized.LastIndexOf('/'))];
            if (FileName(path) is "scripts" or "src")
            {
                path = path[..Math.Max(0, path.LastIndexOf('/'))];
            }
        }

        string name = FileName(path);
        return name.Length == 0 || name.EndsWith(':') ? null : path;
    }

    public static string ProjectName(string project)
    {
        string[] folders = project.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        int worktrees = Array.FindLastIndex(folders, folder => folder.Equals("worktrees", StringComparison.OrdinalIgnoreCase));
        if (worktrees > 1 && worktrees + 1 < folders.Length)
        {
            return folders[worktrees - 1] == ".t3" ? folders[worktrees + 1] : folders[worktrees - 1];
        }

        return FileName(project);
    }

    public static string FileName(string path) => path[(Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\')) + 1)..];

    public static string? DescribeParent(RunningProcessInfo process, IReadOnlyDictionary<int, RunningProcessInfo> processes)
    {
        List<string> ancestors = [];
        HashSet<int> seen = [process.Id];
        while (seen.Count <= 16 && processes.TryGetValue(process.ParentId, out RunningProcessInfo? parent) &&
            parent.StartTimeUtc is DateTimeOffset start && process.StartTimeUtc is DateTimeOffset childStart &&
            start <= childStart && seen.Add(parent.Id))
        {
            string name = parent.Name.ToLowerInvariant() switch
            {
                "windowsterminal" => "Windows Terminal",
                "claude" => "Claude Code",
                "node_repl" => "Node.js shell",
                "codex" => "Codex",
                _ => parent.Name
            };

            if (ancestors.Count == 0 || ancestors[^1] != name)
            {
                ancestors.Add(name);
            }

            process = parent;
        }

        int launcher = ancestors.FindIndex(name => name is "Claude Code" or "Codex" or "Windows Terminal");
        if (launcher >= 0)
        {
            ancestors.RemoveRange(launcher + 1, ancestors.Count - launcher - 1);
        }
        else if (ancestors.Count > 5)
        {
            ancestors.RemoveRange(5, ancestors.Count - 5);
        }

        ancestors.Reverse();
        return ancestors.Count == 0 ? null : $"started by {string.Join(" → ", ancestors)}";
    }
}
