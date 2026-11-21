using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;
using PerformanceTracker.Core.Models;
using PerformanceTracker.Desktop.Services;
using Brush = System.Windows.Media.Brush;
using BrushConverter = System.Windows.Media.BrushConverter;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace PerformanceTracker.Desktop;

/// <summary>One row in the Apps tab. Updated in place so the list does not jump while you read it.</summary>
public sealed class AppRowViewModel : INotifyPropertyChanged
{
    private static readonly Brush SuggestedBrush = CreateBrush("#47CD89");
    private static readonly Brush SuggestedBackgroundBrush = CreateBrush("#0E3B2A");
    private static readonly Brush BusyBrush = CreateBrush("#53B1FD");
    private static readonly Brush BusyBackgroundBrush = CreateBrush("#102A45");
    private static readonly Brush NeutralBrush = CreateBrush("#A9BACB");
    private static readonly Brush NeutralBackgroundBrush = CreateBrush("#1C2B3B");

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource?> Icons = new(StringComparer.OrdinalIgnoreCase);
    private AppGroup _group;
    private bool _isExpanded;

    public ObservableCollection<AppRowViewModel> Processes { get; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            if (value)
            {
                UpdateProcesses();
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public ImageSource? Icon => _group.ExecutablePath is string path ? Icons.GetValueOrDefault(path) : null;

    public static void CacheIcons(IEnumerable<AppGroup> groups)
    {
        foreach (string path in groups.Select(group => group.ExecutablePath).OfType<string>())
        {
            if (Icons.ContainsKey(path))
            {
                continue;
            }

            ImageSource? source = null;
            try
            {
                using System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon is not null)
                {
                    source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                    source.Freeze();
                }
            }
            catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
            {
                // Some installed app paths are inaccessible.
            }

            Icons[path] = source;
        }
    }

    private void UpdateProcesses()
    {
        Dictionary<string, AppGroup> incoming = _group.Processes.ToDictionary(process => process.Key);
        for (int index = Processes.Count - 1; index >= 0; index--)
        {
            AppRowViewModel process = Processes[index];
            if (incoming.Remove(process.Key, out AppGroup? updated))
            {
                process.Update(updated, TotalMemoryBytes);
            }
            else
            {
                Processes.RemoveAt(index);
            }
        }

        foreach (AppGroup process in _group.Processes.Where(process => incoming.ContainsKey(process.Key)))
        {
            Processes.Add(new AppRowViewModel(process, TotalMemoryBytes));
        }
    }

    internal DateTime NextTabRead { get; set; }

    public System.Collections.IEnumerable BrowserTabs { get; private set; } = Array.Empty<object>();

    public string BrowserTabStatus { get; private set; } = "Reading open tabs?";

    internal void UpdateBrowserTabs(BrowserTab[] tabs, string status)
    {
        BrowserTabs = tabs;
        BrowserTabStatus = status;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BrowserTabs)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BrowserTabStatus)));
    }

    public string BrowserNote => PerformanceTracker.Core.Services.AppGroupBuilder.IsChromium(Key)
        ? "Largest processes first when opened. Tabs and frames can share a process. For tabs without a memory reading here, open the browser's Task Manager (Shift+Esc)."
        : string.Empty;

    public Visibility BrowserNoteVisibility => BrowserNote.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public string StatusReason => _group.Safety == AppSafety.Protected
        ? "Windows protection or an identity check blocks ending this process."
        : _group.Safety == AppSafety.Service
            ? "This background service may support other apps."
            : _group.IsSuggested
                ? "This background app has no visible window, has been observed using little CPU for at least 10 minutes, and uses at least 200 MB of memory. It may still have work in progress."
                : _group.Activity == AppActivity.Quiet
                    ? "CPU use has been low. Open windows and background work can still be in use."
                    : _group.Activity == AppActivity.Busy ? "CPU activity was detected recently." : "Waiting for reliable activity readings.";


    public AppRowViewModel(AppGroup group, long totalMemoryBytes)
    {
        _group = group;
        TotalMemoryBytes = totalMemoryBytes;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppGroup Group => _group;

    public string Key => _group.Key;

    public long TotalMemoryBytes { get; private set; }

    public string DisplayName => _group.DisplayName;

    public string CountText => _group.ProcessIds.Count == 1 ? "1 process" : $"{_group.ProcessIds.Count} processes";

    public string Explanation => _group.Explanation;

    public string Detail => _group.Detail ?? string.Empty;

    public string MemoryText => FormatBytes(_group.MemoryBytes);

    public double MemoryFraction => TotalMemoryBytes <= 0 ? 0 : Math.Clamp((double)_group.MemoryBytes / TotalMemoryBytes, 0, 1);

    public string CpuText => _group.CpuPercent >= 0.1 ? $"{_group.CpuPercent:F1}% CPU" : "<0.1% CPU";

    public string StatusText =>
        _group.Safety == AppSafety.Protected ? "Protected"
        : _group.Safety == AppSafety.Service ? "Service"
        : _group.IsSuggested ? $"Stale {FormatDuration(_group.QuietFor)}"
        : _group.Activity switch
        {
            AppActivity.Busy => "Active",
            AppActivity.Quiet => $"Idle {FormatDuration(_group.QuietFor)}",
            _ => "Checking..."
        };

    public Brush StatusBrush =>
        _group.IsSuggested ? SuggestedBrush
        : _group.Activity == AppActivity.Busy && _group.Safety != AppSafety.Protected ? BusyBrush
        : NeutralBrush;

    public Brush StatusBackground =>
        _group.IsSuggested ? SuggestedBackgroundBrush
        : _group.Activity == AppActivity.Busy && _group.Safety != AppSafety.Protected ? BusyBackgroundBrush
        : NeutralBackgroundBrush;

    public bool CanEnd => _group.CanEnd;

    public string EndButtonText => _group.CanEnd ? (_group.HasWindow ? "Close" : "End") : "Protected";

    public string EndButtonToolTip => _group.CanEnd
        ? $"End {_group.DisplayName} ({CountText}, {MemoryText}). You will be asked to confirm."
        : "Windows protection or an identity check blocks this action.";

    public void Update(AppGroup group, long totalMemoryBytes)
    {
        _group = group;
        TotalMemoryBytes = totalMemoryBytes;
        if (IsExpanded)
        {
            UpdateProcesses();
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public static string FormatBytes(long bytes)
    {
        double mb = bytes / 1024d / 1024d;
        return mb >= 1024 ? $"{mb / 1024:F1} GB" : $"{mb:F0} MB";
    }

    public static string FormatDuration(TimeSpan? duration)
    {
        if (duration is not TimeSpan value)
        {
            return "--";
        }

        if (value.TotalHours >= 1)
        {
            return $"{(int)value.TotalHours}h {value.Minutes}m";
        }

        return value.TotalMinutes >= 1 ? $"{(int)value.TotalMinutes} min" : "<1 min";
    }

    private static SolidColorBrush CreateBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
