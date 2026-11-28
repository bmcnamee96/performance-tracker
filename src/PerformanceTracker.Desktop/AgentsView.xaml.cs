using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using PerformanceTracker.Core.Models;
using PerformanceTracker.Core.Services;
using PerformanceTracker.Infrastructure.Services;
using UserControl = System.Windows.Controls.UserControl;

namespace PerformanceTracker.Desktop;

public partial class AgentsView : UserControl
{
    private readonly T3AgentReader _reader;
    private readonly DispatcherTimer _timer;
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private List<AgentRowViewModel> _roots = [];
    private string _shape = string.Empty;
    private bool _busy;

    public AgentsView(T3AgentReader reader)
    {
        InitializeComponent();
        _reader = reader;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        IsVisibleChanged += async (_, _) =>
        {
            // Only read T3 Code while someone is looking at this tab.
            if (IsVisible)
            {
                _timer.Start();
                await RefreshAsync();
            }
            else
            {
                _timer.Stop();
            }
        };
    }

    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void ExpandAllClick(object sender, RoutedEventArgs e) => SetExpanded(_roots, true);

    private void CollapseAllClick(object sender, RoutedEventArgs e) => SetExpanded(_roots, false);

    private async Task RefreshAsync()
    {
        if (_busy)
        {
            return;
        }

        if (!_reader.DatabaseExists)
        {
            ShowStatus($"T3 Code's data wasn't found at {_reader.DatabasePath}. Set T3_STATE_DATABASE if it lives somewhere else.");
            Show(AgentSnapshot.Empty);
            return;
        }

        _busy = true;
        try
        {
            IReadOnlyList<AgentRecord> records = await Task.Run(_reader.ReadActive);
            Show(AgentTreeBuilder.Build(records));
            Status.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Keep the last good tree on screen; T3 Code may simply be mid-write.
            ShowStatus($"Couldn't read T3 Code just now. {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private void Show(AgentSnapshot snapshot)
    {
        string shape = Shape(snapshot.Roots);
        if (shape == _shape)
        {
            Dictionary<string, AgentNode> byId = Flatten(snapshot.Roots).ToDictionary(node => node.Agent.Id, StringComparer.Ordinal);
            foreach (AgentRowViewModel row in Walk(_roots))
            {
                row.Update(byId[row.Id]);
            }
        }
        else
        {
            _shape = shape;
            _roots = snapshot.Roots.Select(ToRow).ToList();
            Tree.ItemsSource = _roots;
        }

        int threads = snapshot.Roots.Count;
        ThreadCount.Text = threads.ToString(CultureInfo.CurrentCulture);
        WorkingCount.Text = snapshot.RunningAgents.ToString(CultureInfo.CurrentCulture);
        WaitingCount.Text = Flatten(snapshot.Roots).Count(node => !node.Agent.IsRunning).ToString(CultureInfo.CurrentCulture);
        ModelChips.ItemsSource = snapshot.RunningByModel;
        NoModels.Visibility = snapshot.RunningByModel.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyMessage.Visibility = threads == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private AgentRowViewModel ToRow(AgentNode node) =>
        new(node, !_collapsed.Contains(node.Agent.Id), OnExpansionChanged)
        {
            Children = node.Children.Select(ToRow).ToArray()
        };

    private void OnExpansionChanged(string id, bool expanded)
    {
        if (expanded)
        {
            _collapsed.Remove(id);
        }
        else
        {
            _collapsed.Add(id);
        }
    }

    private static void SetExpanded(IEnumerable<AgentRowViewModel> rows, bool expanded)
    {
        foreach (AgentRowViewModel row in Walk(rows))
        {
            row.IsExpanded = expanded;
        }
    }

    /// <summary>What decides whether the tree must be rebuilt: who is where, and who is working.</summary>
    private static string Shape(IEnumerable<AgentNode> roots) =>
        string.Join("|", Flatten(roots).Select(node =>
            $"{node.Agent.Id}>{node.Agent.ParentId}:{node.Agent.IsRunning}:{node.Agent.HelpersStarted}:{node.RunningBelow}"));

    private static IEnumerable<AgentNode> Flatten(IEnumerable<AgentNode> nodes) =>
        nodes.SelectMany(node => Flatten(node.Children).Prepend(node));

    private static IEnumerable<AgentRowViewModel> Walk(IEnumerable<AgentRowViewModel> rows) =>
        rows.SelectMany(row => Walk(row.Children).Prepend(row));

    private void ShowStatus(string message)
    {
        Status.Text = message;
        Status.Visibility = Visibility.Visible;
    }
}
