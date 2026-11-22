using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using System.Windows;
using Microsoft.Extensions.Options;
using PerformanceTracker.Core.Configuration;
using PerformanceTracker.Desktop.Services;
using PerformanceTracker.Infrastructure.Storage;
using UserControl = System.Windows.Controls.UserControl;

namespace PerformanceTracker.Desktop;

/// <summary>Notifications, startup, what counts as a slowdown, and where data is kept.</summary>
public partial class SettingsView : UserControl
{
    private readonly UserSettingsStore _settings;
    private readonly AlertService _alerts;
    private readonly string _dataFolder;
    private bool _loading = true;

    public SettingsView(
        UserSettingsStore settings,
        AlertService alerts,
        SqliteSampleRepository repository,
        IOptions<CollectorSettings> collector,
        IOptions<RetentionSettings> retention)
    {
        InitializeComponent();
        _settings = settings;
        _alerts = alerts;
        _dataFolder = Path.GetDirectoryName(repository.DatabasePath) ?? AppContext.BaseDirectory;

        MemoryAlert.IsChecked = settings.Current.Memory;
        GrowthAlert.IsChecked = settings.Current.Growth;
        Threshold.Value = Math.Clamp(settings.Current.Threshold, (int)Threshold.Minimum, (int)Threshold.Maximum);
        UpdateThresholdRow();

        CollectorSettings c = collector.Value;
        CpuRule.Text = $"Over {c.CpuHighPercent:F0}% for {c.CpuHighDurationSeconds} seconds";
        MemoryRule.Text = $"Over {c.MemoryHighPercent:F0}% in use";
        DiskRule.Text = $"Busy over {c.DiskActiveHighPercent:F0}% of the time";
        IntervalRule.Text = $"Every {c.SampleIntervalSeconds} seconds";

        RetentionSettings r = retention.Value;
        EventRetention.Text = $"Kept {Days(r.EventRetentionDays)}";
        AppRetention.Text = $"Kept {Days(r.AppMemoryRetentionDays)}";
        SampleRetention.Text = r.RawSampleRetentionHours % 24 == 0
            ? $"Kept {Days(r.RawSampleRetentionHours / 24)}"
            : $"Kept {r.RawSampleRetentionHours} hours";

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                RefreshLiveValues();
            }
        };
        _loading = false;
    }

    private void RefreshLiveValues()
    {
        try
        {
            LaunchAtSignIn.IsChecked = Services.LaunchAtSignIn.IsEnabled;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            LaunchAtSignIn.IsEnabled = false;
            ShowLaunchStatus($"Couldn't check startup settings. {ex.Message}");
        }

        try
        {
            long bytes = Directory.Exists(_dataFolder)
                ? new DirectoryInfo(_dataFolder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length)
                : 0;
            DataSize.Text = AppRowViewModel.FormatBytes(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DataSize.Text = "Unknown";
        }
    }

    private void AlertChanged(object sender, RoutedEventArgs e)
    {
        UpdateThresholdRow();
        SaveAlerts();
    }

    private void ThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThresholdText is null)
        {
            return;
        }

        ThresholdText.Text = $"{e.NewValue:F0}% full";
        SaveAlerts();
    }

    private void UpdateThresholdRow() =>
        ThresholdRow.Opacity = MemoryAlert.IsChecked == true ? 1 : 0.4;

    private void SaveAlerts()
    {
        if (_loading)
        {
            return;
        }

        try
        {
            _settings.Save(new(MemoryAlert.IsChecked == true, GrowthAlert.IsChecked == true, (int)Math.Round(Threshold.Value)));
            AlertStatus.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AlertStatus.Text = $"Couldn't save. {ex.Message}";
            AlertStatus.Visibility = Visibility.Visible;
        }
    }

    private void TestClick(object sender, RoutedEventArgs e)
    {
        AlertStatus.Text = _alerts.Test()
            ? "Test notification sent. It should appear in the corner of your screen."
            : "Windows is holding notifications right now (Focus or Do Not Disturb). Try again later.";
        AlertStatus.Visibility = Visibility.Visible;
    }

    private void LaunchChanged(object sender, RoutedEventArgs e)
    {
        bool enable = LaunchAtSignIn.IsChecked == true;
        try
        {
            Services.LaunchAtSignIn.Set(enable);
            LaunchStatus.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            LaunchAtSignIn.IsChecked = !enable;
            ShowLaunchStatus($"Couldn't change startup. {ex.Message}");
        }
    }

    private void ShowLaunchStatus(string message)
    {
        LaunchStatus.Text = message;
        LaunchStatus.Visibility = Visibility.Visible;
    }

    private void OpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_dataFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_dataFolder}\"") { UseShellExecute = true });
    }

    private static string Days(int days) => days == 1
        ? "1 day"
        : string.Create(CultureInfo.CurrentCulture, $"{days} days");
}
