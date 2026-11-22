using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Toolkit.Uwp.Notifications;
using PerformanceTracker.Core.Models;
using PerformanceTracker.Core.Services;

namespace PerformanceTracker.Desktop.Services;

public sealed class AlertService(
    MonitoringState monitoring,
    ProcessOverviewService overview,
    UserSettingsStore settings,
    TrayIconService tray) : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly AlertRules _rules = new();
    private readonly Dictionary<string, AppMemoryGrowth> _pending = new(StringComparer.Ordinal);
    private DateTimeOffset _pausedUntil;
    private MetricSample? _lastSample;
    private Action<string?>? _showApp;
    private bool _toastAvailable;

    public void Start(Action<string?> showApp)
    {
        _showApp = showApp;
        try
        {
            ToastNotificationManagerCompat.OnActivated += Activated;
            _toastAvailable = true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or TypeInitializationException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
        overview.GrowthDetected += GrowthDetected;
        _timer.Tick += Check;
        _timer.Start();
    }

    public void Pause() => _pausedUntil = DateTimeOffset.UtcNow.AddHours(1);

    public bool Test()
    {
        if (IsQuiet())
        {
            return false;
        }

        Notify("Performance Tracker test alert", "This is what an alert looks like.");
        return true;
    }

    private void GrowthDetected(object? sender, AppMemoryGrowth growth) =>
        _ = _timer.Dispatcher.BeginInvoke(() => _pending[growth.Latest.Key] = growth);

    private async void Check(object? sender, EventArgs e)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool quiet = now < _pausedUntil || IsQuiet();
        AlertSettings options = settings.Current;
        foreach ((string key, AppMemoryGrowth growth) in _pending.ToArray())
        {
            if (!options.Growth || overview.GetGrowth(key) is null)
            {
                _pending.Remove(key);
            }
            else if (!quiet)
            {
                _pending.Remove(key);
                if (_rules.Growth(now, key, quiet))
                {
                    Notify($"{growth.Latest.DisplayName} is using more memory", growth.Reason, key);
                }
            }
        }

        MetricSample? sample = monitoring.GetSnapshot().LatestSample;
        if (sample is null || ReferenceEquals(sample, _lastSample))
        {
            return;
        }

        _lastSample = sample;
        if (!_rules.Memory(sample.TimestampUtc, options.Memory ? sample.MemoryUsedPercent : 0, options.Threshold, quiet))
        {
            return;
        }

        AppGroup? biggest = null;
        try
        {
            biggest = (await overview.GetAppsAsync()).MaxBy(app => app.MemoryBytes);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }

        if (DateTimeOffset.UtcNow >= _pausedUntil && settings.Current.Memory && !IsQuiet())
        {
            Notify($"Memory at {sample.MemoryUsedPercent:F0}%",
                biggest is null ? "Open Apps & Memory to see what's using memory." :
                $"{biggest.NameWithActivity} is using {AppRowViewModel.FormatBytes(biggest.MemoryBytes)}.", biggest?.Key);
        }
    }

    private void Activated(ToastNotificationActivatedEventArgsCompat args)
    {
        ToastArguments parsed = ToastArguments.Parse(args.Argument);
        _ = _timer.Dispatcher.BeginInvoke(() => _showApp?.Invoke(parsed.TryGetValue("app", out string? key) ? key : null));
    }

    private void Notify(string title, string body, string? key = null)
    {
        if (!_toastAvailable)
        {
            tray.ShowAlert(title, body, () => _showApp?.Invoke(key));
            return;
        }

        try
        {
            new ToastContentBuilder()
                .AddArgument("app", key ?? "")
                .AddText(title)
                .AddText(body)
                .AddButton(new ToastButton().SetContent("Show me").AddArgument("show", true))
                .AddButton(new ToastButtonDismiss("Dismiss"))
                .Show();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            tray.ShowAlert(title, body, () => _showApp?.Invoke(key));
        }
    }

    private static bool IsQuiet() =>
        SHQueryUserNotificationState(out int state) < 0 || state is not (5 or 7);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHQueryUserNotificationState(out int state);

    public void Dispose()
    {
        _timer.Stop();
        overview.GrowthDetected -= GrowthDetected;
        if (_toastAvailable)
        {
            ToastNotificationManagerCompat.OnActivated -= Activated;
        }
    }
}
