using System.ComponentModel;
using System.Runtime.CompilerServices;
using PerformanceTracker.Core.Models;
using PerformanceTracker.Core.Services;

namespace PerformanceTracker.Desktop;

public sealed class AgentRowViewModel : INotifyPropertyChanged
{
    private readonly Action<string, bool> _expansionChanged;
    private AgentNode _node;
    private bool _isExpanded;

    public AgentRowViewModel(AgentNode node, bool isExpanded, Action<string, bool> expansionChanged)
    {
        _node = node;
        _isExpanded = isExpanded;
        _expansionChanged = expansionChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id => _node.Agent.Id;

    public IReadOnlyList<AgentRowViewModel> Children { get; init; } = [];

    public string Title => _node.Agent.Title;

    public string ModelText => AgentTreeBuilder.FriendlyModel(_node.Agent.Model);

    public string ModelToolTip => _node.Agent.Model ?? "T3 Code didn't record a model for this agent.";

    public bool IsRunning => _node.Agent.IsRunning;

    public string StateText => IsRunning ? "Working" : "Waiting on helpers";

    public bool HasHelpers => _node.RunningBelow > 0 || _node.Agent.HelpersStarted > 0;

    public string HelpersText
    {
        get
        {
            int direct = Children.Count(child => child.IsRunning);
            string started = _node.Agent.HelpersStarted == 1 ? "1 helper started in total" : $"{_node.Agent.HelpersStarted} helpers started in total";
            string working = direct switch { 0 => "No helpers working right now", 1 => "1 helper working", _ => $"{direct} helpers working" };
            string deeper = _node.RunningBelow > direct ? $" ({_node.RunningBelow} counting their helpers)" : string.Empty;
            return $"{working}{deeper} · {started}";
        }
    }

    public string DetailText
    {
        get
        {
            List<string> parts = [];
            if (!string.IsNullOrWhiteSpace(_node.Agent.ProjectName))
            {
                parts.Add(_node.Agent.ProjectName!);
            }

            if (IsRunning && _node.Agent.StartedAtUtc is { } started)
            {
                parts.Add($"working for {FormatDuration(DateTimeOffset.UtcNow - started)}");
            }

            return string.Join(" · ", parts);
        }
    }

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
            _expansionChanged(Id, value);
            OnPropertyChanged();
        }
    }

    /// <summary>Takes fresh data for the same agent without rebuilding the tree, so scroll and focus stay put.</summary>
    public void Update(AgentNode node)
    {
        _node = node;
        OnPropertyChanged(string.Empty);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        return duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes}m {duration.Seconds}s" : $"{duration.Seconds}s";
    }
}
