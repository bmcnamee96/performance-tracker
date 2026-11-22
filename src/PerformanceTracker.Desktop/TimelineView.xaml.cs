using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using PerformanceTracker.Core.Models;
using PerformanceTracker.Core.Services;
using PerformanceTracker.Desktop.Services;
using PerformanceTracker.Infrastructure.Storage;
using Brush = System.Windows.Media.Brush;
using BrushConverter = System.Windows.Media.BrushConverter;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using UserControl = System.Windows.Controls.UserControl;

namespace PerformanceTracker.Desktop;

/// <summary>History of slowdowns: totals, a bar chart of when they happened, and a day-by-day list.</summary>
public partial class TimelineView : UserControl
{
    private const double BarAreaHeight = 66;
    private static readonly Brush CriticalBrush = CreateBrush("#F97066");
    private static readonly Brush WarningBrush = CreateBrush("#FDB022");
    private static readonly Brush InfoBrush = CreateBrush("#53B1FD");
    private static readonly Brush BarBrush = CreateBrush("#3FD5E6");
    private static readonly Brush EmptyBarBrush = CreateBrush("#1A2E42");

    private readonly SqliteSampleRepository _repository;
    private readonly MonitoringState _state;
    private readonly DispatcherTimer _timer;
    private bool _loading;

