using PerformanceTracker.Core.Services;
using Xunit;

namespace PerformanceTracker.Tests;

public sealed class WslSummaryTests
{
    private const string Output = """
              1     3  0.0 /sbin/init
              2     0  0.0 [kthreadd]
           3659   120 49.2 /home/me/venv/bin/python -B scripts/species_dataset/florida_counties.py import --jobs 8
           3660   118 50.0 /home/me/venv/bin/python -B scripts/species_dataset/florida_counties.py import --jobs 8
           4100     1  0.3 node /srv/app/server.js
           4200     0 20.0 ps -eo pid=,times=,pcpu=,args=
        garbage line
        """;

    [Fact]
    public void ParsesRowsAndSkipsKernelThreadsAndPs()
    {
        IReadOnlyList<WslProcessRow> rows = WslSummary.Parse(Output);

        Assert.Equal([1, 3659, 3660, 4100], rows.Select(row => row.Pid));
        Assert.Equal(120, rows[1].CpuSeconds);
        Assert.StartsWith("/home/me/venv/bin/python -B scripts", rows[1].Command, StringComparison.Ordinal);
    }

    [Fact]
    public void GroupsWorkersOfOneScriptAndMeasuresCpuBetweenReadings()
    {
        IReadOnlyList<WslProcessRow> rows = WslSummary.Parse(Output);
        Dictionary<int, long> before = new() { [1] = 3, [3659] = 115, [3660] = 113, [4100] = 1 };

        // 10 s apart on a 16-processor PC: the two workers used 10 CPU-seconds = 1 core = 6.25% of the PC.
        WslActivity busiest = Assert.Single(WslSummary.Summarize(rows, before, elapsedSeconds: 10, hostProcessors: 16));

        Assert.Equal("florida_counties.py", busiest.Name);
        Assert.Equal(2, busiest.ProcessCount);
        Assert.Equal(6.3, busiest.CpuPercent, 1);
        Assert.Equal("florida_counties.py (2 processes)", WslSummary.Label(busiest));
    }

    [Fact]
    public void FirstReadingFallsBackToLifetimeAverage()
    {
        IReadOnlyList<WslActivity> activities = WslSummary.Summarize(WslSummary.Parse(Output), null, 0, hostProcessors: 16);

        Assert.Equal("florida_counties.py", activities[0].Name);
        Assert.Equal(6.2, activities[0].CpuPercent, 1);
        Assert.StartsWith("Inside Linux: florida_counties.py (2 processes)", WslSummary.Describe(activities), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/usr/bin/python3 -m http.server", "http.server")]
    [InlineData("/usr/lib/postgresql/16/bin/postgres -D /var/lib", "postgres")]
    [InlineData("node /srv/app/server.js", "server.js")]
    [InlineData("node --require /tmp/suppress-warnings.cjs scripts/preview-android-ui.mjs", "preview-android-ui.mjs")]
    [InlineData("node --import tsx/esm -r ./env.cjs src/main.ts", "main.ts")]
    public void NamesCommandsReadably(string command, string expected)
    {
        Assert.Equal(expected, WslSummary.Name(command));
    }
}
