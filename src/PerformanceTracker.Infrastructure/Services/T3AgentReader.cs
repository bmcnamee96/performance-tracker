using System.Globalization;
using Microsoft.Data.Sqlite;
using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Infrastructure.Services;

/// <summary>
/// Reads the agents T3 Code is running from its local state database. The database is opened
/// read-only and never written to; T3 Code owns it and can change its layout in any release.
/// </summary>
public sealed class T3AgentReader
{
    // The same list as T3 Code's own index of unfinished runs, so the lookup stays cheap.
    private const string ActiveRunStatuses = "'queued', 'preparing', 'starting', 'running', 'waiting'";
    private const string FinishedStatuses = "'completed', 'failed', 'cancelled', 'interrupted', 'rolled_back'";
    private const int MaxAncestorDepth = 32;

    private static readonly object SqliteInitLock = new();
    private static bool _sqliteInitialized;

    public T3AgentReader(string databasePath) => DatabasePath = databasePath;

    public string DatabasePath { get; }

    public static string DefaultDatabasePath =>
        Environment.GetEnvironmentVariable("T3_STATE_DATABASE") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".t3", "userdata", "statev2.sqlite");

    public bool DatabaseExists => File.Exists(DatabasePath);

    public IReadOnlyList<AgentRecord> ReadActive()
    {
        EnsureSqliteInitialized();
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();

        using SqliteConnection connection = new(connectionString);
        connection.Open();
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA query_only = 1;";
            pragma.ExecuteNonQuery();
        }

        using SqliteTransaction snapshot = connection.BeginTransaction(deferred: true);
        Dictionary<string, Activity> active = ReadActiveRuns(connection);
        foreach ((string id, Activity activity) in ReadActiveSubagents(connection))
        {
            active.TryAdd(id, activity);
        }

        Dictionary<string, AgentRecord> records = new(StringComparer.Ordinal);
        Queue<(string Id, int Depth)> pending = new(active.Keys.Select(id => (id, 0)));
        while (pending.TryDequeue(out (string Id, int Depth) item))
        {
            if (records.ContainsKey(item.Id))
            {
                continue;
            }

            Spawn? spawn = ReadSpawn(connection, item.Id);
            ThreadInfo? thread = ReadThread(connection, item.Id);
            active.TryGetValue(item.Id, out Activity? activity);
            string? parentId = spawn?.ParentId ?? thread?.LineageParentId;

            records[item.Id] = new AgentRecord(
                item.Id,
                parentId,
                FirstText(thread?.Title, spawn?.Title, activity?.Title) ?? "Untitled agent",
                FirstText(activity?.Model, spawn?.Model, thread?.Model),
                activity is not null,
                activity?.StartedAtUtc ?? spawn?.StartedAtUtc ?? thread?.CreatedAtUtc,
                thread?.ProjectName,
                CountHelpersStarted(connection, item.Id));

            if (parentId is not null && item.Depth < MaxAncestorDepth)
            {
                pending.Enqueue((parentId, item.Depth + 1));
            }
        }

        return records.Values.ToArray();
    }

    private static Dictionary<string, Activity> ReadActiveRuns(SqliteConnection connection)
    {
        Dictionary<string, Activity> active = new(StringComparer.Ordinal);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT thread_id,
                   json_extract(payload_json, '$.modelSelection.model'),
                   coalesce(json_extract(payload_json, '$.startedAt'), requested_at)
            FROM orchestration_v2_projection_runs
            WHERE status IN ({ActiveRunStatuses})
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            active[reader.GetString(0)] = new Activity(null, Text(reader, 1), Time(reader, 2));
        }

        return active;
    }

    private static List<(string Id, Activity Activity)> ReadActiveSubagents(SqliteConnection connection)
    {
        List<(string Id, Activity Activity)> active = [];
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT coalesce(child_thread_id, subagent_id),
                   json_extract(payload_json, '$.title'),
                   json_extract(payload_json, '$.model'),
                   started_at
            FROM orchestration_v2_projection_subagents
            WHERE status NOT IN ({FinishedStatuses})
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            active.Add((reader.GetString(0), new Activity(Text(reader, 1), Text(reader, 2), Time(reader, 3))));
        }

        return active;
    }

    /// <summary>Who started this agent. The first spawn wins: later messages to the same task are not new parents.</summary>
    private static Spawn? ReadSpawn(SqliteConnection connection, string id)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.thread_id,
                   parent.child_thread_id,
                   json_extract(s.payload_json, '$.title'),
                   json_extract(s.payload_json, '$.model'),
                   s.started_at
            FROM orchestration_v2_projection_subagents s
            LEFT JOIN orchestration_v2_projection_subagents parent ON parent.subagent_id = s.parent_node_id
            WHERE s.child_thread_id = $id OR s.subagent_id = $id
            ORDER BY s.started_at IS NULL, s.started_at
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        // A subagent started by another subagent hangs off that subagent's node, not the thread's run.
        string parentId = Text(reader, 1) ?? reader.GetString(0);
        return new Spawn(parentId == id ? null : parentId, Text(reader, 2), Text(reader, 3), Time(reader, 4));
    }

    private static ThreadInfo? ReadThread(SqliteConnection connection, string id)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.title,
                   json_extract(t.payload_json, '$.modelSelection.model'),
                   CASE WHEN json_extract(t.payload_json, '$.lineage.relationshipToParent') = 'subagent'
                        THEN json_extract(t.payload_json, '$.lineage.parentThreadId') END,
                   t.created_at,
                   p.title
            FROM orchestration_v2_projection_threads t
            LEFT JOIN projection_projects p ON p.project_id = t.project_id
            WHERE t.thread_id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? new ThreadInfo(Text(reader, 0), Text(reader, 1), Text(reader, 2), Time(reader, 3), Text(reader, 4))
            : null;
    }

    /// <summary>Every helper this agent has started, finished or not.</summary>
    private static int CountHelpersStarted(SqliteConnection connection, string id)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(DISTINCT helper) FROM (
                SELECT coalesce(child_thread_id, subagent_id) AS helper
                FROM orchestration_v2_projection_subagents
                WHERE thread_id = $id AND parent_node_id LIKE 'node:run:%'
                UNION ALL
                SELECT coalesce(child.child_thread_id, child.subagent_id)
                FROM orchestration_v2_projection_subagents own
                JOIN orchestration_v2_projection_subagents child ON child.parent_node_id = own.subagent_id
                WHERE own.child_thread_id = $id OR own.subagent_id = $id
            )
            WHERE helper <> $id
            """;
        command.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string? FirstText(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal).ToString();

    private static DateTimeOffset? Time(SqliteDataReader reader, int ordinal) =>
        Text(reader, ordinal) is { } text &&
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset value)
            ? value
            : null;

    private static void EnsureSqliteInitialized()
    {
        lock (SqliteInitLock)
        {
            if (!_sqliteInitialized)
            {
                SQLitePCL.Batteries_V2.Init();
                _sqliteInitialized = true;
            }
        }
    }

    private sealed record Activity(string? Title, string? Model, DateTimeOffset? StartedAtUtc);

    private sealed record Spawn(string? ParentId, string? Title, string? Model, DateTimeOffset? StartedAtUtc);

    private sealed record ThreadInfo(string? Title, string? Model, string? LineageParentId, DateTimeOffset? CreatedAtUtc, string? ProjectName);
}