    public TimelineView(SqliteSampleRepository repository, MonitoringState state)
    {
        InitializeComponent();
        _repository = repository;
        _state = state;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += async (_, _) => await LoadAsync();
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible)
            {
                _timer.Start();
                await LoadAsync();
            }
            else
            {
                _timer.Stop();
            }
        };
    }

    private bool _reloadRequested;

    private async void RangeChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            await LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        if (_loading)
        {
            // Picked another range mid-load: load again once this one finishes so the chips and results agree.
            _reloadRequested = true;
            return;
        }

        _loading = true;
        try
        {
            do
            {
                _reloadRequested = false;
                int days = RangeMonth.IsChecked == true ? 30 : RangeWeek.IsChecked == true ? 7 : 1;
                ContentRoot.Opacity = 0.55;
                IReadOnlyList<PerformanceEvent> live = _state.GetSnapshot().RecentEvents;
                TimelineData data = await Task.Run(() => Build(days, live));
                if (!_reloadRequested)
                {
                    Show(data);
                }
            }
            while (_reloadRequested);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or System.IO.IOException)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            CountText.Text = "--";
            CommonText.Text = $"Couldn't read history: {ex.Message}";
        }
        finally
        {
            _loading = false;
            ContentRoot.Opacity = 1;
        }
    }

    // Runs off the UI thread: reads the database, merges reports and prepares everything the view shows.
    private TimelineData Build(int days, IReadOnlyList<PerformanceEvent> live)
    {
        TimeZoneInfo zone = TimeZoneInfo.Local;
        DateTime today = DateTime.Now.Date;
        DateTime firstDay = today.AddDays(1 - days);
        DateTimeOffset since = SlowdownHistory.LocalMidnight(firstDay, zone);
        DateTimeOffset until = SlowdownHistory.LocalMidnight(today.AddDays(1), zone);

        IReadOnlyList<PerformanceEvent> events = SlowdownHistory.Consolidate(
            _repository.ReadPerformanceEvents(since).Concat(live.Where(e => e.EndedAtUtc >= since)));

        SlowdownTotals totals = SlowdownHistory.Totals(events, since, until);
        bool hourly = days == 1;
        IReadOnlyList<DateTimeOffset> edges = hourly
            ? SlowdownHistory.HourBoundaries(today, zone)
            : SlowdownHistory.DayBoundaries(firstDay, days, zone);
        TimelineBar[] bars = BuildBars(events, edges, hourly, days);

        TimelineDay[] dayGroups = events
            .GroupBy(e => Max(TimeZoneInfo.ConvertTime(e.StartedAtUtc, zone).Date, firstDay))
            .OrderByDescending(group => group.Key)
            .Select(group =>
            {
                PerformanceEvent[] dayEvents = group.OrderByDescending(e => e.StartedAtUtc).ToArray();
                SlowdownTotals dayTotals = SlowdownHistory.Totals(events,
                    SlowdownHistory.LocalMidnight(group.Key, zone), SlowdownHistory.LocalMidnight(group.Key.AddDays(1), zone));
                string title = group.Key == today ? "Today"
                    : group.Key == today.AddDays(-1) ? "Yesterday"
                    : group.Key.ToString("dddd, MMMM d", CultureInfo.CurrentCulture);
                string summary = $"{dayTotals.Count} slowdown{(dayTotals.Count == 1 ? string.Empty : "s")}  ·  {FormatLength(dayTotals.TotalTime)}";
                return new TimelineDay(title, summary, dayEvents.Select(e => ToEntry(e, zone, group.Key)).ToArray());
            })
            .ToArray();

        return new TimelineData(totals, bars, dayGroups);
    }

    private void Show(TimelineData data)
    {
        CountText.Text = data.Totals.Count.ToString(CultureInfo.CurrentCulture);
        TotalText.Text = data.Totals.Count == 0 ? "None" : FormatLength(data.Totals.TotalTime);
        CommonText.Text = data.Totals.MostCommon ?? "Nothing yet";
        EmptyState.Visibility = data.Days.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Bars.ItemsSource = data.Bars;
        BarLabels.ItemsSource = data.Bars;
        Days.ItemsSource = data.Days;
    }

    private static TimelineBar[] BuildBars(IReadOnlyList<PerformanceEvent> events, IReadOnlyList<DateTimeOffset> edges, bool hourly, int days)
    {
        double[] minutes = SlowdownHistory.MinutesPerBucket(events, edges);
        double max = Math.Max(minutes.DefaultIfEmpty(0).Max(), 5);
        return minutes.Select((value, index) =>
        {
            DateTime start = TimeZoneInfo.ConvertTime(edges[index], TimeZoneInfo.Local).DateTime;
            string when = hourly
                ? start.ToString("h tt", CultureInfo.CurrentCulture)
                : start.ToString("ddd, MMM d", CultureInfo.CurrentCulture);
            string label = hourly
                ? start.Hour % 6 == 0 && start.Minute == 0 ? start.ToString("h tt", CultureInfo.CurrentCulture).ToLowerInvariant() : string.Empty
                : days <= 7 || index % 5 == 0 ? start.ToString(days <= 7 ? "ddd" : "MMM d", CultureInfo.CurrentCulture) : string.Empty;
            string amount = value < 0.5 ? (value > 0 ? "under a minute" : "no slowdowns") : $"{Math.Round(value)} min slowed";
            return new TimelineBar(
                value <= 0 ? 3 : Math.Max(6, value / max * BarAreaHeight),
                value <= 0 ? EmptyBarBrush : BarBrush,
                $"{when}: {amount}",
                label);
        }).ToArray();
    }

    private static DateTime Max(DateTime left, DateTime right) => left > right ? left : right;

    private static TimelineEntry ToEntry(PerformanceEvent performanceEvent, TimeZoneInfo zone, DateTime shownDay)
    {
        List<string> chips = [];
        if (!string.IsNullOrWhiteSpace(performanceEvent.ForegroundApp))
        {
            chips.Add($"Using {performanceEvent.ForegroundApp}");
        }

        MetricSample trigger = performanceEvent.TriggerSample;
        if (trigger.CpuUsagePercent is double cpu)
        {
            chips.Add($"Processor {cpu:F0}%");
        }

        if (trigger.MemoryUsedPercent > 0)
        {
            chips.Add($"Memory {trigger.MemoryUsedPercent:F0}%");
        }

        if (trigger.DiskActiveTimePercent is double disk)
        {
            chips.Add($"Disk {disk:F0}%");
        }

        DateTimeOffset localStart = TimeZoneInfo.ConvertTime(performanceEvent.StartedAtUtc, zone);
        string time = localStart.ToString("h:mm tt", CultureInfo.CurrentCulture) +
            (localStart.Date < shownDay ? " (day before)" : string.Empty);
        string length = EventMerging.DescribeDuration(performanceEvent);
        return new TimelineEntry(
            performanceEvent.Summary,
            string.IsNullOrWhiteSpace(performanceEvent.LikelyCause) ? "The cause wasn't clear." : performanceEvent.LikelyCause,
            time,
            char.ToUpperInvariant(length[0]) + length[1..],
            chips,
            performanceEvent.Severity switch
            {
                EventSeverity.Critical => CriticalBrush,
                EventSeverity.Warning => WarningBrush,
                _ => InfoBrush
            });
    }

    private static string FormatLength(TimeSpan length) => length switch
    {
        { TotalMinutes: < 1 } => "Under 1 min",
        { TotalHours: < 1 } => $"{(int)Math.Round(length.TotalMinutes)} min",
        _ => $"{(int)length.TotalHours} h {length.Minutes} min"
    };

    private static SolidColorBrush CreateBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}

/// <summary>Everything the timeline shows for one range, prepared off the UI thread.</summary>
internal sealed record TimelineData(SlowdownTotals Totals, TimelineBar[] Bars, TimelineDay[] Days);

/// <summary>One bar in the timeline chart.</summary>
public sealed record TimelineBar(double Height, Brush Fill, string ToolTip, string Label);

/// <summary>A day heading and its slowdowns.</summary>
public sealed record TimelineDay(string Title, string Totals, IReadOnlyList<TimelineEntry> Items);

/// <summary>One slowdown card.</summary>
public sealed record TimelineEntry(string Title, string Cause, string Time, string Length, IReadOnlyList<string> Chips, Brush Brush);
