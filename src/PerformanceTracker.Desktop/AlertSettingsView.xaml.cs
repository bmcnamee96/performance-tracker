using System.Globalization;
using System.IO;
using System.Windows;
using PerformanceTracker.Desktop.Services;
using UserControl = System.Windows.Controls.UserControl;

namespace PerformanceTracker.Desktop;

public partial class AlertSettingsView : UserControl
{
    private readonly UserSettingsStore _settings;
    private readonly AlertService _alerts;

    public AlertSettingsView(UserSettingsStore settings, AlertService alerts)
    {
        InitializeComponent();
        _settings = settings;
        _alerts = alerts;
        MemoryAlert.IsChecked = settings.Current.Memory;
        GrowthAlert.IsChecked = settings.Current.Growth;
        Threshold.Text = settings.Current.Threshold.ToString(CultureInfo.CurrentCulture);
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(Threshold.Text, CultureInfo.CurrentCulture, out int threshold) || threshold is < 1 or > 99)
        {
            Status.Text = "Enter a threshold from 1 to 99.";
            return;
        }

        try
        {
            _settings.Save(new(MemoryAlert.IsChecked == true, GrowthAlert.IsChecked == true, threshold));
            Status.Text = "Alert settings saved.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Text = $"Couldn't save alert settings. {ex.Message}";
        }
    }

    private void TestClick(object sender, RoutedEventArgs e) =>
        Status.Text = _alerts.Test() ? "Test alert sent." : "Windows is quieting notifications. Try again when notifications are available.";
}
