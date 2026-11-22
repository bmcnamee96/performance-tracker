using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Options;
using PerformanceTracker.Core.Configuration;
using PerformanceTracker.Core.Models;
using PerformanceTracker.Core.Services;
using PerformanceTracker.Desktop.Services;
using PerformanceTracker.Infrastructure.Storage;
using Brush = System.Windows.Media.Brush;
using BrushConverter = System.Windows.Media.BrushConverter;
using Button = System.Windows.Controls.Button;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using UserControl = System.Windows.Controls.UserControl;

namespace PerformanceTracker.Desktop;

/// <summary>Plain-language overview: live tiles, the biggest memory users and recent slowdowns.</summary>
public partial class DashboardView : UserControl
{
    private const int TopAppCount = 6;
    private static readonly TimeSpan AppsRefreshInterval = TimeSpan.FromSeconds(5);

    private static readonly Brush CpuBrush = CreateBrush("#53B1FD");
    private static readonly Brush MemoryBrush = CreateBrush("#3FD5E6");
    private static readonly Brush DiskBrush = CreateBrush("#B692F6");
    private static readonly Brush NetworkBrush = CreateBrush("#8BE5C1");
    private static readonly Brush GpuBrush = CreateBrush("#FDB022");
    private static readonly Brush CriticalBrush = CreateBrush("#F97066");
    private static readonly Brush WarningBrush = CreateBrush("#FDB022");
    private static readonly Brush InfoBrush = CreateBrush("#53B1FD");
    private static readonly Brush FooterBrush = CreateBrush("#6B7F94");

    private static readonly TimeSpan SavedEventsRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly MonitoringState _state;
    private readonly SqliteSampleRepository _repository;
    private IReadOnlyList<PerformanceEvent> _savedEvents = [];
    private DateTime _nextSavedEventsRead = DateTime.MinValue;
    private bool _readingSavedEvents;
    private readonly ProcessOverviewService _overview;
    private readonly CollectorSettings _settings;
    private readonly DispatcherTimer _timer;
    private readonly DashboardTileViewModel _cpu = new("Processor", CpuBrush);
    private readonly DashboardTileViewModel _memory = new("Memory", MemoryBrush);
    private readonly DashboardTileViewModel _disk = new("Windows drive", DiskBrush);
    private readonly DashboardTileViewModel _network = new("Network", NetworkBrush);
    private readonly DashboardTileViewModel _gpu = new("Graphics", GpuBrush) { Visibility = Visibility.Collapsed };
    private DateTime _nextAppsRead = DateTime.MinValue;
    private bool _readingApps;

