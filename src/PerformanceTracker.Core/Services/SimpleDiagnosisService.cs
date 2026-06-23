using PerformanceTracker.Core.Configuration;
using PerformanceTracker.Core.Interfaces;
using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Core.Services;

public sealed class SimpleDiagnosisService : IDiagnosisService
{
    public string DescribeLikelyCause(MetricSample sample)
    {
        DiagnosisAnalyzer.DiagnosisInsight insight = DiagnosisAnalyzer.Analyze([sample], new CollectorSettings());
        return insight.HasSignal
            ? insight.LikelyCause
            : "Performance changed, but the current telemetry could not classify the cause confidently.";
    }
}

internal static class DiagnosisAnalyzer
{
    private const double NearThresholdMargin = 5;
    private const double CpuRiseTriggerPercent = 15;
    private const double MemoryRiseTriggerPercent = 8;
    private const double DiskRiseTriggerPercent = 20;
    private const double NetworkSpikeMultiplier = 3;
    private const double MinimumNetworkSpikeKbps = 1024;
    private const long LowAvailableMemoryMb = 2048;
    private const long VeryLowAvailableMemoryMb = 1024;
    private const double HeavyProcessMemoryMb = 1024;

    public static DiagnosisInsight Analyze(IReadOnlyList<MetricSample> recentSamples, CollectorSettings settings)
    {
        if (recentSamples.Count == 0)
        {
            return DiagnosisInsight.None;
        }

        MetricSample first = recentSamples[0];
        MetricSample latest = recentSamples[^1];

        MetricSeries cpu = MetricSeries.Create(recentSamples, sample => sample.CpuUsagePercent);
        MetricSeries disk = MetricSeries.Create(recentSamples, sample => sample.DiskActiveTimePercent);
        MetricSeries network = MetricSeries.CreateNetwork(recentSamples);

        int requiredCpuSamples = Math.Max(
            1,
            (int)Math.Ceiling((double)settings.CpuHighDurationSeconds / Math.Max(1, settings.SampleIntervalSeconds)));

        bool sustainedCpu = cpu.HasAtLeast(requiredCpuSamples)
            && cpu.TakeLast(requiredCpuSamples).All(value => value >= settings.CpuHighPercent);
        bool cpuNearHigh = cpu.HasData && cpu.Latest >= settings.CpuHighPercent - NearThresholdMargin;
        bool cpuRising = cpu.Count >= 2
            && cpu.Latest - cpu.First >= CpuRiseTriggerPercent
            && cpu.Latest >= settings.CpuHighPercent - (NearThresholdMargin + 2);

        bool memoryHigh = latest.MemoryUsedPercent >= settings.MemoryHighPercent;
        bool memoryNearHigh = latest.MemoryUsedPercent >= settings.MemoryHighPercent - NearThresholdMargin;
        bool memoryRising = recentSamples.Count >= 2
            && latest.MemoryUsedPercent - first.MemoryUsedPercent >= MemoryRiseTriggerPercent
            && memoryNearHigh;
        bool lowAvailableMemory = latest.AvailableMemoryMb > 0 && latest.AvailableMemoryMb <= LowAvailableMemoryMb;
        bool veryLowAvailableMemory = latest.AvailableMemoryMb > 0 && latest.AvailableMemoryMb <= VeryLowAvailableMemoryMb;

        bool diskHigh = disk.HasData && disk.Latest >= settings.DiskActiveHighPercent;
        bool diskNearHigh = disk.HasData && disk.Latest >= settings.DiskActiveHighPercent - NearThresholdMargin;
        bool diskRising = disk.Count >= 2
            && disk.Latest - disk.First >= DiskRiseTriggerPercent
            && disk.Latest >= settings.DiskActiveHighPercent - 10;

        double networkBaseline = network.Count > 1 ? network.TakeWithoutLast().DefaultIfEmpty(0).Average() : 0;
        bool networkSpike = network.HasData
            && network.Latest >= MinimumNetworkSpikeKbps
            && network.Latest >= Math.Max(MinimumNetworkSpikeKbps, networkBaseline * NetworkSpikeMultiplier);

        bool topProcessLooksHeavy = latest.TopProcessMemoryMb is double processMemory && processMemory >= HeavyProcessMemoryMb;
        string topProcessFragment = BuildTopProcessFragment(latest);

        if ((memoryHigh || (memoryRising && lowAvailableMemory)) && (diskHigh || diskRising || diskNearHigh))
        {
            EventSeverity severity = (memoryHigh && (diskHigh || veryLowAvailableMemory)) || latest.MemoryUsedPercent >= 95
                ? EventSeverity.Critical
                : EventSeverity.Warning;

            return new DiagnosisInsight(
                HasSignal: true,
                Title: "Memory pressure with disk contention",
                SummaryFragment: "memory use climbed while disk activity stayed elevated",
                LikelyCause: $"Rising memory use alongside busy storage suggests paging, asset streaming, or cache churn.{topProcessFragment}",
                Severity: severity);
        }

        if ((sustainedCpu || cpuRising) && (diskNearHigh || memoryRising || memoryHigh || networkSpike))
        {
            string title = networkSpike
                ? "CPU pressure during network burst"
                : diskNearHigh
                    ? "CPU and disk contention"
                    : "CPU pressure";

            string cause = networkSpike
                ? "CPU load rose together with a large network burst, which points to downloads, sync, streaming, or patching work competing with foreground activity."
                : diskNearHigh
                    ? "CPU load rose while storage stayed busy, which suggests background asset loads, scans, decompression, or paging work."
                    : "CPU load rose together with other pressure signals, which suggests the foreground workload is competing for shared system resources.";

            if (topProcessLooksHeavy)
            {
                cause = $"{cause}{topProcessFragment}";
            }

            return new DiagnosisInsight(
                HasSignal: true,
                Title: title,
                SummaryFragment: "CPU usage climbed with corroborating system pressure signals",
                LikelyCause: cause,
                Severity: cpu.Latest >= 95 || (cpuNearHigh && diskHigh) ? EventSeverity.Critical : EventSeverity.Warning);
        }

        if (sustainedCpu || (cpuRising && cpu.HasData && cpu.Latest >= settings.CpuHighPercent))
        {
            return new DiagnosisInsight(
                HasSignal: true,
                Title: "CPU pressure",
                SummaryFragment: sustainedCpu
                    ? "CPU usage stayed high across the recent window"
                    : "CPU usage climbed sharply across the recent window",
                LikelyCause: $"CPU demand built up across recent samples, which points to compute-heavy foreground work or background processing contention.{topProcessFragment}",
                Severity: cpu.Latest >= 95 ? EventSeverity.Critical : EventSeverity.Warning);
        }

        if (memoryHigh || (memoryRising && lowAvailableMemory))
        {
            return new DiagnosisInsight(
                HasSignal: true,
                Title: "RAM pressure",
                SummaryFragment: "memory use rose while available RAM tightened",
                LikelyCause: $"Available memory fell as utilization rose, which suggests the working set is outgrowing free RAM.{topProcessFragment}",
                Severity: memoryHigh && veryLowAvailableMemory ? EventSeverity.Critical : EventSeverity.Warning);
        }

        if (diskHigh || (diskRising && diskNearHigh))
        {
            return new DiagnosisInsight(
                HasSignal: true,
                Title: "Disk pressure",
                SummaryFragment: diskHigh
                    ? "storage stayed saturated in the latest samples"
                    : "storage activity ramped up quickly in the latest samples",
                LikelyCause: $"Storage activity became the dominant bottleneck, which points to scans, indexing, patching, or asset loads competing for disk time.{topProcessFragment}",
                Severity: disk.Latest >= 95 ? EventSeverity.Critical : EventSeverity.Warning);
        }

        if (networkSpike && (cpuNearHigh || diskNearHigh || memoryNearHigh))
        {
            return new DiagnosisInsight(
                HasSignal: true,
                Title: "Background transfer contention",
                SummaryFragment: "network traffic spiked while system pressure was already elevated",
                LikelyCause: "A large network burst coincided with other elevated resource signals, which suggests downloads, sync, or streaming activity is amplifying the slowdown.",
                Severity: EventSeverity.Warning);
        }

        return DiagnosisInsight.None;
    }

