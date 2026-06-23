using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Options;
using PerformanceTracker.Core.Configuration;
using PerformanceTracker.Core.Models;
using PerformanceTracker.Desktop.Services;
using Brush = System.Windows.Media.Brush;
using BrushConverter = System.Windows.Media.BrushConverter;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace PerformanceTracker.Desktop;

public partial class MainWindow : Window
{
    private static readonly Brush CriticalEventBrush = CreateBrush("#B42318");
    private static readonly Brush WarningEventBrush = CreateBrush("#B54708");
    private static readonly Brush InfoEventBrush = CreateBrush("#1570EF");

    private readonly MonitoringState _monitoringState;
    private readonly CollectorSettings _collectorSettings;
    private readonly RetentionSettings _retentionSettings;
    private readonly DispatcherTimer _refreshTimer;
    private bool _allowClose;

    public MainWindow(
        MonitoringState monitoringState,
        IOptions<CollectorSettings> collectorSettings,
        IOptions<RetentionSettings> retentionSettings)
    {
        InitializeComponent();

        _monitoringState = monitoringState;
        _collectorSettings = collectorSettings.Value;
        _retentionSettings = retentionSettings.Value;

        RefreshSettingsView();
        RefreshView();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += (_, _) => RefreshView();
        _refreshTimer.Start();
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void PrepareForExit()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void RefreshView()
    {
        var snapshot = _monitoringState.GetSnapshot();
        SessionHealthText.Text = BuildSessionHealthText(snapshot);
        SessionStatsText.Text = $"Session length: {FormatDuration(DateTimeOffset.UtcNow - snapshot.SessionStartedAtUtc)}";
        MonitoringCadenceText.Text = $"Sampling every {_collectorSettings.SampleIntervalSeconds}s, retaining up to {snapshot.RecentSamples.Count} recent samples in memory.";
        EventRateText.Text = snapshot.RecentEvents.Count == 0
            ? "Events this session: none yet."
            : $"Events this session: {snapshot.RecentEvents.Count} (latest {FormatRelativeAge(snapshot.RecentEvents[^1].EndedAtUtc)}).";

        if (snapshot.LatestSample is null)
        {
            ApplyWaitingState(snapshot);
            return;
        }

        MetricSample latestSample = snapshot.LatestSample;
        IReadOnlyList<MetricSample> recentSamples = snapshot.RecentSamples;
        PerformanceEvent[] recentEvents = snapshot.RecentEvents
            .OrderByDescending(static performanceEvent => performanceEvent.EndedAtUtc)
            .ToArray();

        StatusText.Text = snapshot.LatestEvent is null
            ? "Monitoring normally in the latest sample window."
            : $"Recent {snapshot.LatestEvent.Severity.ToString().ToLowerInvariant()} event: {snapshot.LatestEvent.Summary}";
        UptimeText.Text = $"Uptime: {latestSample.Uptime:g}";
        LastUpdatedText.Text = $"Last sample: {FormatTimestamp(latestSample.TimestampUtc)} ({FormatRelativeAge(latestSample.TimestampUtc)}).";

        CpuText.Text = BuildCpuSummary(latestSample, recentSamples);
        MemoryText.Text = BuildMemorySummary(latestSample, recentSamples);
        DiskText.Text = BuildDiskSummary(latestSample, recentSamples);
        NetworkText.Text = BuildNetworkSummary(latestSample, recentSamples);
        SampleTrendText.Text = BuildTrendSummary(recentSamples);

        ForegroundAppText.Text = $"Foreground app: {snapshot.ForegroundApp ?? "unavailable"}";
        TopProcessText.Text = latestSample.TopProcessName is null
            ? "Top process: unavailable in current sample."
            : $"Top process: {latestSample.TopProcessName} using {FormatMegabytes(latestSample.TopProcessMemoryMb)}.";
        TimelineHintText.Text = recentEvents.Length == 0
            ? "No event detections yet in this session."
            : $"Showing the {Math.Min(4, recentEvents.Length)} most recent detections below. Open Event Timeline for the full in-session history.";

        CoverageText.Text = BuildCoverageSummary(latestSample);
        OptionalMetricsText.Text = BuildOptionalTelemetrySummary(latestSample);

        UpdateEventFocus(snapshot, latestSample);
        UpdateTimeline(snapshot, recentEvents);
    }

    private void RefreshSettingsView()
    {
        CollectionConfigText.Text =
            $"Collect a sample every {_collectorSettings.SampleIntervalSeconds}s. The desktop UI reads foreground app context and the top {_collectorSettings.TopProcessLimit} process entries when that data is available from the collector.";
        ThresholdConfigText.Text =
            $"Current detector thresholds: CPU {_collectorSettings.CpuHighPercent:F0}% for {_collectorSettings.CpuHighDurationSeconds}s, memory {_collectorSettings.MemoryHighPercent:F0}%, disk active {_collectorSettings.DiskActiveHighPercent:F0}%.";
        WindowConfigText.Text =
            $"The live diagnosis window spans {_collectorSettings.EventContextWindowSeconds}s of recent samples, with a {_collectorSettings.EventCooldownSeconds}s cooldown between persisted events.";
        RetentionConfigText.Text =
            $"Raw samples: {_retentionSettings.RawSampleRetentionHours}h. Event rows: {_retentionSettings.EventRetentionDays}d. Daily summaries: {_retentionSettings.DailySummaryRetentionDays}d.";

        string overridesPath = Path.Combine(AppContext.BaseDirectory, "appsettings.Development.json");
        SettingsStatusText.Text = File.Exists(overridesPath)
            ? "A local development override file is present. Startup configuration may differ from tracked defaults on this machine."
            : "No local development override file is present. The app is using the tracked defaults shipped with the desktop build.";
        SettingsPathText.Text = $"Override file path: {overridesPath}";
    }

    private void ApplyWaitingState(MonitoringSnapshot snapshot)
    {
        StatusText.Text = "Collector is waiting for the first completed sample.";
        UptimeText.Text = "Uptime: --";
        LastUpdatedText.Text = "Last sample: waiting for first reading.";
        CpuText.Text = "CPU telemetry will appear after the first sample.";
        MemoryText.Text = "Memory telemetry will appear after the first sample.";
        DiskText.Text = "Disk telemetry will appear after the first sample.";
        NetworkText.Text = "Network telemetry will appear after the first sample.";
        SampleTrendText.Text = "Trend analysis requires at least one completed sample.";
        ForegroundAppText.Text = "Foreground app: waiting for first sample.";
        TopProcessText.Text = "Top process: waiting for first sample.";
        TimelineHintText.Text = "The timeline tab keeps an in-session history once detections start arriving.";
        CoverageText.Text = "Telemetry coverage will be summarized once the collector produces its first sample.";
        OptionalMetricsText.Text = "Additional GPU or gaming telemetry will appear here automatically when those optional fields are present.";
        LatestEventSeverityText.Text = "No event";
        LastEventText.Text = "No events detected yet.";
        LastEventCauseText.Text = "Event summaries and likely causes will appear here after the first detection.";
        LatestEventMetaText.Text = $"Session active for {FormatDuration(DateTimeOffset.UtcNow - snapshot.SessionStartedAtUtc)}.";
        TriggerMetricsText.Text = "Trigger metrics: unavailable until the first sample is collected.";
        TimelineSummaryText.Text = "No session events yet.";
        DashboardTimelineList.ItemsSource = Array.Empty<TimelineEventItem>();
        TimelineList.ItemsSource = Array.Empty<TimelineEventItem>();
        TimelineEmptyText.Visibility = Visibility.Visible;
    }

    private void UpdateEventFocus(MonitoringSnapshot snapshot, MetricSample latestSample)
    {
        PerformanceEvent? latestEvent = snapshot.LatestEvent;
        if (latestEvent is null)
        {
            LatestEventSeverityText.Text = "No event";
            LatestEventSeverityText.Foreground = InfoEventBrush;
            LastEventText.Text = "No event detections have been recorded in this session.";
            LastEventCauseText.Text = "When a detector fires, its summary and likely cause will appear here.";
            LatestEventMetaText.Text = $"Current foreground app: {snapshot.ForegroundApp ?? "unavailable"}.";
            TriggerMetricsText.Text = $"Current metrics: {BuildTriggerMetricSummary(latestSample)}";
            return;
        }

        LatestEventSeverityText.Text = $"{latestEvent.Severity} event";
        LatestEventSeverityText.Foreground = GetSeverityBrush(latestEvent.Severity);
        LastEventText.Text = latestEvent.Summary;
        LastEventCauseText.Text = string.IsNullOrWhiteSpace(latestEvent.LikelyCause)
            ? "No likely cause was provided for this event."
            : latestEvent.LikelyCause;
        LatestEventMetaText.Text =
            $"{FormatTimestamp(latestEvent.EndedAtUtc)} ({FormatRelativeAge(latestEvent.EndedAtUtc)})"
            + $" - foreground app: {latestEvent.ForegroundApp ?? snapshot.ForegroundApp ?? "unavailable"}.";
        TriggerMetricsText.Text = $"Trigger metrics: {BuildTriggerMetricSummary(latestEvent.TriggerSample)}";
    }

    private void UpdateTimeline(MonitoringSnapshot snapshot, PerformanceEvent[] recentEvents)
    {
        DashboardTimelineList.ItemsSource = recentEvents
            .Take(4)
            .Select(CreateTimelineItem)
            .ToArray();

        TimelineList.ItemsSource = recentEvents
            .Select(CreateTimelineItem)
            .ToArray();

        TimelineEmptyText.Visibility = recentEvents.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        TimelineSummaryText.Text = recentEvents.Length == 0
            ? $"No events have been detected since this session started at {FormatTimestamp(snapshot.SessionStartedAtUtc)}."
            : $"{recentEvents.Length} in-session events across {snapshot.SampleCount} collected samples since {FormatTimestamp(snapshot.SessionStartedAtUtc)}.";
    }

    private static string BuildCpuSummary(MetricSample latestSample, IReadOnlyList<MetricSample> recentSamples)
    {
        double? peak = GetMax(recentSamples.Select(sample => sample.CpuUsagePercent));
        double? average = GetAverage(recentSamples.Select(sample => sample.CpuUsagePercent));

        return $"Current {FormatPercent(latestSample.CpuUsagePercent)}"
            + $", average {FormatPercent(average)}"
            + $", peak {FormatPercent(peak)}.";
    }

    private static string BuildMemorySummary(MetricSample latestSample, IReadOnlyList<MetricSample> recentSamples)
    {
        double peak = recentSamples.Count == 0
            ? latestSample.MemoryUsedPercent
            : recentSamples.Max(sample => sample.MemoryUsedPercent);

        return $"{latestSample.MemoryUsedPercent:F1}% used, {latestSample.AvailableMemoryMb} MB free, {peak:F1}% peak in the recent window.";
    }

    private static string BuildDiskSummary(MetricSample latestSample, IReadOnlyList<MetricSample> recentSamples)
    {
        double? peak = GetMax(recentSamples.Select(sample => sample.DiskActiveTimePercent));
        return latestSample.DiskActiveTimePercent is null
            ? "Disk activity telemetry is not available from the current collector."
            : $"Current {latestSample.DiskActiveTimePercent:F1}% active time, peak {FormatPercent(peak)} in the recent window.";
    }

    private static string BuildNetworkSummary(MetricSample latestSample, IReadOnlyList<MetricSample> recentSamples)
    {
        double? peakReceive = GetMax(recentSamples.Select(sample => sample.NetworkReceiveKbps));
        double? peakSend = GetMax(recentSamples.Select(sample => sample.NetworkSendKbps));

        if (latestSample.NetworkReceiveKbps is null && latestSample.NetworkSendKbps is null)
        {
            return "Network telemetry is not available from the current collector.";
        }

        return $"{FormatRate(latestSample.NetworkReceiveKbps)} down, {FormatRate(latestSample.NetworkSendKbps)} up. "
            + $"Recent peaks: {FormatRate(peakReceive)} down, {FormatRate(peakSend)} up.";
    }

    private static string BuildTrendSummary(IReadOnlyList<MetricSample> recentSamples)
    {
        if (recentSamples.Count == 0)
        {
            return "No recent samples are available yet.";
        }

        MetricSample oldestSample = recentSamples[0];
        MetricSample latestSample = recentSamples[^1];

        List<string> peaks =
        [
            $"CPU {FormatPercent(GetMax(recentSamples.Select(sample => sample.CpuUsagePercent)))}",
            $"memory {recentSamples.Max(sample => sample.MemoryUsedPercent):F1}%"
        ];

        double? diskPeak = GetMax(recentSamples.Select(sample => sample.DiskActiveTimePercent));
        if (diskPeak is not null)
        {
            peaks.Add($"disk {diskPeak:F1}%");
        }

        double? networkPeak = GetMax(recentSamples.Select(sample => sample.NetworkReceiveKbps));
        if (networkPeak is not null)
        {
            peaks.Add($"download {networkPeak:F0} Kbps");
        }

        return $"Window covers {recentSamples.Count} samples from {FormatTimestamp(oldestSample.TimestampUtc)} to {FormatTimestamp(latestSample.TimestampUtc)}. "
            + $"Recent high-water marks: {string.Join(", ", peaks)}.";
    }

    private static string BuildCoverageSummary(MetricSample latestSample)
    {
        List<string> available =
        [
            "memory",
            "uptime"
        ];
        List<string> missing = [];

        AddCoverage(latestSample.CpuUsagePercent is not null, "CPU", available, missing);
        AddCoverage(latestSample.DiskActiveTimePercent is not null, "disk", available, missing);
        AddCoverage(latestSample.NetworkReceiveKbps is not null || latestSample.NetworkSendKbps is not null, "network", available, missing);
        AddCoverage(!string.IsNullOrWhiteSpace(latestSample.TopProcessName), "top process", available, missing);

        string availableText = $"Available now: {string.Join(", ", available)}.";
        return missing.Count == 0
            ? availableText + " No expected desktop telemetry feeds are currently missing."
            : availableText + $" Waiting on: {string.Join(", ", missing)}.";
    }

    private static string BuildOptionalTelemetrySummary(MetricSample latestSample)
    {
        List<string> optionalMetrics = [];
        AddOptionalMetric(optionalMetrics, latestSample, "GPU load", "%", "GpuUsagePercent", "GpuUtilizationPercent");
        AddOptionalMetric(optionalMetrics, latestSample, "GPU memory", " MB", "GpuMemoryUsedMb", "VramUsedMb");
        AddOptionalMetric(optionalMetrics, latestSample, "FPS", string.Empty, "FramesPerSecond", "Fps");
        AddOptionalMetric(optionalMetrics, latestSample, "Frame time", " ms", "FrameTimeMs", "AverageFrameTimeMs");
        AddOptionalMetric(optionalMetrics, latestSample, "Input latency", " ms", "InputLatencyMs");
        AddOptionalMetric(optionalMetrics, latestSample, "Render latency", " ms", "RenderLatencyMs");
        AddOptionalMetric(optionalMetrics, latestSample, "GPU temperature", " C", "GpuTemperatureC");
        AddOptionalMetric(optionalMetrics, latestSample, "CPU temperature", " C", "CpuTemperatureC");

        return optionalMetrics.Count == 0
            ? "Additional GPU or gaming telemetry is not present in the current sample contract. This panel will populate automatically when optional fields land."
            : $"Optional telemetry detected: {string.Join(" | ", optionalMetrics)}.";
    }

    private static TimelineEventItem CreateTimelineItem(PerformanceEvent performanceEvent)
    {
        return new TimelineEventItem(
            FormatTimestamp(performanceEvent.EndedAtUtc),
            performanceEvent.Severity.ToString().ToUpperInvariant(),
            performanceEvent.Summary,
            string.IsNullOrWhiteSpace(performanceEvent.LikelyCause)
                ? "No likely cause was provided."
                : performanceEvent.LikelyCause,
            $"App: {performanceEvent.ForegroundApp ?? "unavailable"}",
            BuildTriggerMetricSummary(performanceEvent.TriggerSample),
            GetSeverityBrush(performanceEvent.Severity));
    }

    private static string BuildTriggerMetricSummary(MetricSample sample)
    {
        List<string> parts =
        [
            $"CPU {FormatPercent(sample.CpuUsagePercent)}",
            $"memory {sample.MemoryUsedPercent:F1}%"
        ];

        if (sample.DiskActiveTimePercent is not null)
        {
            parts.Add($"disk {sample.DiskActiveTimePercent:F1}%");
        }

        if (sample.NetworkReceiveKbps is not null || sample.NetworkSendKbps is not null)
        {
            parts.Add($"{FormatRate(sample.NetworkReceiveKbps)} down / {FormatRate(sample.NetworkSendKbps)} up");
        }

        if (!string.IsNullOrWhiteSpace(sample.TopProcessName))
        {
            parts.Add($"top process {sample.TopProcessName}");
        }

        return string.Join(", ", parts);
    }

    private static void AddCoverage(bool isAvailable, string label, List<string> available, List<string> missing)
    {
        if (isAvailable)
        {
            available.Add(label);
            return;
        }

        missing.Add(label);
    }

    private static void AddOptionalMetric(List<string> metrics, MetricSample sample, string label, string suffix, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (!TryGetOptionalPropertyValue(sample, propertyName, out object? value) || value is null)
            {
                continue;
            }

            metrics.Add($"{label} {FormatOptionalValue(value, suffix)}");
            return;
        }
    }