    public DashboardView(MonitoringState state, ProcessOverviewService overview, IOptions<CollectorSettings> settings,
        SqliteSampleRepository repository)
    {
        InitializeComponent();
        _repository = repository;
        _state = state;
        _overview = overview;
        _settings = settings.Value;
        Tiles.ItemsSource = new[] { _cpu, _memory, _disk, _network, _gpu };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _nextAppsRead = DateTime.MinValue;
                Refresh();
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        };
    }

    /// <summary>Raised when the user wants the Apps &amp; Memory tab, optionally with one app expanded.</summary>
    public event EventHandler<string?>? ShowAppsRequested;

    /// <summary>Raised when the user wants the full event history.</summary>
    public event EventHandler? ShowTimelineRequested;

    private void Refresh()
    {
        MonitoringSnapshot snapshot = _state.GetSnapshot();
        if (DateTime.UtcNow >= _nextAppsRead)
        {
            _ = RefreshAppsAsync();
        }

        if (DateTime.UtcNow >= _nextSavedEventsRead)
        {
            _ = RefreshSavedEventsAsync();
        }

        UpdateEvents(snapshot);
        TimeSpan window = TimeSpan.FromSeconds(Math.Max(1, _settings.SampleIntervalSeconds) * Math.Max(1, snapshot.RecentSamples.Count));
        WindowCaption.Text = snapshot.RecentSamples.Count > 1 ? $"Charts show the last {FormatDuration(window)}" : string.Empty;

        if (snapshot.LatestSample is not MetricSample latest)
        {
            FooterText.Text = $"Monitoring started {FormatDuration(DateTimeOffset.UtcNow - snapshot.SessionStartedAtUtc)} ago. Waiting for the first reading.";
            return;
        }

        IReadOnlyList<MetricSample> recent = snapshot.RecentSamples;
        UpdateTiles(latest, recent);
        bool stale = DateTimeOffset.UtcNow - latest.TimestampUtc > TimeSpan.FromSeconds(30);
        FooterText.Text = stale
            ? $"Readings have stopped. Last reading {FormatAge(latest.TimestampUtc)}. Restart the app from the tray icon if this lasts."
            : $"PC on for {FormatDuration(latest.Uptime)}  ·  checking every {_settings.SampleIntervalSeconds}s  ·  last reading {FormatAge(latest.TimestampUtc)}";
        FooterText.Foreground = stale ? WarningBrush : FooterBrush;
    }

    private void UpdateTiles(MetricSample latest, IReadOnlyList<MetricSample> recent)
    {
        double? cpuAverage = Average(recent.Select(sample => sample.CpuUsagePercent));
        double? cpuPeak = Max(recent.Select(sample => sample.CpuUsagePercent));
        _cpu.Value = Percent(latest.CpuUsagePercent);
        _cpu.Detail = $"Average {Percent(cpuAverage)}  ·  peak {Percent(cpuPeak)}";
        _cpu.ToolTip = "How busy your processor is. Short spikes are normal; staying near 100% makes everything feel slow.";
        _cpu.History = recent.Select(sample => sample.CpuUsagePercent).ToArray();

        long totalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        long freeBytes = latest.AvailableMemoryMb * 1024 * 1024;
        _memory.Value = $"{latest.MemoryUsedPercent:F0}%";
        _memory.Detail = totalBytes > 0
            ? $"{AppRowViewModel.FormatBytes(Math.Max(0, totalBytes - freeBytes))} of {AppRowViewModel.FormatBytes(totalBytes)} in use"
            : $"{AppRowViewModel.FormatBytes(freeBytes)} free";
        _memory.ToolTip = "How full your RAM is. Above about 90%, Windows starts using the disk instead and things slow down.";
        _memory.History = recent.Select(sample => (double?)sample.MemoryUsedPercent).ToArray();

        _disk.Value = Percent(latest.DiskActiveTimePercent);
        _disk.Detail = latest.DiskReadMbPerSec is not null || latest.DiskWriteMbPerSec is not null
            ? $"Reading {Rate(latest.DiskReadMbPerSec)}  ·  writing {Rate(latest.DiskWriteMbPerSec)}"
            : $"Peak {Percent(Max(recent.Select(sample => sample.DiskActiveTimePercent)))}";
        _disk.ToolTip = "How much of the time the drive Windows is installed on is busy reading or writing. Other drives aren't measured.";
        _disk.History = recent.Select(sample => sample.DiskActiveTimePercent).ToArray();

        double? down = latest.NetworkReceiveKbps;
        double peakDown = Max(recent.Select(sample => sample.NetworkReceiveKbps)) ?? 0;
        _network.Value = down is null ? "--" : NetworkRate(down.Value);
        _network.Detail = latest.NetworkSendKbps is double up ? $"Down  ·  {NetworkRate(up)} up" : "Down";
        _network.ToolTip = "Data coming in (the big number and the chart) and going out over your internet connection.";
        _network.History = recent.Select(sample => sample.NetworkReceiveKbps).ToArray();
        _network.Maximum = Math.Max(peakDown * 1.15, 100);

        _gpu.Visibility = latest.GpuUsagePercent is null ? Visibility.Collapsed : Visibility.Visible;
        if (latest.GpuUsagePercent is not null)
        {
            _gpu.Value = Percent(latest.GpuUsagePercent);
            _gpu.Detail = latest.GpuDedicatedMemoryMb is double vram
                ? $"{AppRowViewModel.FormatBytes((long)(vram * 1024 * 1024))} video memory"
                : $"Peak {Percent(Max(recent.Select(sample => sample.GpuUsagePercent)))}";
            _gpu.ToolTip = "How busy your graphics card is. Games and video use it most.";
            _gpu.History = recent.Select(sample => sample.GpuUsagePercent).ToArray();
        }
    }

    private async Task RefreshAppsAsync()
    {
        if (_readingApps)
        {
            return;
        }

        _readingApps = true;
        _nextAppsRead = DateTime.UtcNow + AppsRefreshInterval;
        try
        {
            IReadOnlyList<AppGroup> groups = await _overview.GetAppsAsync();
            long totalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            AppRowViewModel[] top = groups
                .OrderByDescending(group => group.MemoryBytes)
                .Take(TopAppCount)
                .Select(group => new AppRowViewModel(group, totalBytes))
                .ToArray();
            TopApps.ItemsSource = top;
            TopAppsEmpty.Visibility = top.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            TopAppsEmpty.Text = "No apps found.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            TopAppsEmpty.Text = $"Couldn't read running apps: {ex.Message}";
            TopAppsEmpty.Visibility = Visibility.Visible;
        }
        finally
        {
            _readingApps = false;
        }
    }

    private async Task RefreshSavedEventsAsync()
    {
        if (_readingSavedEvents)
        {
            return;
        }

        _readingSavedEvents = true;
        _nextSavedEventsRead = DateTime.UtcNow + SavedEventsRefreshInterval;
        try
        {
            _savedEvents = await Task.Run(() => _repository.ReadPerformanceEvents(DateTimeOffset.UtcNow.AddDays(-1)));
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Keep showing what we have; the next refresh will try again.
        }
        finally
        {
            _readingSavedEvents = false;
        }
    }

    private void UpdateEvents(MonitoringSnapshot snapshot)
    {
        DashboardEventItem[] items = SlowdownHistory.Consolidate(_savedEvents.Concat(snapshot.RecentEvents))
            .OrderByDescending(performanceEvent => performanceEvent.EndedAtUtc)
            .Take(5)
            .Select(performanceEvent => new DashboardEventItem(
                performanceEvent.Summary,
                string.IsNullOrWhiteSpace(performanceEvent.LikelyCause) ? "The cause wasn't clear." : performanceEvent.LikelyCause,
                DescribeWhen(performanceEvent),
                performanceEvent.Severity switch
                {
                    EventSeverity.Critical => CriticalBrush,
                    EventSeverity.Warning => WarningBrush,
                    _ => InfoBrush
                }))
            .ToArray();

        if (RecentEvents.ItemsSource is not DashboardEventItem[] current || !current.SequenceEqual(items))
        {
            RecentEvents.ItemsSource = items;
        }

        EventsEmpty.Visibility = items.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowAppsClick(object sender, RoutedEventArgs e) => ShowAppsRequested?.Invoke(this, null);

    private void ShowTimelineClick(object sender, RoutedEventArgs e) => ShowTimelineRequested?.Invoke(this, EventArgs.Empty);

    private void AppRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AppRowViewModel row })
        {
            ShowAppsRequested?.Invoke(this, row.Key);
        }
    }

    private static string Percent(double? value) => value is null ? "--" : $"{value:F0}%";

    private static string Rate(double? megabytesPerSecond) =>
        megabytesPerSecond is null ? "--" : megabytesPerSecond >= 10 ? $"{megabytesPerSecond:F0} MB/s" : $"{megabytesPerSecond:F1} MB/s";

    private static string NetworkRate(double kbps) =>
        kbps >= 1000 ? $"{(kbps / 1000).ToString(kbps >= 10_000 ? "F0" : "F1", CultureInfo.CurrentCulture)} Mbps" : $"{kbps:F0} Kbps";

    private static double? Average(IEnumerable<double?> values)
    {
        double[] actual = values.OfType<double>().ToArray();
        return actual.Length == 0 ? null : actual.Average();
    }

    private static double? Max(IEnumerable<double?> values)
    {
        double[] actual = values.OfType<double>().ToArray();
        return actual.Length == 0 ? null : actual.Max();
    }

    private static string DescribeWhen(PerformanceEvent performanceEvent)
    {
        bool ongoing = DateTimeOffset.UtcNow - performanceEvent.EndedAtUtc < TimeSpan.FromSeconds(15);
        string length = EventMerging.DescribeDuration(performanceEvent);
        string lengthPart = length == "under a minute" ? string.Empty : $"{length}  ·  ";
        return lengthPart + (ongoing ? "now" : FormatAge(performanceEvent.EndedAtUtc));
    }

    private static string FormatAge(DateTimeOffset timestampUtc)
    {
        TimeSpan age = DateTimeOffset.UtcNow - timestampUtc;
        return age.TotalSeconds < 5 ? "just now" : $"{FormatDuration(age)} ago";
    }

    private static string FormatDuration(TimeSpan duration) => duration switch
    {
        { TotalDays: >= 1 } => $"{(int)duration.TotalDays}d {duration.Hours}h",
        { TotalHours: >= 1 } => $"{(int)duration.TotalHours}h {duration.Minutes}m",
        { TotalMinutes: >= 1 } => $"{(int)duration.TotalMinutes} min",
        _ => $"{Math.Max(0, (int)duration.TotalSeconds)}s"
    };

    private static SolidColorBrush CreateBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}

/// <summary>One slowdown in the dashboard's recent list.</summary>
public sealed record DashboardEventItem(string Summary, string Cause, string When, Brush Brush);
