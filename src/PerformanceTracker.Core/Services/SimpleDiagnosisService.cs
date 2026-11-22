using System.Globalization;
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
            : "The cause wasn't clear.";
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
    private const int SustainedDiskSamples = 3;

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

        string topProcessFragment = BuildTopProcessFragment(latest);
        string transfer = DescribeTransfer(latest);

        // "Busy" is not the same as "slow": the disk can be 100% active while still answering quickly.
        // Only blame the disk when it stayed busy and requests were actually waiting.
        bool diskSustained = disk.HasAtLeast(SustainedDiskSamples)
            && disk.TakeLast(SustainedDiskSamples).All(value => value >= settings.DiskActiveHighPercent);
        double? diskResponse = latest.DiskAverageResponseMs;
        double? diskQueue = latest.DiskQueueLength;
        bool diskSlow = diskResponse is null && diskQueue is null
            ? diskSustained
            : diskResponse >= settings.DiskResponseHighMs || diskQueue >= settings.DiskQueueHighThreshold;

        if ((memoryHigh || (memoryRising && lowAvailableMemory)) && (diskHigh || diskRising || diskNearHigh))
        {
            EventSeverity severity = (memoryHigh && (diskHigh || veryLowAvailableMemory)) || latest.MemoryUsedPercent >= 95
                ? EventSeverity.Critical
                : EventSeverity.Warning;

            return new DiagnosisInsight(
                HasSignal: true,
                Title: "Memory full, PC slowed down",
                SummaryFragment: "memory use climbed while disk activity stayed elevated",
                LikelyCause: $"Windows ran low on memory and started using the disk instead.{topProcessFragment}",
                Severity: severity);
        }

        if ((sustainedCpu || cpuRising) && (diskNearHigh || memoryRising || memoryHigh || networkSpike))
        {
            string title = networkSpike
                ? $"Processor busy during a big {transfer}"
                : diskNearHigh
                    ? "Processor and disk both busy"
                    : "Processor maxed out";

            string cause = networkSpike
                ? TransferCause(transfer)
                : diskNearHigh
                    ? "Often an update, a virus scan or something installing."
                    : memoryHigh
                        ? $"The processor was maxed out while memory was nearly full.{topProcessFragment}"
                        : "Something was using a lot of processing power.";

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
                Title: "Processor maxed out",
                SummaryFragment: sustainedCpu
                    ? "CPU usage stayed high across the recent window"
                    : "CPU usage climbed sharply across the recent window",
                LikelyCause: "Something was using a lot of processing power.",
                Severity: cpu.Latest >= 95 ? EventSeverity.Critical : EventSeverity.Warning);
        }

        if (memoryHigh || (memoryRising && lowAvailableMemory))
        {
            return new DiagnosisInsight(
                HasSignal: true,
                Title: "Memory almost full",
                SummaryFragment: "memory use rose while available RAM tightened",
                LikelyCause: $"Free memory ran low. Closing apps you aren't using helps.{topProcessFragment}",
                Severity: memoryHigh && veryLowAvailableMemory ? EventSeverity.Critical : EventSeverity.Warning);
        }

        if (diskSustained && diskSlow)
        {
            string cause = diskResponse is double response
                ? string.Create(CultureInfo.CurrentCulture, $"Your Windows drive took {response:0} ms to respond. Often an update, a virus scan, indexing or a big file copy.")
                : "Often an update, a virus scan, indexing or a big file copy.";
            return new DiagnosisInsight(
                HasSignal: true,
                Title: "Disk very busy",
                SummaryFragment: "storage stayed saturated and requests were waiting",
                LikelyCause: cause,
                Severity: disk.Latest >= 95 && (diskResponse >= settings.DiskResponseHighMs * 2 || diskQueue >= settings.DiskQueueHighThreshold * 2)
                    ? EventSeverity.Critical
                    : EventSeverity.Warning);
        }

        if (networkSpike && (cpuNearHigh || diskNearHigh || memoryNearHigh))
        {
            return new DiagnosisInsight(
                HasSignal: true,
                Title: $"Big {transfer} while PC was busy",
                SummaryFragment: "network traffic spiked while system pressure was already elevated",
                LikelyCause: $"A {transfer} added to the load. {TransferCause(transfer)}",
                Severity: EventSeverity.Warning);
        }

        return DiagnosisInsight.None;
    }

    /// <summary>"download", "upload" or "network transfer", depending on which direction dominated.</summary>
    internal static string DescribeTransfer(MetricSample sample)
    {
        double down = sample.NetworkReceiveKbps ?? 0;
        double up = sample.NetworkSendKbps ?? 0;
        return down >= up * 2 ? "download" : up >= down * 2 ? "upload" : "network transfer";
    }

    private static string TransferCause(string transfer) => transfer switch
    {
        "download" => "Often a game update, cloud sync or streaming.",
        "upload" => "Often a cloud backup, sync or a video call.",
        _ => "Often a cloud sync, streaming or a video call."
    };

    private static string BuildTopProcessFragment(MetricSample sample)
    {
        if (string.IsNullOrWhiteSpace(sample.TopProcessName))
        {
            return string.Empty;
        }

        if (sample.TopProcessMemoryMb is not double processMemory || processMemory <= 0)
        {
            return $" Biggest app: {sample.TopProcessName}.";
        }

        string size = processMemory >= 1024
            ? string.Create(CultureInfo.CurrentCulture, $"{processMemory / 1024:0.#} GB")
            : string.Create(CultureInfo.CurrentCulture, $"{Math.Round(processMemory)} MB");
        return $" Biggest app: {sample.TopProcessName} ({size}).";
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
