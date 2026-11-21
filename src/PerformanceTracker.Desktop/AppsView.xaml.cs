using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using PerformanceTracker.Core.Interfaces;
using PerformanceTracker.Core.Models;
using PerformanceTracker.Desktop.Services;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;
using UserControl = System.Windows.Controls.UserControl;

namespace PerformanceTracker.Desktop;

/// <summary>
/// A plain-language alternative to Task Manager: one row per app, sorted by memory,
/// with advice on what looks unused and a confirmed End button.
/// </summary>
public partial class AppsView : UserControl
{
    private const long SmallAppBytes = 50L * 1024 * 1024;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ResortInterval = TimeSpan.FromSeconds(15);

    private readonly ProcessOverviewService _overview;
    private readonly IProcessTerminator _terminator;
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<AppRowViewModel> _rows = [];
    private IReadOnlyList<AppGroup> _latestGroups = [];
    private DateTime _lastSortUtc = DateTime.MinValue;
    private bool _refreshing;
    private bool _busyEnding;
    private bool _closingTab;
    private readonly BrowserTabsService _browserTabs = new();
    private readonly HashSet<AppRowViewModel> _readingTabs = [];


    public AppsView(ProcessOverviewService overview, IProcessTerminator terminator)
    {
        InitializeComponent();
        _overview = overview;
        _terminator = terminator;
        AppList.ItemsSource = _rows;

        _timer = new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick += async (_, _) => await RefreshAsync(forceSort: false);

        IsVisibleChanged += async (_, _) =>
        {
            if (IsVisible)
            {
                _timer.Start();
                await RefreshAsync(forceSort: true);
            }
            else
            {
                _timer.Stop();
            }
        };
    }