    private static bool TryGetOptionalPropertyValue(MetricSample sample, string propertyName, out object? value)
    {
        PropertyInfo? property = sample.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        value = property?.GetValue(sample);
        return property is not null;
    }

    private static string FormatOptionalValue(object value, string suffix)
    {
        return value switch
        {
            double doubleValue => $"{doubleValue:F1}{suffix}",
            float floatValue => $"{floatValue:F1}{suffix}",
            decimal decimalValue => $"{decimalValue:F1}{suffix}",
            int intValue => $"{intValue}{suffix}",
            long longValue => $"{longValue}{suffix}",
            TimeSpan timeSpan => $"{timeSpan.TotalMilliseconds:F1} ms",
            _ => $"{value}{suffix}"
        };
    }

    private static string BuildSessionHealthText(MonitoringSnapshot snapshot)
    {
        TimeSpan sessionLength = DateTimeOffset.UtcNow - snapshot.SessionStartedAtUtc;
        if (snapshot.LatestSample is null)
        {
            return $"Starting up - {FormatDuration(sessionLength)}";
        }

        PerformanceEvent? latestEvent = snapshot.LatestEvent;
        return latestEvent?.Severity switch
        {
            EventSeverity.Critical => $"Critical event seen {FormatRelativeAge(latestEvent.EndedAtUtc)}",
            EventSeverity.Warning => $"Warning seen {FormatRelativeAge(latestEvent.EndedAtUtc)}",
            EventSeverity.Info => $"Info event seen {FormatRelativeAge(latestEvent.EndedAtUtc)}",
            _ => $"Stable - {snapshot.SampleCount} samples in {FormatDuration(sessionLength)}"
        };
    }