    private static string BuildTopProcessFragment(MetricSample sample)
    {
        if (string.IsNullOrWhiteSpace(sample.TopProcessName))
        {
            return string.Empty;
        }

        if (sample.TopProcessMemoryMb is not double processMemory || processMemory <= 0)
        {
            return $" {sample.TopProcessName} was the heaviest observed process.";
        }

        return $" {sample.TopProcessName} was the heaviest observed process at roughly {Math.Round(processMemory)} MB.";
    }

    internal sealed record DiagnosisInsight(
        bool HasSignal,
        string Title,
        string SummaryFragment,
        string LikelyCause,
        EventSeverity Severity)
    {
        public static DiagnosisInsight None { get; } = new(
            HasSignal: false,
            Title: string.Empty,
            SummaryFragment: string.Empty,
            LikelyCause: string.Empty,
            Severity: EventSeverity.Info);
    }

    private sealed class MetricSeries
    {
        private readonly double[] _values;

        private MetricSeries(double[] values)
        {
            _values = values;
        }

        public int Count => _values.Length;

        public bool HasData => _values.Length > 0;

        public double First => _values[0];

        public double Latest => _values[^1];

        public static MetricSeries Create(
            IReadOnlyList<MetricSample> recentSamples,
            Func<MetricSample, double?> selector)
        {
            return new MetricSeries(recentSamples
                .Select(selector)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .ToArray());
        }

        public static MetricSeries CreateNetwork(IReadOnlyList<MetricSample> recentSamples)
        {
            return new MetricSeries(recentSamples
                .Select(sample =>
                {
                    double receive = sample.NetworkReceiveKbps ?? 0;
                    double send = sample.NetworkSendKbps ?? 0;
                    return receive + send;
                })
                .ToArray());
        }

        public bool HasAtLeast(int count) => _values.Length >= count;

        public IEnumerable<double> TakeLast(int count)
        {
            int skip = Math.Max(0, _values.Length - count);
            return _values.Skip(skip);
        }

        public IEnumerable<double> TakeWithoutLast()
        {
            int count = Math.Max(0, _values.Length - 1);
            return _values.Take(count);
        }
    }
}
