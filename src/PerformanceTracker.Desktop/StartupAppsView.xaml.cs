using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using PerformanceTracker.Core.Services;
using PerformanceTracker.Infrastructure.Services;
using CheckBox = System.Windows.Controls.CheckBox;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace PerformanceTracker.Desktop;

public partial class StartupAppsView : UserControl
{
    private readonly StartupEntryReader _reader;
    private IReadOnlyList<StartupRowViewModel> _rows = [];
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
        Summary.Text = "Reading startup apps...";
        try
        {
            IReadOnlyList<StartupEntry> entries = await Task.Run(_reader.Read);
            _rows = entries.Select(entry => new StartupRowViewModel(entry)).ToArray();
            ApplyView();
            Status.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or COMException or ManagementException)
        {
            ShowStatus($"Couldn't read startup apps. {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private void FilterChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplyView();
        }
    }

    private void ApplyView()
    {
        bool showBackground = ShowBackground.IsChecked == true;
        ListCollectionView view = new(_rows.ToList())
        {
            Filter = item => showBackground || ((StartupRowViewModel)item).Category != StartupCategory.ServicesAndTasks
        };
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(StartupRowViewModel.GroupTitle)));
        view.SortDescriptions.Add(new SortDescription(nameof(StartupRowViewModel.Category), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(StartupRowViewModel.Name), ListSortDirection.Ascending));
        Entries.ItemsSource = view;

        StartupRowViewModel[] yours = _rows.Where(row => row.Category == StartupCategory.YourApps).ToArray();
        int yoursOn = yours.Count(row => row.Enabled == true);
        int hidden = _rows.Count(row => row.Category == StartupCategory.ServicesAndTasks);
        Summary.Text = $"{yoursOn} of your {yours.Length} apps start with Windows" +
            (showBackground ? "." : $". {hidden} background services and tasks are hidden.");
    }

    private void ShowStatus(string message)
    {
        Status.Text = message;
        Status.Visibility = Visibility.Visible;
    }

    private async void ToggleClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: StartupRowViewModel { Entry: var entry } } checkBox)
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
            ShowStatus($"Couldn't change startup. {ex.Message}");
            return;
        }
        finally
        {
            _busy = false;
        }

        await RefreshAsync();
    }
}
