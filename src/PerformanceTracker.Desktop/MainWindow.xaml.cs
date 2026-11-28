using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PerformanceTracker.Core.Models;
using PerformanceTracker.Desktop.Services;

namespace PerformanceTracker.Desktop;

public partial class MainWindow : Window
{
    private readonly MonitoringState _monitoringState;
    private readonly DispatcherTimer _refreshTimer;
    private bool _allowClose;

    public MainWindow(
        MonitoringState monitoringState,
        DashboardView dashboardView, AppsView appsView, AgentsView agentsView, StartupAppsView startupAppsView,
        TimelineView timelineView, SettingsView settingsView)
    {
        InitializeComponent();
        DashboardTab.Content = dashboardView;
        dashboardView.ShowAppsRequested += (_, key) => ShowApp(key);
        dashboardView.ShowTimelineRequested += (_, _) => TimelineTab.IsSelected = true;
        AppsTab.Content = appsView;
        AgentsTab.Content = agentsView;
        StartupTab.Content = startupAppsView;
        TimelineTab.Content = timelineView;
        SettingsTab.Content = settingsView;

        _monitoringState = monitoringState;

        RefreshView();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += (_, _) => RefreshView();
        _refreshTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Ask Windows for a dark title bar to match the dark theme (Windows 10 20H1+ / Windows 11).
        const int DwmwaUseImmersiveDarkMode = 20;
        int enabled = 1;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void ShowAppFromAlert(string? key)
    {
        ShowFromTray();
        ShowApp(key);
    }

    private void ShowApp(string? key)
    {
        AppsTab.IsSelected = true;
        if (!string.IsNullOrEmpty(key) && AppsTab.Content is AppsView apps)
        {
            apps.Reveal(key);
        }
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

    private void RefreshView() => SessionHealthText.Text = BuildSessionHealthText(_monitoringState.GetSnapshot());

    private static string BuildSessionHealthText(MonitoringSnapshot snapshot)
    {
        TimeSpan sessionLength = DateTimeOffset.UtcNow - snapshot.SessionStartedAtUtc;
        if (snapshot.LatestSample is null)
        {
            return "Starting up...";
        }

        TimeSpan sinceReading = DateTimeOffset.UtcNow - snapshot.LatestSample.TimestampUtc;
        if (sinceReading > TimeSpan.FromSeconds(30))
        {
            return $"Not recording · last reading {FormatRelativeAge(snapshot.LatestSample.TimestampUtc)}";
        }

        PerformanceEvent? latestEvent = snapshot.LatestEvent;
        return latestEvent is { Severity: EventSeverity.Critical or EventSeverity.Warning } &&
            DateTimeOffset.UtcNow - latestEvent.EndedAtUtc < TimeSpan.FromMinutes(15)
                ? $"Slowdown {FormatRelativeAge(latestEvent.EndedAtUtc)}"
                : $"Watching for {FormatDuration(sessionLength)}";
    }

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
}