    private async Task RefreshAsync(bool forceSort)
    {
        if (_refreshing || !IsVisible)
        {
            return;
        }

        _refreshing = true;
        try
        {
            _latestGroups = await _overview.GetAppsAsync();
            ApplyGroups(forceSort);
            await Task.WhenAll(_rows.Where(row => row.IsExpanded).Select(RefreshTabsAsync));

        }
        catch (Exception ex)
        {
            ShowStatus($"Couldn't read running apps: {ex.Message}");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplyGroups(bool forceSort)
    {
        (long totalBytes, long availableBytes) = ReadMemoryStatus();
        long usedBytes = totalBytes - availableBytes;

        MemorySummaryText.Text = totalBytes > 0
            ? $"{AppRowViewModel.FormatBytes(usedBytes)} of {AppRowViewModel.FormatBytes(totalBytes)} in use ({(double)usedBytes / totalBytes:P0}). {AppRowViewModel.FormatBytes(availableBytes)} free."
            : "Memory totals unavailable.";
        MemoryUsageBar.Value = totalBytes > 0 ? (double)usedBytes / totalBytes : 0;

        AppGroup[] suggested = _latestGroups.Where(g => g.IsSuggested).ToArray();
        SuggestionSummaryText.Text = suggested.Length == 0
            ? "Nothing looks unused right now."
            : $"{suggested.Length} app{(suggested.Length == 1 ? " looks" : "s look")} unused. Estimated memory in idle candidates: {AppRowViewModel.FormatBytes(suggested.Sum(g => g.MemoryBytes))}.";

        bool showWindows = ShowWindowsCheckBox.IsChecked == true;
        bool hideSmall = HideSmallCheckBox.IsChecked == true;
        AppGroup[] visible = _latestGroups
            .Where(g => showWindows || g.Safety != AppSafety.Protected)
            .Where(g => !hideSmall || g.MemoryBytes >= SmallAppBytes)
            .ToArray();

        // Re-sorting moves rows under the mouse, so only do it occasionally and never while the pointer is on the list.
        bool resort = !AppList.IsMouseOver && !AppList.IsKeyboardFocusWithin && !_rows.Any(row => row.IsExpanded) &&
            (forceSort || DateTime.UtcNow - _lastSortUtc > ResortInterval);
        Dictionary<string, AppRowViewModel> existing = _rows.ToDictionary(r => r.Key);
        HashSet<string> visibleKeys = visible.Select(g => g.Key).ToHashSet();

        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            if (!visibleKeys.Contains(_rows[i].Key))
            {
                _rows.RemoveAt(i);
            }
        }

        for (int index = 0; index < visible.Length; index++)
        {
            AppGroup group = visible[index];
            if (existing.TryGetValue(group.Key, out AppRowViewModel? row))
            {
                row.Update(group, totalBytes);
                if (resort)
                {
                    int current = _rows.IndexOf(row);
                    if (current != index)
                    {
                        _rows.Move(current, index);
                    }
                }
            }
            else
            {
                AppRowViewModel newRow = new(group, totalBytes);
                if (resort)
                {
                    _rows.Insert(Math.Min(index, _rows.Count), newRow);
                }
                else
                {
                    _rows.Add(newRow);
                }
            }
        }

        if (resort)
        {
            _lastSortUtc = DateTime.UtcNow;
        }
    }

    private async Task RefreshTabsAsync(AppRowViewModel row)
    {
        if (!IsVisible || !row.IsExpanded || row.BrowserNoteVisibility != Visibility.Visible ||
            DateTime.UtcNow < row.NextTabRead || !_readingTabs.Add(row))
        {
            return;
        }

        try
        {
            var (tabs, status) = await _browserTabs.ReadAsync(row.Group.ProcessIds);
            if (row.IsExpanded)
            {
                row.UpdateBrowserTabs(tabs, status);
            }
        }
        finally
        {
            row.NextTabRead = DateTime.UtcNow.AddSeconds(5);
            _readingTabs.Remove(row);
        }
    }

    private async void OnAppExpanded(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) &&
            sender is System.Windows.Controls.Expander { DataContext: AppRowViewModel row })
        {
            await RefreshTabsAsync(row);
        }
    }

    private async void OnCloseTabClicked(object sender, RoutedEventArgs e)
    {
        if (_closingTab || sender is not Button { DataContext: BrowserTab { CanClose: true } tab })
        {
            return;
        }

        _closingTab = true;
        try
        {
            if (MessageBox.Show(Window.GetWindow(this),
                $"Close the tab ?{tab.Title}?? Unsaved form input on that page will be lost.",
                "Close browser tab", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                return;
            }

            ShowStatus(await BrowserTabsService.CloseAsync(tab));
            foreach (AppRowViewModel row in _rows)
            {
                row.NextTabRead = DateTime.MinValue;
            }

            await Task.WhenAll(_rows.Where(row => row.IsExpanded).Select(RefreshTabsAsync));
        }
        finally
        {
            _closingTab = false;
        }
    }

    private async void OnEndClicked(object sender, RoutedEventArgs e)
    {
        if (_busyEnding || sender is not Button { DataContext: AppRowViewModel row } || !row.CanEnd)
        {
            return;
        }

        AppGroup group = row.Group;
        List<string> lines =
        [
            $"This ends {row.CountText} using about {row.MemoryText}.",
            group.Explanation
        ];

        if (group.Detail is not null)
        {
            lines.Add(group.Detail);
        }

        if (group.Caution is not null)
        {
            lines.Add(string.Empty);
            lines.Add(group.Caution);
        }

        if (group.HasWindow)
        {
            lines.Add(string.Empty);
            lines.Add("Its windows will be asked to close first, so you get a chance to save.");
        }

        lines.Add(string.Empty);
        lines.Add($"End {group.DisplayName} now?");

        _busyEnding = true;
        MessageBoxResult answer = MessageBox.Show(
            Window.GetWindow(this),
            string.Join(Environment.NewLine, lines),
            $"End {group.DisplayName}?",
            MessageBoxButton.YesNo,
            group.Safety == AppSafety.Normal ? MessageBoxImage.Question : MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            _busyEnding = false;
            return;
        }

        ShowStatus($"Ending {group.DisplayName}...");
        try
        {
            EndProcessResult result = await _terminator.EndAsync(group.Targets, force: false, CancellationToken.None);

            if (result.StillRunning > 0)
            {
                MessageBoxResult forceAnswer = MessageBox.Show(
                    Window.GetWindow(this),
                    $"{group.DisplayName} hasn't closed yet. It may be showing a \"save changes?\" prompt - check its window." +
                    $"{Environment.NewLine}{Environment.NewLine}Force it to end now? Anything unsaved will be lost.",
                    $"{group.DisplayName} is still open",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);

                if (forceAnswer == MessageBoxResult.Yes)
                {
                    EndProcessResult forced = await _terminator.EndAsync(group.Targets, force: true, CancellationToken.None);
                    result = new EndProcessResult(
                        result.Ended + forced.Ended,
                        forced.StillRunning,
                        Math.Max(result.AccessDenied, forced.AccessDenied),
                        result.FreedBytes + forced.FreedBytes);
                }
            }

            ShowStatus(DescribeResult(group, result));
        }
        catch (Exception ex)
        {
            ShowStatus($"Couldn't end {group.DisplayName}: {ex.Message}");
        }
        finally
        {
            _busyEnding = false;
        }

        await RefreshAsync(forceSort: true);
    }

    private static string DescribeResult(AppGroup group, EndProcessResult result)
    {
        List<string> parts = [];
        if (result.Ended > 0)
        {
            parts.Add($"Ended {group.DisplayName} - was using about {AppRowViewModel.FormatBytes(result.FreedBytes)}.");
        }

        if (result.StillRunning > 0)
        {
            parts.Add($"{result.StillRunning} process{(result.StillRunning == 1 ? " is" : "es are")} still running.");
        }

        if (result.AccessDenied > 0)
        {
            parts.Add($"Windows access or identity checks blocked {result.AccessDenied} processes.");
        }

        return parts.Count == 0 ? $"{group.DisplayName} had already closed." : string.Join(" ", parts);
    }

    private void ShowStatus(string message)
    {
        ActionStatusText.Text = message;
        ActionStatusText.Visibility = Visibility.Visible;
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync(forceSort: true);

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplyGroups(forceSort: true);
        }
    }

    private static (long Total, long Available) ReadMemoryStatus()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? ((long)status.TotalPhys, (long)status.AvailPhys) : (0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
