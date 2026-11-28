using Microsoft.Data.Sqlite;
using PerformanceTracker.Core.Models;
using PerformanceTracker.Core.Services;
using PerformanceTracker.Infrastructure.Services;
using Xunit;

namespace PerformanceTracker.Tests;

public sealed class AgentActivityTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-28T16:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void BuildsParentChildTreeAndCountsRunningHelpersAtEveryLevel()
    {
        AgentSnapshot snapshot = AgentTreeBuilder.Build(
        [
            Agent("lead", null, running: false, model: "claude-opus-5-5", helpers: 5),
            Agent("impl", "lead", running: true, model: "claude-opus-5-5", helpers: 2),
            Agent("review", "lead", running: true, model: "gpt-6.1-sol"),
            Agent("explore", "impl", running: true, model: "claude-haiku-5-5"),
            Agent("solo", null, running: true, model: "claude-opus-5-5")
        ]);

        Assert.Equal(["lead", "solo"], snapshot.Roots.Select(node => node.Agent.Id));
        AgentNode lead = snapshot.Roots[0];
        Assert.Equal(3, lead.RunningBelow);
        Assert.Equal(["impl", "review"], lead.Children.Select(node => node.Agent.Id));
        Assert.Equal(1, lead.Children[0].RunningBelow);
        Assert.Equal(4, snapshot.RunningAgents);
        Assert.Equal(
            [new ModelCount("Opus 5.5", 2), new ModelCount("gpt-6.1-sol", 1), new ModelCount("Haiku 5.5", 1)],
            snapshot.RunningByModel);
    }

    [Fact]
    public void OrphansAndCyclesStillShowUp()
    {
        AgentSnapshot snapshot = AgentTreeBuilder.Build(
        [
            Agent("a", "b", running: true),
            Agent("b", "a", running: true),
            Agent("orphan", "missing", running: true)
        ]);

        Assert.Equal(3, snapshot.RunningAgents);
        Assert.Equal(3, Count(snapshot.Roots));
    }

    [Theory]
    [InlineData("claude-opus-5-5", "Opus 5.5")]
    [InlineData("claude-fable-5-2", "Fable 5.2")]
    [InlineData("claude-haiku-4-5-20251001", "Haiku 4.5")]
    [InlineData("claude-sonnet-4-6[1m]", "Sonnet 4.6 (1M)")]
    [InlineData("gpt-6.1-sol", "gpt-6.1-sol")]
    [InlineData(null, "Unknown model")]
    public void ModelNamesAreReadable(string? model, string expected) =>
        Assert.Equal(expected, AgentTreeBuilder.FriendlyModel(model));

    [Fact]
    public void ReaderFollowsDelegatedTasksAndNestedSubagentsUpToTheirThread()
    {
        SQLitePCL.Batteries_V2.Init();
        string path = Path.Combine(Path.GetTempPath(), $"t3-state-{Guid.NewGuid():N}.sqlite");
        try
        {
            using (SqliteConnection connection = new($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                Execute(connection, """
                    CREATE TABLE orchestration_v2_projection_runs (run_id TEXT PRIMARY KEY, thread_id TEXT, status TEXT, requested_at TEXT, payload_json TEXT);
                    CREATE TABLE orchestration_v2_projection_subagents (subagent_id TEXT PRIMARY KEY, thread_id TEXT, parent_node_id TEXT,
                        child_thread_id TEXT, status TEXT, started_at TEXT, updated_at TEXT, payload_json TEXT);
                    CREATE TABLE orchestration_v2_projection_threads (thread_id TEXT PRIMARY KEY, project_id TEXT, title TEXT, created_at TEXT, payload_json TEXT);
                    CREATE TABLE projection_projects (project_id TEXT PRIMARY KEY, title TEXT);

                    INSERT INTO projection_projects VALUES ('p1', 'Fantasy-Val');
                    INSERT INTO orchestration_v2_projection_threads VALUES
                      ('lead', 'p1', 'Clip fixes', '2026-11-28T15:00:00Z', '{"modelSelection":{"model":"claude-opus-5-5"},"lineage":{"parentThreadId":null}}'),
                      ('impl', 'p1', 'Impl-B clip fixes', '2026-11-28T16:01:00Z', '{"modelSelection":{"model":"claude-opus-5-5"},"lineage":{"parentThreadId":"lead","relationshipToParent":"subagent"}}'),
                      ('thread:subagent:x', 'p1', 'Explore draft code', '2026-11-28T16:02:00Z', '{"modelSelection":{"model":"claude-haiku-5-5"},"lineage":{"parentThreadId":"impl","relationshipToParent":"subagent"}}'),
                      ('thread:subagent:y', 'p1', 'Nested reader', '2026-11-28T16:03:00Z', '{"lineage":{"parentThreadId":"impl","relationshipToParent":"subagent"}}');

                    -- Only the delegated task has a live run; the lead thread is waiting on it.
                    INSERT INTO orchestration_v2_projection_runs VALUES
                      ('r1', 'lead', 'completed', '2026-11-28T15:00:00Z', '{"modelSelection":{"model":"claude-opus-5-5"}}'),
                      ('r2', 'impl', 'running', '2026-11-28T16:01:00Z', '{"modelSelection":{"model":"claude-opus-5-5"},"startedAt":"2026-11-28T16:01:01Z"}');

                    INSERT INTO orchestration_v2_projection_subagents VALUES
                      ('node:delegated-task:1', 'lead', 'node:run:lead:root', 'impl', 'completed', '2026-11-28T16:01:00Z', '2026-11-28T16:01:00Z', '{"title":"Impl-B","model":"claude-opus-5-5"}'),
                      ('node:delegated-task:2', 'lead', 'node:run:lead:root', 'impl', 'running', '2026-11-28T16:05:00Z', '2026-11-28T16:05:00Z', '{"title":"Follow-up","model":"claude-opus-5-5"}'),
                      ('node:delegated-task:3', 'lead', 'node:run:lead:root', 'review', 'completed', '2026-11-28T16:00:00Z', '2026-11-28T16:00:30Z', '{"title":"Review","model":"gpt-6.1-sol"}'),
                      ('node:provider:explore', 'impl', 'node:run:impl:root', 'thread:subagent:x', 'running', '2026-11-28T16:02:00Z', '2026-11-28T16:02:00Z', '{"title":"Explore","model":"claude-haiku-5-5"}'),
                      ('node:provider:nested', 'impl', 'node:provider:explore', 'thread:subagent:y', 'running', '2026-11-28T16:03:00Z', '2026-11-28T16:03:00Z', '{"title":"Nested","model":"claude-haiku-5-5"}');
                    """);
            }

            Dictionary<string, AgentRecord> agents = new T3AgentReader(path).ReadActive().ToDictionary(agent => agent.Id);

            Assert.Equal(["impl", "lead", "thread:subagent:x", "thread:subagent:y"], agents.Keys.Order());
            Assert.False(agents["lead"].IsRunning);
            Assert.Equal(2, agents["lead"].HelpersStarted);
            Assert.Equal("Fantasy-Val", agents["lead"].ProjectName);

            AgentRecord impl = agents["impl"];
            Assert.Equal("lead", impl.ParentId);
            Assert.Equal("Impl-B clip fixes", impl.Title);
            Assert.True(impl.IsRunning);
            Assert.Equal(1, impl.HelpersStarted);
            Assert.Equal(DateTimeOffset.Parse("2026-11-28T16:01:01Z", System.Globalization.CultureInfo.InvariantCulture), impl.StartedAtUtc);

            Assert.Equal("impl", agents["thread:subagent:x"].ParentId);
            Assert.Equal(1, agents["thread:subagent:x"].HelpersStarted);
            Assert.Equal("thread:subagent:x", agents["thread:subagent:y"].ParentId);
            Assert.Equal("claude-haiku-5-5", agents["thread:subagent:y"].Model);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static AgentRecord Agent(string id, string? parent, bool running, string? model = null, int helpers = 0) =>
        new(id, parent, id, model, running, T0.AddMinutes(id.Length), null, helpers);

    private static int Count(IEnumerable<AgentNode> nodes) => nodes.Sum(node => 1 + Count(node.Children));

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
