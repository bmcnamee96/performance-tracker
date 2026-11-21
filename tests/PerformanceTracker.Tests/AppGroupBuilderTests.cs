using PerformanceTracker.Core.Models;
using PerformanceTracker.Core.Services;
using PerformanceTracker.Infrastructure.Services;
using Xunit;

namespace PerformanceTracker.Tests;

public sealed class AppGroupBuilderTests
{
    private const long Mb = 1024 * 1024;
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GroupsProcessesByNameAndSortsByMemory()
    {
        RunningProcessInfo[] processes =
        [
            Process(1, "chrome", 300 * Mb, windowTitle: "Inbox"),
            Process(2, "chrome", 200 * Mb),
            Process(3, "notepad", 900 * Mb, windowTitle: "notes.txt")
        ];

        IReadOnlyList<AppGroup> groups = AppGroupBuilder.Build(processes, Quiet(processes, TimeSpan.Zero), selfProcessId: 999);

        Assert.Equal(["notepad", "chrome"], groups.Select(g => g.Key));
        AppGroup chrome = groups[1];
        Assert.Equal("Google Chrome", chrome.DisplayName);
        Assert.Equal(500 * Mb, chrome.MemoryBytes);
        Assert.Equal([1, 2], chrome.ProcessIds);
        Assert.True(chrome.HasWindow);
        Assert.Equal(AppSafety.Caution, chrome.Safety);
    }

    [Fact]
    public void WindowsProcessesAndThisAppAreProtected()
    {
        RunningProcessInfo[] processes =
        [
            Process(1, "svchost", 50 * Mb, sessionId: 0),
            Process(2, "PerformanceTracker.Desktop", 80 * Mb)
        ];

        IReadOnlyList<AppGroup> groups = AppGroupBuilder.Build(processes, Quiet(processes, TimeSpan.FromHours(1)), selfProcessId: 2);

        Assert.All(groups, group =>
        {
            Assert.Equal(AppSafety.Protected, group.Safety);
            Assert.False(group.CanEnd);
            Assert.False(group.IsSuggested);
        });
    }

    [Fact]
    public void UnknownSessionZeroProcessIsAService()
    {
        RunningProcessInfo[] processes = [Process(1, "vendorupdater", 120 * Mb, sessionId: 0, description: "Vendor Updater")];

        AppGroup group = Assert.Single(AppGroupBuilder.Build(processes, Quiet(processes, TimeSpan.Zero), selfProcessId: 999));

        Assert.Equal("Vendor Updater", group.DisplayName);
        Assert.Equal(AppSafety.Service, group.Safety);
        Assert.NotNull(group.Caution);
    }

    [Fact]
    public void SuggestsLargeBackgroundAppsThatHaveBeenQuiet()
    {
        RunningProcessInfo[] processes =
        [
            Process(1, "bigidle", 800 * Mb),
            Process(2, "smallidle", 20 * Mb),
            Process(3, "windowedidle", 800 * Mb, windowTitle: "Doc")
        ];

        IReadOnlyList<AppGroup> groups = AppGroupBuilder.Build(processes, Quiet(processes, TimeSpan.FromMinutes(15)), selfProcessId: 999);

        Assert.True(groups.Single(g => g.Key == "bigidle").IsSuggested);
        Assert.False(groups.Single(g => g.Key == "smallidle").IsSuggested);
        // Apps with a window need a longer quiet period before they're suggested.
        Assert.False(groups.Single(g => g.Key == "windowedidle").IsSuggested);
    }

    [Fact]
    public void BusyAppsAreNotSuggested()
    {
        RunningProcessInfo[] processes = [Process(1, "python", 6000 * Mb, commandLine: "python.exe -B scripts\\atlas_crawl.py --out D:\\x")];
        Dictionary<int, ProcessActivity> activity = new() { [1] = new ProcessActivity(12.5, TimeSpan.Zero, TimeSpan.FromHours(1)) };

        AppGroup group = Assert.Single(AppGroupBuilder.Build(processes, activity, selfProcessId: 999));

        Assert.Equal(AppActivity.Busy, group.Activity);
        Assert.False(group.IsSuggested);
        Assert.Equal("Running: atlas_crawl.py", group.Detail);
    }

