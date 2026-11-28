using System.Text.RegularExpressions;
using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Core.Services;

public static partial class AgentTreeBuilder
{
    public static AgentSnapshot Build(IEnumerable<AgentRecord> records)
    {
        Dictionary<string, AgentNode> nodes = new(StringComparer.Ordinal);
        foreach (AgentRecord record in records)
        {
            nodes[record.Id] = new AgentNode(record);
        }

        List<AgentNode> roots = [];
        foreach (AgentNode node in nodes.Values)
        {
            if (node.Agent.ParentId is { } parentId &&
                nodes.TryGetValue(parentId, out AgentNode? parent) &&
                !IsAncestor(node, parent, nodes))
            {
                parent.Children.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        foreach (AgentNode root in roots)
        {
            CountAndSort(root);
        }

        roots.Sort(Compare);
        ModelCount[] byModel = nodes.Values
            .Where(node => node.Agent.IsRunning)
            .GroupBy(node => FriendlyModel(node.Agent.Model))
            .Select(group => new ModelCount(group.Key, group.Count()))
            .OrderByDescending(count => count.Count)
            .ThenBy(count => count.Model, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new AgentSnapshot(roots, nodes.Values.Count(node => node.Agent.IsRunning), byModel);
    }

    /// <summary>"claude-opus-5-5" becomes "Opus 5.5"; other model ids are shown as they are.</summary>
    public static string FriendlyModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return "Unknown model";
        }

        Match match = ClaudeModel().Match(model.Trim());
        if (!match.Success)
        {
            return model.Trim();
        }

        string family = match.Groups["family"].Value;
        string name = char.ToUpperInvariant(family[0]) + family[1..];
        string version = match.Groups["minor"].Success
            ? $"{match.Groups["major"].Value}.{match.Groups["minor"].Value}"
            : match.Groups["major"].Value;
        return match.Groups["long"].Success ? $"{name} {version} (1M)" : $"{name} {version}";
    }

    private static bool IsAncestor(AgentNode candidate, AgentNode start, Dictionary<string, AgentNode> nodes)
    {
        // Guards against a cycle in the source data, which would otherwise hide both agents.
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (AgentNode? current = start; current is not null && seen.Add(current.Agent.Id);)
        {
            if (ReferenceEquals(current, candidate))
            {
                return true;
            }

            current = current.Agent.ParentId is { } parentId && nodes.TryGetValue(parentId, out AgentNode? parent) ? parent : null;
        }

        return false;
    }

    private static int CountAndSort(AgentNode node)
    {
        int running = 0;
        foreach (AgentNode child in node.Children)
        {
            running += CountAndSort(child) + (child.Agent.IsRunning ? 1 : 0);
        }

        node.Children.Sort(Compare);
        node.RunningBelow = running;
        return running;
    }

    private static int Compare(AgentNode left, AgentNode right)
    {
        // Oldest first, so a refresh never reorders agents that are already on screen.
        int byStart = Nullable.Compare(left.Agent.StartedAtUtc, right.Agent.StartedAtUtc);
        return byStart != 0 ? byStart : string.CompareOrdinal(left.Agent.Id, right.Agent.Id);
    }

    [GeneratedRegex(@"^claude-(?<family>[a-z]+)-(?<major>\d+)(?:-(?<minor>\d{1,2}))?(?:-\d{8})?(?<long>\[1m\])?$", RegexOptions.IgnoreCase)]
    private static partial Regex ClaudeModel();
}
