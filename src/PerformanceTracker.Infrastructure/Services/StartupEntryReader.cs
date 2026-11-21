using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PerformanceTracker.Core.Interfaces;
using PerformanceTracker.Core.Models;
using PerformanceTracker.Core.Services;

namespace PerformanceTracker.Infrastructure.Services;

public sealed record StartupEntry(
    string Name, string Explanation, string Source, string Location, string Publisher,
    bool? Enabled, bool Recommended, string Running, string? ChangeNote, StartupApprovalTarget? Target)
{
    public bool CanChange => Target is not null && ChangeNote is null && Enabled.HasValue;
    public string Advice => Recommended ? "Recommended to keep" : "";
    public override string ToString() => Name;
}

public sealed record StartupApprovalTarget(RegistryHive Hive, string Bucket, string Name, string? RunKey, string Original);

public sealed class StartupEntryReader(IProcessInventory inventory)
{
    private const string CurrentVersion = @"Software\Microsoft\Windows\CurrentVersion\";
    private const string WowVersion = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\";
    private const string Approval = CurrentVersion + @"Explorer\StartupApproved\";
    private static readonly RegistryView View = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;

    public IReadOnlyList<StartupEntry> Read()
    {
        IReadOnlyList<RunningProcessInfo> processes = inventory.Capture(includeDetails: true);
        var byPath = processes.Where(process => process.ExecutablePath is not null)
            .ToLookup(process => process.ExecutablePath!, StringComparer.OrdinalIgnoreCase);
        var byId = processes.ToDictionary(process => process.Id);
        List<StartupEntry> entries = [];
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(hive, View);
            var approvals = ReadApprovals(root).ToLookup(entry => entry.Bucket, StringComparer.OrdinalIgnoreCase);
            foreach (string version in new[] { CurrentVersion, WowVersion })
            {
                ReadRun(root, hive, version + "Run", version == WowVersion ? "Run32" : "Run", approvals, byPath, entries);
                ReadRun(root, hive, version + "RunOnce", null, approvals, byPath, entries);
            }

            ReadFolder(hive, approvals, byPath, entries);
        }

        ReadTasks(byPath, entries);
        ReadServices(byPath, byId, entries);
        return entries.OrderBy(entry => entry.Target is null)
            .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static void SetEnabled(StartupEntry entry, bool enabled)
    {
        if (!entry.CanChange || entry.Target is not { } target)
        {
            throw new InvalidOperationException("This item cannot be changed here.");
        }

        using RegistryKey root = RegistryKey.OpenBaseKey(target.Hive, View);
        using RegistryKey? source = target.RunKey is null ? null : root.OpenSubKey(target.RunKey);
        bool present = target.RunKey is null ? File.Exists(target.Original) :
            string.Equals(source?.GetValue(target.Name) as string, target.Original, StringComparison.Ordinal);
        if (!present)
        {
            throw new InvalidOperationException("The startup item changed. Refresh the list and try again.");
        }

        using RegistryKey key = root.CreateSubKey(Approval + target.Bucket);
        object? current = key.GetValue(target.Name);
        if ((current is not null and not byte[]) || StartupApproval.Read(current as byte[]) is null)
        {
            throw new InvalidOperationException("Windows reported an unrecognized startup setting.");
        }

        key.SetValue(target.Name, StartupApproval.Write(enabled, DateTimeOffset.UtcNow, current as byte[]), RegistryValueKind.Binary);
    }

    private static void ReadRun(RegistryKey root, RegistryHive hive, string path, string? bucket,
        ILookup<string, StartupApprovalRecord> approvals,
        ILookup<string, RunningProcessInfo> byPath, List<StartupEntry> entries)
    {
        using RegistryKey? key = root.OpenSubKey(path);
        if (key is null)
        {
            return;
        }

        foreach (string name in key.GetValueNames())
        {
            if (key.GetValue(name) is string command)
            {
                StartupApprovalTarget? target = bucket is null ? null : new(hive, bucket, name, path, command);
                Add(entries, byPath, name, bucket ?? "RunOnce", Resolve(command), command, null,
                    target is null ? null : StartupApproval.Match(approvals, target.Bucket, target.Name), target,
                    target is null ? "Runs once. Managed by the app that added it." : ChangeNote(hive));
            }
        }
    }

    private static void ReadFolder(RegistryHive hive, ILookup<string, StartupApprovalRecord> approvals,
        ILookup<string, RunningProcessInfo> byPath, List<StartupEntry> entries)
    {
        string folder = Environment.GetFolderPath(hive == RegistryHive.CurrentUser
            ? Environment.SpecialFolder.Startup : Environment.SpecialFolder.CommonStartup);
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (string file in Directory.EnumerateFiles(folder).Where(file =>
                     !File.GetAttributes(file).HasFlag(FileAttributes.Hidden) &&
                     !string.Equals(Path.GetFileName(file), "desktop.ini", StringComparison.OrdinalIgnoreCase)))
        {
            string name = Path.GetFileName(file);
            StartupApprovalTarget target = new(hive, "StartupFolder", name, null, file);
            string executable = file;
            if (string.Equals(Path.GetExtension(file), ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    dynamic shell = CreateCom("WScript.Shell");
                    executable = shell.CreateShortcut(file).TargetPath;
                }
                catch (COMException)
                {
                    executable = "";
                }
            }

            Add(entries, byPath, Path.GetFileNameWithoutExtension(name), "Startup folder", executable, file,
                null, StartupApproval.Match(approvals, target.Bucket, target.Name), target, ChangeNote(hive));
        }
    }