    [Fact]
    public void RecentlySeenProcessesAreNotJudgedYet()
    {
        RunningProcessInfo[] processes = [Process(1, "bigidle", 800 * Mb)];
        Dictionary<int, ProcessActivity> activity = new() { [1] = new ProcessActivity(0, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(20)) };

        AppGroup group = Assert.Single(AppGroupBuilder.Build(processes, activity, selfProcessId: 999));

        Assert.Equal(AppActivity.Unknown, group.Activity);
        Assert.False(group.IsSuggested);
    }

    [Theory]
    [InlineData("\"C:\\Python312\\python.exe\" -B scripts\\atlas_crawl.py --targets \"D:\\a b\\t.json\"", "atlas_crawl.py")]
    [InlineData("python -m http.server 8000", "http.server")]
    [InlineData("\"node.exe\" \"C:\\npm\\node_modules\\@openai\\codex\\bin\\codex.js\" app-server", "codex.js")]
    [InlineData("node --inspect", null)]
    [InlineData("python -c \"print(1)\" notes.py", null)]
    [InlineData("pwsh -Command \"Write-Host ready\" notes.ps1", null)]
    [InlineData(null, null)]
    public void ExtractsScriptNames(string? commandLine, string? expected)
    {
        Assert.Equal(expected, AppGroupBuilder.ExtractScript(commandLine));
    }

    [Theory]
    [InlineData("chrome", "chrome.exe --type=renderer", "Tab or site")]
    [InlineData("msedge", "msedge.exe --type=renderer --extension-process", "Extension")]
    [InlineData("brave", "brave.exe --type=gpu-process", "Graphics (GPU)")]
    [InlineData("msedgewebview2", "msedgewebview2.exe --type=utility", "Utility")]
    [InlineData("chrome", "chrome.exe", "Browser core")]
    [InlineData("chrome", null, "Browser process")]
    [InlineData("chrome", "chrome.exe --type=other", "Browser process")]
    [InlineData("notepad", "notepad.exe --type=renderer", null)]
    public void ClassifiesBrowserProcesses(string name, string? commandLine, string? role)
    {
        Assert.Equal(role, AppGroupBuilder.ClassifyBrowser(name, commandLine)?.Name);
    }

    [Fact]
    public void OnlyOrdinaryQuietBackgroundProcessesAreStaleCandidates()
    {
        RunningProcessInfo[] processes =
        [
            Process(1, "quiet", 300 * Mb),
            Process(2, "python", 300 * Mb),
            Process(3, "chrome", 300 * Mb),
            Process(4, "windowed", 300 * Mb, windowTitle: "Document"),
            Process(5, "service", 300 * Mb, sessionId: 0),
            Process(6, "critical", 300 * Mb) with { IsProtected = true },
            Process(7, "unidentified", 300 * Mb) with { StartTimeUtc = null }
        ];

        IReadOnlyList<AppGroup> groups = AppGroupBuilder.Build(processes, Quiet(processes, TimeSpan.FromHours(2)), selfProcessId: 999);
        Assert.Equal("quiet", Assert.Single(groups, group => group.IsSuggested).Key);
        Assert.Equal(2, groups.Count(group => !group.CanEnd));
        Assert.All(groups, group => Assert.Equal(group.IsSuggested, Assert.Single(group.Processes).IsSuggested));
    }

    [Fact]
    public void UnreadableCpuBreaksTheQuietObservation()
    {
        ProcessActivityTracker tracker = new(processorCount: 1);
        RunningProcessInfo process = Process(1, "app", Mb);
        tracker.Observe([process], Start);
        tracker.Observe([process with { TotalCpuTime = null }], Start.AddMinutes(10));

        Assert.Null(tracker.Observe([process], Start.AddMinutes(20))[1].QuietFor);
        Assert.Equal(TimeSpan.FromMinutes(1), tracker.Observe([process], Start.AddMinutes(21))[1].QuietFor);
    }

