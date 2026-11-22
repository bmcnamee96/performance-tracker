using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Brush = System.Windows.Media.Brush;

namespace PerformanceTracker.Desktop;

/// <summary>One live metric tile on the dashboard.</summary>
public sealed class DashboardTileViewModel(string title, Brush accent, double maximum = 100) : INotifyPropertyChanged
{
    private string _value = "--";
    private string _detail = string.Empty;
    private string _toolTip = string.Empty;
    private IReadOnlyList<double?> _history = [];
    private double _maximum = maximum;
    private Visibility _visibility = Visibility.Visible;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Title { get; } = title;

    public Brush Accent { get; } = accent;

    public string Value { get => _value; set => Set(ref _value, value); }

    public string Detail { get => _detail; set => Set(ref _detail, value); }

    public string ToolTip { get => _toolTip; set => Set(ref _toolTip, value); }

    public IReadOnlyList<double?> History { get => _history; set => Set(ref _history, value); }

    public double Maximum { get => _maximum; set => Set(ref _maximum, value); }

    public Visibility Visibility { get => _visibility; set => Set(ref _visibility, value); }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