    private static double? GetAverage(IEnumerable<double?> values)
    {
        List<double> materialized = [];
        foreach (double? value in values)
        {
            if (value is double actualValue)
            {
                materialized.Add(actualValue);
            }
        }

        return materialized.Count == 0 ? null : materialized.Average();
    }

    private static double? GetMax(IEnumerable<double?> values)
    {
        List<double> materialized = [];
        foreach (double? value in values)
        {
            if (value is double actualValue)
            {
                materialized.Add(actualValue);
            }
        }

        return materialized.Count == 0 ? null : materialized.Max();
    }

    private static string FormatPercent(double? value) => value is null ? "unavailable" : $"{value:F1}%";

    private static string FormatMegabytes(double? value) => value is null ? "unknown MB" : $"{value:F1} MB";

    private static string FormatRate(double? value) => value is null ? "unavailable" : $"{value:F0} Kbps";

    private static string FormatRelativeAge(DateTimeOffset timestampUtc)
    {
        TimeSpan age = DateTimeOffset.UtcNow - timestampUtc;
        if (age.TotalHours >= 1)
        {
            return $"{(int)age.TotalHours}h {age.Minutes}m ago";
        }

        if (age.TotalMinutes >= 1)
        {
            return $"{(int)age.TotalMinutes}m {age.Seconds}s ago";
        }

        return $"{Math.Max(age.Seconds, 0)}s ago";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        if (duration.TotalMinutes >= 1)
        {
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        }

        return $"{Math.Max(duration.Seconds, 0)}s";
    }

    private static string FormatTimestamp(DateTimeOffset timestampUtc) =>
        timestampUtc.ToLocalTime().ToString("MMM d, h:mm:ss tt", CultureInfo.CurrentCulture);

    private static Brush GetSeverityBrush(EventSeverity severity) =>
        severity switch
        {
            EventSeverity.Critical => CriticalEventBrush,
            EventSeverity.Warning => WarningEventBrush,
            _ => InfoEventBrush
        };

    private static SolidColorBrush CreateBrush(string colorHex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(colorHex)!;
        brush.Freeze();
        return brush;
    }

    private sealed record TimelineEventItem(
        string OccurredAtText,
        string SeverityText,
        string Summary,
        string Cause,
        string AppText,
        string MetricSummary,
        Brush SeverityBrush);
}