    [Fact]
    public void ActivityTrackerMeasuresCpuAndQuietTime()
    {
        ProcessActivityTracker tracker = new(processorCount: 4);
        RunningProcessInfo first = Process(1, "app", Mb) with { TotalCpuTime = TimeSpan.FromSeconds(10) };

        tracker.Observe([first], Start);
        // 4 CPU-seconds over 10 wall-seconds on 4 cores = 10%.
        IReadOnlyDictionary<int, ProcessActivity> busy = tracker.Observe([first with { TotalCpuTime = TimeSpan.FromSeconds(14) }], Start.AddSeconds(10));
        Assert.Equal(10, busy[1].CpuPercent, precision: 3);
        Assert.Equal(TimeSpan.Zero, busy[1].QuietFor);

        IReadOnlyDictionary<int, ProcessActivity> quiet = tracker.Observe([first with { TotalCpuTime = TimeSpan.FromSeconds(14) }], Start.AddMinutes(5));
        Assert.Equal(0, quiet[1].CpuPercent);
        Assert.Equal(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(10), quiet[1].QuietFor);
    }

    [Fact]
    public void ActivityTrackerTreatsReusedProcessIdAsNewProcess()
    {
        ProcessActivityTracker tracker = new(processorCount: 1);
        tracker.Observe([Process(1, "old", Mb) with { TotalCpuTime = TimeSpan.Zero }], Start);

        RunningProcessInfo replacement = Process(1, "new", Mb) with { StartTimeUtc = Start.AddMinutes(1), TotalCpuTime = TimeSpan.FromSeconds(30) };
        IReadOnlyDictionary<int, ProcessActivity> result = tracker.Observe([replacement], Start.AddMinutes(2));

        Assert.Equal(TimeSpan.Zero, result[1].ObservedFor);
        Assert.Equal(0, result[1].CpuPercent);
    }

    [Fact]
    public void InventoryCapturesCurrentProcess()
    {
        WindowsProcessInventory inventory = new();

        IReadOnlyList<RunningProcessInfo> processes = inventory.Capture(includeDetails: true);

        RunningProcessInfo self = Assert.Single(processes, p => p.Id == Environment.ProcessId);
        Assert.True(self.MemoryBytes > 0);
        Assert.NotNull(self.TotalCpuTime);
        Assert.NotNull(self.ExecutablePath);
    }

    [Fact]
    public async Task TerminatorEndsBackgroundProcess()
    {
        using System.Diagnostics.Process child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ping.exe",
            Arguments = "-n 120 127.0.0.1",
            CreateNoWindow = true,
            UseShellExecute = false
        })!;

        Core.Interfaces.EndProcessResult stale = await new WindowsProcessTerminator().EndAsync(
            [Process(child.Id, child.ProcessName, 0) with { StartTimeUtc = child.StartTime.ToUniversalTime().AddSeconds(-1) }],
            force: true, CancellationToken.None);
        Assert.Equal(1, stale.AccessDenied);
        Assert.False(child.HasExited);

        Core.Interfaces.EndProcessResult result = await new WindowsProcessTerminator().EndAsync([Process(child.Id, child.ProcessName, 0) with { StartTimeUtc = child.StartTime.ToUniversalTime() }], force: false, CancellationToken.None);

        Assert.Equal(1, result.Ended);
        Assert.Equal(0, result.StillRunning);
        Assert.True(child.HasExited);
    }

    [Fact]
    public async Task TerminatorRefusesProtectedAndSelf()
    {
        int svchost = System.Diagnostics.Process.GetProcessesByName("svchost")[0].Id;

        Core.Interfaces.EndProcessResult result = await new WindowsProcessTerminator().EndAsync([Process(svchost, "svchost", 0), Process(Environment.ProcessId, "dotnet", 0)], force: true, CancellationToken.None);

        Assert.Equal(new Core.Interfaces.EndProcessResult(0, 0, 2, 0), result);
    }

    private static RunningProcessInfo Process(
        int id,
        string name,
        long memory,
        string? windowTitle = null,
        int sessionId = 1,
        string? description = null,
        string? commandLine = null) =>
        new()
        {
            Id = id,
            Name = name,
            MemoryBytes = memory,
            TotalCpuTime = TimeSpan.FromSeconds(1),
            StartTimeUtc = Start,
            SessionId = sessionId,
            WindowTitle = windowTitle,
            FileDescription = description,
            CommandLine = commandLine
        };

    private static Dictionary<int, ProcessActivity> Quiet(IEnumerable<RunningProcessInfo> processes, TimeSpan quietFor) =>
        processes.ToDictionary(p => p.Id, _ => new ProcessActivity(0, quietFor, TimeSpan.FromHours(2)));
}
