using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using PerformanceTracker.Infrastructure.Services;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace PerformanceTracker.Desktop;

public partial class StartupAppsView : UserControl
{
    private readonly StartupEntryReader _reader;
    private bool _busy;

    public StartupAppsView(StartupEntryReader reader)
    {
        InitializeComponent();
        _reader = reader;
        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible)
            {
                await RefreshAsync();
            }
        };
    }

    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        Status.Text = "Reading startup apps…";
        try
        {
            var entries = await Task.Run(_reader.Read);
            Entries.ItemsSource = entries;
            Status.Text = $"{entries.Count} startup items";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or COMException or ManagementException)
        {
            Status.Text = $"Couldn't read startup apps. {ex.Message}";
        }
        finally
        {
            _busy = false;
        }
    }

    private async void ToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: StartupEntry entry } checkBox)
        {
            return;
        }

        checkBox.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.UpdateTarget();
        if (_busy || !entry.CanChange)
        {
            return;
        }

        bool enable = entry.Enabled == false;
        if (!enable && MessageBox.Show(Window.GetWindow(this),
            $"{entry.Name} won't start with Windows. Some features may be unavailable until you open it.\n\n{entry.Explanation}",
            "Turn off automatic startup?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        _busy = true;
        try
        {
            await Task.Run(() => StartupEntryReader.SetEnabled(entry, enable));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            Status.Text = $"Couldn't change startup. {ex.Message}";
            return;
        }
        finally
        {
            _busy = false;
        }

        await RefreshAsync();
    }
}