    private static void ReadTasks(ILookup<string, RunningProcessInfo> byPath, List<StartupEntry> entries)
    {
        dynamic scheduler = CreateCom("Schedule.Service");
        scheduler.Connect();
        ReadTaskFolder(scheduler.GetFolder("\\"), byPath, entries);
    }

    private static void ReadTaskFolder(object folderObject, ILookup<string, RunningProcessInfo> byPath, List<StartupEntry> entries)
    {
        dynamic folder = folderObject;
        try
        {
            foreach (dynamic task in folder.GetTasks(1))
            {
                try
                {
                    dynamic definition = task.Definition;
                    if (!((System.Collections.IEnumerable)definition.Triggers).Cast<dynamic>()
                        .Any(trigger => (int)trigger.Type == 9))
                    {
                        continue;
                    }

                    string executable = "";
                    foreach (dynamic action in definition.Actions)
                    {
                        if ((int)action.Type == 0)
                        {
                            executable = Resolve((string)action.Path);
                            break;
                        }
                    }

                    string name = task.Name;
                    string path = task.Path;
                    string? author = definition.RegistrationInfo.Author;
                    bool enabled = task.Enabled;
                    bool running = (int)task.State == 4;
                    Add(entries, byPath, name, "Logon task", executable, path, author, enabled, null,
                        "Change in Task Scheduler.", runningStatus: running ? null : "Not running", taskRunning: running);
                }
                catch (COMException)
                {
                    // A task can disappear or be inaccessible while enumerating.
                }
            }

            foreach (object child in folder.GetFolders(0))
            {
                ReadTaskFolder(child, byPath, entries);
            }
        }
        catch (COMException)
        {
            // Task Scheduler restricts access to some folders.
        }
    }

    private static void ReadServices(ILookup<string, RunningProcessInfo> byPath,
        Dictionary<int, RunningProcessInfo> byId, List<StartupEntry> entries)
    {
        using ManagementObjectSearcher searcher = new(
            "SELECT DisplayName, PathName, State, ProcessId FROM Win32_Service WHERE StartMode='Auto'");
        using ManagementObjectCollection services = searcher.Get();
        foreach (ManagementBaseObject service in services)
        {
            using (service)
            {
                string command = service["PathName"] as string ?? "";
                string name = service["DisplayName"] as string ?? "Windows service";
                int pid = (int)(uint)service["ProcessId"];
                string status = service["State"] as string ?? "Unknown";
                Add(entries, byPath, name, "Automatic service", Resolve(command), command, null, true, null,
                    "Change in Windows Services.", byId.GetValueOrDefault(pid), status);
            }
        }
    }

    private static void Add(List<StartupEntry> entries, ILookup<string, RunningProcessInfo> byPath,
        string name, string source, string executable, string location, string? publisher, bool? enabled,
        StartupApprovalTarget? target, string? note, RunningProcessInfo? serviceProcess = null, string? runningStatus = null, bool taskRunning = false)
    {
        string processName = Path.GetFileNameWithoutExtension(executable);
        ProcessCatalog.Entry? info = ProcessCatalog.Lookup(processName) ?? ProcessCatalog.Lookup(name);
        FileVersionInfo? version = null;
        if (File.Exists(executable))
        {
            try
            {
                version = FileVersionInfo.GetVersionInfo(executable);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The file may have been removed since the existence check.
            }
        }

        var matches = runningStatus is null ? byPath[executable].ToArray() : [];
        string running = serviceProcess is not null ? $"Running · {serviceProcess.MemoryBytes / 1048576d:F1} MB (host)" :
            matches.Length > 0 ? $"Running · {matches.Sum(process => process.MemoryBytes) / 1048576d:F1} MB" :
            runningStatus ?? (taskRunning ? "Running · memory unavailable" : "Not seen running");
        entries.Add(new(info?.DisplayName ?? name, info?.Explanation ?? version?.FileDescription ?? "Starts automatically with Windows.",
            source, string.IsNullOrEmpty(executable) ? location : executable, version?.CompanyName ?? publisher ?? "Unknown",
            enabled, StartupApproval.RecommendedToKeep(executable, name), running, note, target));
    }

    private static string? ChangeNote(RegistryHive hive)
    {
        if (hive == RegistryHive.CurrentUser)
        {
            return null;
        }

        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var principal = new System.Security.Principal.WindowsPrincipal(identity);
        return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator) ? null : "Needs admin to change";
    }

    private static IEnumerable<StartupApprovalRecord> ReadApprovals(RegistryKey root)
    {
        foreach (string bucket in new[] { "Run", "Run32", "StartupFolder" })
        {
            using RegistryKey? key = root.OpenSubKey(Approval + bucket);
            if (key is not null)
            {
                foreach (string name in key.GetValueNames())
                {
                    yield return new(bucket, name, key.GetValue(name));
                }
            }
        }
    }

    private static object CreateCom(string programId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(programId, throwOnError: true)!)!;

    private static string Resolve(string command)
    {
        string expanded = Environment.ExpandEnvironmentVariables(command).Trim();
        if (expanded.StartsWith('"'))
        {
            return expanded.Split('"')[1];
        }

        int extension = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        string path = extension >= 0 ? expanded[..(extension + 4)] : expanded;
        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), path[12..]);
        }

        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path) == path)
        {
            string systemPath = Path.Combine(Environment.SystemDirectory, path);
            return File.Exists(systemPath) ? systemPath : path;
        }

        return path;
    }
}
