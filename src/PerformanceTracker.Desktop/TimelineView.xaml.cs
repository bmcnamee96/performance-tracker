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
            return;
        }

        _loading = true;
        try
        {
            DateTime today = DateTime.Today;
            bool hourly = RangeToday.IsChecked == true;
            int days = RangeMonth.IsChecked == true ? 30 : RangeWeek.IsChecked == true ? 7 : 1;
            DateTimeOffset since = new(today.AddDays(1 - days));

            IReadOnlyList<PerformanceEvent> saved = await Task.Run(() => _repository.ReadPerformanceEvents(since));
            IReadOnlyList<PerformanceEvent> events = SlowdownHistory.Consolidate(
                saved.Concat(_state.GetSnapshot().RecentEvents.Where(e => e.EndedAtUtc >= since)));

            ShowTotals(events);
            ShowBars(events, since, hourly, days);
            ShowDays(events);
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
        }
    }

    private void ShowTotals(IReadOnlyList<PerformanceEvent> events)
    {
        SlowdownTotals totals = SlowdownHistory.Totals(events);
        CountText.Text = totals.Count.ToString(CultureInfo.CurrentCulture);
        TotalText.Text = totals.Count == 0 ? "None" : FormatLength(totals.TotalTime);
        CommonText.Text = totals.MostCommon ?? "Nothing yet";
        EmptyState.Visibility = events.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowBars(IReadOnlyList<PerformanceEvent> events, DateTimeOffset since, bool hourly, int days)
    {
        TimeSpan bucket = hourly ? TimeSpan.FromHours(1) : TimeSpan.FromDays(1);
        int count = hourly ? 24 : days;
        double[] minutes = SlowdownHistory.MinutesPerBucket(events, since, bucket, count);
        double max = Math.Max(minutes.Max(), 5);

        TimelineBar[] bars = minutes.Select((value, index) =>
        {
            DateTime start = since.LocalDateTime + bucket * index;
            string when = hourly
                ? start.ToString("h tt", CultureInfo.CurrentCulture)
                : start.ToString("ddd, MMM d", CultureInfo.CurrentCulture);
            string label = hourly
                ? index % 6 == 0 ? start.ToString("h tt", CultureInfo.CurrentCulture).ToLowerInvariant() : string.Empty
                : days <= 7 || index % 5 == 0 ? start.ToString(days <= 7 ? "ddd" : "MMM d", CultureInfo.CurrentCulture) : string.Empty;
            string amount = value < 0.5 ? (value > 0 ? "under a minute" : "no slowdowns") : $"{Math.Round(value)} min slowed";
            return new TimelineBar(
                value <= 0 ? 3 : Math.Max(6, value / max * BarAreaHeight),
                value <= 0 ? EmptyBarBrush : BarBrush,
                $"{when}: {amount}",
                label);
        }).ToArray();

        Bars.ItemsSource = bars;
        BarLabels.ItemsSource = bars;
    }

    private void ShowDays(IReadOnlyList<PerformanceEvent> events)
    {
        DateTime today = DateTime.Today;
        Days.ItemsSource = events
            .GroupBy(e => e.StartedAtUtc.LocalDateTime.Date)
            .OrderByDescending(group => group.Key)
            .Select(group =>
            {
                PerformanceEvent[] dayEvents = group.OrderByDescending(e => e.StartedAtUtc).ToArray();
                SlowdownTotals totals = SlowdownHistory.Totals(dayEvents);
                string title = group.Key == today ? "Today"
                    : group.Key == today.AddDays(-1) ? "Yesterday"
                    : group.Key.ToString("dddd, MMMM d", CultureInfo.CurrentCulture);
                string summary = $"{totals.Count} slowdown{(totals.Count == 1 ? string.Empty : "s")}  ·  {FormatLength(totals.TotalTime)}";
                return new TimelineDay(title, summary, dayEvents.Select(ToEntry).ToArray());
            })
            .ToArray();
    }

    private static TimelineEntry ToEntry(PerformanceEvent performanceEvent)
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

        string time = performanceEvent.StartedAtUtc.LocalDateTime.ToString("h:mm tt", CultureInfo.CurrentCulture);
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

/// <summary>One bar in the timeline chart.</summary>
public sealed record TimelineBar(double Height, Brush Fill, string ToolTip, string Label);

/// <summary>A day heading and its slowdowns.</summary>
public sealed record TimelineDay(string Title, string Totals, IReadOnlyList<TimelineEntry> Items);

/// <summary>One slowdown card.</summary>
public sealed record TimelineEntry(string Title, string Cause, string Time, string Length, IReadOnlyList<string> Chips, Brush Brush);
