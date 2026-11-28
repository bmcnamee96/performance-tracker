namespace PerformanceTracker.Core.Models;

/// <summary>
/// One T3 Code agent: a top-level thread, a delegated task, or a provider subagent.
/// Agents that are not running themselves appear only because a helper below them is.
/// </summary>
public sealed record AgentRecord(
    string Id,
    string? ParentId,
    string Title,
    string? Model,
    bool IsRunning,
    DateTimeOffset? StartedAtUtc,
    string? ProjectName,
    int HelpersStarted);

public sealed class AgentNode
{
    public AgentNode(AgentRecord agent) => Agent = agent;

    public AgentRecord Agent { get; }

    public List<AgentNode> Children { get; } = [];

    /// <summary>Running agents anywhere below this one.</summary>
    public int RunningBelow { get; internal set; }
}

public sealed record ModelCount(string Model, int Count);

public sealed record AgentSnapshot(IReadOnlyList<AgentNode> Roots, int RunningAgents, IReadOnlyList<ModelCount> RunningByModel)
{
    public static AgentSnapshot Empty { get; } = new([], 0, []);
}
