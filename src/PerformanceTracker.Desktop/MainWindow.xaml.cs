using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Options;
using PerformanceTracker.Core.Configuration;
using PerformanceTracker.Desktop.Services;

namespace PerformanceTracker.Desktop;

public partial class MainWindow : Window
{
    private readonly MonitoringState _monitoringState;
    private readonly DispatcherTimer _refreshTimer;
    private bool _allowClose;

    public MainWindow(
        MonitoringState monitoringState,
        IOptions<CollectorSettings> collectorSettings,
        IOptions<RetentionSettings> retentionSettings)
    {
        InitializeComponent();

        _monitoringState = monitoringState;
        CollectorConfigText.Text =
            $"Sample interval: {collectorSettings.Value.SampleIntervalSeconds}s, CPU threshold: {collectorSettings.Value.CpuHighPercent}%, RAM threshold: {collectorSettings.Value.MemoryHighPercent}%, cooldown: {collectorSettings.Value.EventCooldownSeconds}s.";
        RetentionConfigText.Text =
            $"Raw samples: {retentionSettings.Value.RawSampleRetentionHours}h, event retention: {retentionSettings.Value.EventRetentionDays}d, daily summaries: {retentionSettings.Value.DailySummaryRetentionDays}d.";

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
        if (snapshot.LatestSample is null)
        {
            StatusText.Text = "Collector is waiting for the first completed sample.";
            return;
        }

        StatusText.Text = snapshot.LatestEvent is null
            ? "Monitoring normally."
            : $"Watch: {snapshot.LatestEvent.Summary}";

        UptimeText.Text = $"Uptime: {snapshot.LatestSample.Uptime:g}";
        CpuText.Text = $"CPU: {FormatPercent(snapshot.LatestSample.CpuUsagePercent)}";
        MemoryText.Text = $"Memory: {snapshot.LatestSample.MemoryUsedPercent:F1}% used, {snapshot.LatestSample.AvailableMemoryMb} MB available";
        DiskText.Text = snapshot.LatestSample.DiskActiveTimePercent is null
            ? "Disk: unavailable in scaffold"
            : $"Disk active time: {snapshot.LatestSample.DiskActiveTimePercent:F1}%";
        TopProcessText.Text = snapshot.LatestSample.TopProcessName is null
            ? "Top process: unavailable"
            : $"Top process: {snapshot.LatestSample.TopProcessName} ({FormatMegabytes(snapshot.LatestSample.TopProcessMemoryMb)})";
        ForegroundAppText.Text = $"Foreground app: {snapshot.ForegroundApp ?? "unavailable"}";
        LastEventText.Text = snapshot.LatestEvent is null
            ? "No events detected yet."
            : $"{snapshot.LatestEvent.Summary} {snapshot.LatestEvent.LikelyCause}";
    }

    private static string FormatPercent(double? value) => value is null ? "warming up..." : $"{value:F1}%";

    private static string FormatMegabytes(double? value) => value is null ? "unknown MB" : $"{value:F1} MB";
}
