using System.Diagnostics;
using PerformanceTracker.Core.Models;
using PerformanceTracker.Core.Services;
using PerformanceTracker.Infrastructure.Services;
using Xunit;

namespace PerformanceTracker.Tests;

public sealed class DevProcessTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData("node", @"node C:\Dev\App\node_modules\vite\bin\vite.js", "Vite dev server")]
    [InlineData("node", "node next dev", "Next.js dev server")]
    [InlineData("node", "node tsserver.js", "TypeScript language server")]
    [InlineData("node", "node codex.js app-server", "Codex app-server")]
    [InlineData("python", "python scripts/atlas_crawl.py", "Python: atlas_crawl.py")]
    [InlineData("dotnet", "dotnet Api.dll", "Api.dll")]
    public void RecognizesTools(string host, string command, string expected)
    {
        Assert.Equal(expected, DevProcessRecognizer.Identify(new() { Id = 10, Name = host, CommandLine = command }).Tool);
    }

    [Fact]
    public void InfersProjectAndGroupsByFullPath()
    {
        var vite = new RunningProcessInfo { Id = 1, Name = "node", CommandLine = @"node C:\Dev\App\node_modules\vite\bin\vite.js", StartTimeUtc = Start };
        Assert.Equal("C:/Dev/App", DevProcessRecognizer.Identify(vite).Project);
        AppGroup app = Assert.Single(AppGroupBuilder.Build(
            [vite, vite with { Id = 2, WorkingDirectory = @"C:\Dev\App\" }, vite with { Id = 3, WorkingDirectory = @"D:\Dev\App" }],
            new Dictionary<int, ProcessActivity>(), selfProcessId: 99));

        Assert.Equal("3 dev servers", app.Detail);
        Assert.Equal(2, app.Processes.Count);
        Assert.Equal(2, Assert.Single(app.Processes, group => group.Targets.Count == 2).Processes.Count);
    }

    [Fact]
    public void DescribesOnlyParentProcessesOldEnoughToBeRelated()
    {
        var child = new RunningProcessInfo { Id = 3, Name = "node", ParentId = 2, StartTimeUtc = Start.AddSeconds(2) };
        var parent = new RunningProcessInfo { Id = 2, Name = "node", ParentId = 1, StartTimeUtc = Start.AddSeconds(1) };
        var root = new RunningProcessInfo { Id = 1, Name = "claude", StartTimeUtc = Start };
        Dictionary<int, RunningProcessInfo> inventory = new() { [1] = root, [2] = parent };
        Assert.Equal("started by Claude Code → node", DevProcessRecognizer.DescribeParent(child, inventory));
        inventory[2] = parent with { StartTimeUtc = Start.AddSeconds(3) };
        Assert.Null(DevProcessRecognizer.DescribeParent(child, inventory));
    }

    [Fact]
    public void ActionsRequireKnownIdentityAndAnUnprotectedTarget()
    {
        var target = new RunningProcessInfo { Id = 123, Name = "ping", StartTimeUtc = Start };
        Assert.True(ProcessActionsService.IsEligible(target, 999));
        Assert.False(ProcessActionsService.IsEligible(target, 123));
        Assert.False(ProcessActionsService.IsEligible(target with { StartTimeUtc = null }, 999));
        Assert.False(ProcessActionsService.IsEligible(target with { IsProtected = true }, 999));
        Assert.False(ProcessActionsService.IsEligible(target with { Name = "svchost" }, 999));
    }

    [Fact]
    public void ActionsRoundTripOnAChildProcess()
    {
        string statePath = Path.Combine(Path.GetTempPath(), $"performance-tracker-test-{Guid.NewGuid():N}.json");
        var actions = new ProcessActionsService(statePath);
        using Process child = Process.Start(new ProcessStartInfo("ping.exe", "-n 120 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true
        })!;

        try
        {
            var target = new RunningProcessInfo { Id = child.Id, Name = child.ProcessName, StartTimeUtc = child.StartTime.ToUniversalTime() };
            Assert.False(actions.Apply(target with { StartTimeUtc = Start }, ProcessAction.Pause).Success);
            Assert.True(actions.Apply(target, ProcessAction.Pause).Success);
            var restarted = new ProcessActionsService(statePath);
            Assert.True(Assert.Single(restarted.ReadState()).Paused);
            child.Refresh();
            Assert.All(child.Threads.Cast<ProcessThread>(), thread =>
            {
                using (thread)
                {
                    Assert.Equal(System.Diagnostics.ThreadState.Wait, thread.ThreadState);
                    Assert.Equal(ThreadWaitReason.Suspended, thread.WaitReason);
                }
            });
            Assert.True(restarted.Apply(target, ProcessAction.Resume).Success);
            Assert.Empty(restarted.ReadState());

            child.Refresh();
            ProcessPriorityClass original = child.PriorityClass;
            Assert.True(actions.Apply(target, ProcessAction.LowerPriority).Success);
            child.Refresh();
            Assert.Equal(original == ProcessPriorityClass.BelowNormal ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal, child.PriorityClass);
            Assert.True(restarted.Apply(target, ProcessAction.RestorePriority).Success);
            child.Refresh();
            Assert.Equal(original, child.PriorityClass);

            ProcessActionResult trim = actions.Apply(target, ProcessAction.Trim);
            Assert.True(trim.Success);
            Assert.True(trim.BeforeBytes > 0);
            Assert.True(trim.AfterBytes >= 0);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                child.WaitForExit();
            }

            File.Delete(statePath);
            File.Delete(statePath + ".tmp");
        }
    }
}
