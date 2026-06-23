using Microsoft.Data.Sqlite;
using PerformanceTracker.Core.Interfaces;
using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Infrastructure.Storage;

public sealed class SqliteSampleRepository : ISampleRepository
{
    private readonly string _databasePath;
    private static bool _sqliteInitialized;
    private static readonly object SqliteInitLock = new();
    private static readonly (string Name, string Definition)[] MetricSampleColumns =
    [
        ("timestamp_utc", "timestamp_utc TEXT NOT NULL"),
        ("cpu_usage_percent", "cpu_usage_percent REAL NULL"),
        ("cpu_frequency_mhz", "cpu_frequency_mhz REAL NULL"),
        ("cpu_queue_length", "cpu_queue_length REAL NULL"),
        ("memory_used_percent", "memory_used_percent REAL NOT NULL DEFAULT 0"),
        ("available_memory_mb", "available_memory_mb INTEGER NOT NULL DEFAULT 0"),
        ("commit_used_percent", "commit_used_percent REAL NULL"),
        ("disk_active_time_percent", "disk_active_time_percent REAL NULL"),
        ("disk_read_mb_per_sec", "disk_read_mb_per_sec REAL NULL"),
        ("disk_write_mb_per_sec", "disk_write_mb_per_sec REAL NULL"),
        ("disk_queue_length", "disk_queue_length REAL NULL"),
        ("disk_average_response_ms", "disk_average_response_ms REAL NULL"),
        ("network_receive_kbps", "network_receive_kbps REAL NULL"),
        ("network_send_kbps", "network_send_kbps REAL NULL"),
        ("network_latency_ms", "network_latency_ms REAL NULL"),
        ("network_retransmits_per_sec", "network_retransmits_per_sec REAL NULL"),
        ("gpu_usage_percent", "gpu_usage_percent REAL NULL"),
        ("gpu_memory_used_percent", "gpu_memory_used_percent REAL NULL"),
        ("gpu_dedicated_memory_mb", "gpu_dedicated_memory_mb REAL NULL"),
        ("gpu_shared_memory_mb", "gpu_shared_memory_mb REAL NULL"),
        ("gpu_temperature_celsius", "gpu_temperature_celsius REAL NULL"),
        ("top_process_name", "top_process_name TEXT NULL"),
        ("top_process_memory_mb", "top_process_memory_mb REAL NULL"),
        ("game_process_name", "game_process_name TEXT NULL"),
        ("gaming_telemetry_active", "gaming_telemetry_active INTEGER NOT NULL DEFAULT 0"),
        ("frames_per_second", "frames_per_second REAL NULL"),
        ("frame_time_ms", "frame_time_ms REAL NULL"),
        ("frame_time_p95_ms", "frame_time_p95_ms REAL NULL"),
        ("input_latency_ms", "input_latency_ms REAL NULL"),
        ("render_latency_ms", "render_latency_ms REAL NULL"),
        ("display_refresh_rate_hz", "display_refresh_rate_hz REAL NULL"),
        ("variable_refresh_rate_enabled", "variable_refresh_rate_enabled INTEGER NULL"),
        ("game_mode_enabled", "game_mode_enabled INTEGER NULL"),
        ("uptime_seconds", "uptime_seconds INTEGER NOT NULL DEFAULT 0")
    ];
    private static readonly (string Name, string Definition)[] PerformanceEventColumns =
    [
        ("started_at_utc", "started_at_utc TEXT NOT NULL"),
        ("ended_at_utc", "ended_at_utc TEXT NOT NULL"),
        ("severity", "severity INTEGER NOT NULL"),
        ("summary", "summary TEXT NOT NULL"),
        ("likely_cause", "likely_cause TEXT NOT NULL"),
        ("foreground_app", "foreground_app TEXT NULL"),
        ("trigger_cpu_percent", "trigger_cpu_percent REAL NULL"),
        ("trigger_cpu_frequency_mhz", "trigger_cpu_frequency_mhz REAL NULL"),
        ("trigger_cpu_queue_length", "trigger_cpu_queue_length REAL NULL"),
        ("trigger_memory_percent", "trigger_memory_percent REAL NOT NULL DEFAULT 0"),
        ("trigger_available_memory_mb", "trigger_available_memory_mb INTEGER NULL"),
        ("trigger_commit_percent", "trigger_commit_percent REAL NULL"),
        ("trigger_disk_percent", "trigger_disk_percent REAL NULL"),
        ("trigger_disk_queue_length", "trigger_disk_queue_length REAL NULL"),
        ("trigger_disk_response_ms", "trigger_disk_response_ms REAL NULL"),
        ("trigger_network_latency_ms", "trigger_network_latency_ms REAL NULL"),
        ("trigger_gpu_percent", "trigger_gpu_percent REAL NULL"),
        ("trigger_gpu_memory_percent", "trigger_gpu_memory_percent REAL NULL"),
        ("trigger_gpu_temperature_celsius", "trigger_gpu_temperature_celsius REAL NULL"),
        ("trigger_game_process_name", "trigger_game_process_name TEXT NULL"),
        ("trigger_gaming_telemetry_active", "trigger_gaming_telemetry_active INTEGER NOT NULL DEFAULT 0"),
        ("trigger_fps", "trigger_fps REAL NULL"),
        ("trigger_frame_time_ms", "trigger_frame_time_ms REAL NULL"),
        ("trigger_frame_time_p95_ms", "trigger_frame_time_p95_ms REAL NULL"),
        ("trigger_input_latency_ms", "trigger_input_latency_ms REAL NULL"),
        ("trigger_render_latency_ms", "trigger_render_latency_ms REAL NULL"),
        ("trigger_refresh_rate_hz", "trigger_refresh_rate_hz REAL NULL"),
        ("trigger_variable_refresh_rate_enabled", "trigger_variable_refresh_rate_enabled INTEGER NULL"),
        ("trigger_game_mode_enabled", "trigger_game_mode_enabled INTEGER NULL")
    ];

    public SqliteSampleRepository(string databasePath)
    {
        EnsureSqliteInitialized();
        _databasePath = databasePath;
    }

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS metric_samples (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              timestamp_utc TEXT NOT NULL,
              cpu_usage_percent REAL NULL,
              cpu_frequency_mhz REAL NULL,
              cpu_queue_length REAL NULL,
              memory_used_percent REAL NOT NULL,
              available_memory_mb INTEGER NOT NULL,
              commit_used_percent REAL NULL,
              disk_active_time_percent REAL NULL,
              disk_read_mb_per_sec REAL NULL,
              disk_write_mb_per_sec REAL NULL,
              disk_queue_length REAL NULL,
              disk_average_response_ms REAL NULL,
              network_receive_kbps REAL NULL,
              network_send_kbps REAL NULL,
              network_latency_ms REAL NULL,
              network_retransmits_per_sec REAL NULL,
              gpu_usage_percent REAL NULL,
              gpu_memory_used_percent REAL NULL,
              gpu_dedicated_memory_mb REAL NULL,
              gpu_shared_memory_mb REAL NULL,
              gpu_temperature_celsius REAL NULL,
              top_process_name TEXT NULL,
              top_process_memory_mb REAL NULL,
              game_process_name TEXT NULL,
              gaming_telemetry_active INTEGER NOT NULL DEFAULT 0,
              frames_per_second REAL NULL,
              frame_time_ms REAL NULL,
              frame_time_p95_ms REAL NULL,
              input_latency_ms REAL NULL,
              render_latency_ms REAL NULL,
              display_refresh_rate_hz REAL NULL,
              variable_refresh_rate_enabled INTEGER NULL,
              game_mode_enabled INTEGER NULL,
              uptime_seconds INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS performance_events (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              started_at_utc TEXT NOT NULL,
              ended_at_utc TEXT NOT NULL,
              severity INTEGER NOT NULL,
              summary TEXT NOT NULL,
              likely_cause TEXT NOT NULL,
              foreground_app TEXT NULL,
              trigger_cpu_percent REAL NULL,
              trigger_cpu_frequency_mhz REAL NULL,
              trigger_cpu_queue_length REAL NULL,
              trigger_memory_percent REAL NOT NULL,
              trigger_available_memory_mb INTEGER NULL,
              trigger_commit_percent REAL NULL,
              trigger_disk_percent REAL NULL,
              trigger_disk_queue_length REAL NULL,
              trigger_disk_response_ms REAL NULL,
              trigger_network_latency_ms REAL NULL,
              trigger_gpu_percent REAL NULL,
              trigger_gpu_memory_percent REAL NULL,
              trigger_gpu_temperature_celsius REAL NULL,
              trigger_game_process_name TEXT NULL,
              trigger_gaming_telemetry_active INTEGER NOT NULL DEFAULT 0,
              trigger_fps REAL NULL,
              trigger_frame_time_ms REAL NULL,
              trigger_frame_time_p95_ms REAL NULL,
              trigger_input_latency_ms REAL NULL,
              trigger_render_latency_ms REAL NULL,
              trigger_refresh_rate_hz REAL NULL,
              trigger_variable_refresh_rate_enabled INTEGER NULL,
              trigger_game_mode_enabled INTEGER NULL
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnsAsync(connection, "metric_samples", MetricSampleColumns, cancellationToken);
        await EnsureColumnsAsync(connection, "performance_events", PerformanceEventColumns, cancellationToken);
    }

    public async Task SaveMetricSampleAsync(MetricSample sample, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO metric_samples (
              timestamp_utc,
              cpu_usage_percent,
              cpu_frequency_mhz,
              cpu_queue_length,
              memory_used_percent,
              available_memory_mb,
              commit_used_percent,
              disk_active_time_percent,
              disk_read_mb_per_sec,
              disk_write_mb_per_sec,
              disk_queue_length,
              disk_average_response_ms,
              network_receive_kbps,
              network_send_kbps,
              network_latency_ms,
              network_retransmits_per_sec,
              gpu_usage_percent,
              gpu_memory_used_percent,
              gpu_dedicated_memory_mb,
              gpu_shared_memory_mb,
              gpu_temperature_celsius,
              top_process_name,
              top_process_memory_mb,
              game_process_name,
              gaming_telemetry_active,
              frames_per_second,
              frame_time_ms,
              frame_time_p95_ms,
              input_latency_ms,
              render_latency_ms,
              display_refresh_rate_hz,
              variable_refresh_rate_enabled,
              game_mode_enabled,
              uptime_seconds
            )
            VALUES (
              $timestampUtc,
              $cpuUsagePercent,
              $cpuFrequencyMHz,
              $cpuQueueLength,
              $memoryUsedPercent,
              $availableMemoryMb,
              $commitUsedPercent,
              $diskActiveTimePercent,
              $diskReadMbPerSec,
              $diskWriteMbPerSec,
              $diskQueueLength,
              $diskAverageResponseMs,
              $networkReceiveKbps,
              $networkSendKbps,
              $networkLatencyMs,
              $networkRetransmitsPerSec,
              $gpuUsagePercent,
              $gpuMemoryUsedPercent,
              $gpuDedicatedMemoryMb,
              $gpuSharedMemoryMb,
              $gpuTemperatureCelsius,
              $topProcessName,
              $topProcessMemoryMb,
              $gameProcessName,
              $gamingTelemetryActive,
              $framesPerSecond,
              $frameTimeMs,
              $frameTimeP95Ms,
              $inputLatencyMs,
              $renderLatencyMs,
              $displayRefreshRateHz,
              $variableRefreshRateEnabled,
              $gameModeEnabled,
              $uptimeSeconds
            );
            """;

        AddNullable(command, "$cpuUsagePercent", sample.CpuUsagePercent);
        AddNullable(command, "$cpuFrequencyMHz", sample.CpuFrequencyMHz);
        AddNullable(command, "$cpuQueueLength", sample.CpuQueueLength);
        AddNullable(command, "$commitUsedPercent", sample.CommitUsedPercent);
        AddNullable(command, "$diskActiveTimePercent", sample.DiskActiveTimePercent);
        AddNullable(command, "$diskReadMbPerSec", sample.DiskReadMbPerSec);
        AddNullable(command, "$diskWriteMbPerSec", sample.DiskWriteMbPerSec);
        AddNullable(command, "$diskQueueLength", sample.DiskQueueLength);
        AddNullable(command, "$diskAverageResponseMs", sample.DiskAverageResponseMs);
        AddNullable(command, "$networkReceiveKbps", sample.NetworkReceiveKbps);
        AddNullable(command, "$networkSendKbps", sample.NetworkSendKbps);
        AddNullable(command, "$networkLatencyMs", sample.NetworkLatencyMs);
        AddNullable(command, "$networkRetransmitsPerSec", sample.NetworkRetransmitsPerSec);
        AddNullable(command, "$gpuUsagePercent", sample.GpuUsagePercent);
        AddNullable(command, "$gpuMemoryUsedPercent", sample.GpuMemoryUsedPercent);
        AddNullable(command, "$gpuDedicatedMemoryMb", sample.GpuDedicatedMemoryMb);
        AddNullable(command, "$gpuSharedMemoryMb", sample.GpuSharedMemoryMb);
        AddNullable(command, "$gpuTemperatureCelsius", sample.GpuTemperatureCelsius);
        AddNullable(command, "$topProcessMemoryMb", sample.TopProcessMemoryMb);
        AddNullable(command, "$framesPerSecond", sample.FramesPerSecond);
        AddNullable(command, "$frameTimeMs", sample.FrameTimeMs);
        AddNullable(command, "$frameTimeP95Ms", sample.FrameTimeP95Ms);
        AddNullable(command, "$inputLatencyMs", sample.InputLatencyMs);
        AddNullable(command, "$renderLatencyMs", sample.RenderLatencyMs);
        AddNullable(command, "$displayRefreshRateHz", sample.DisplayRefreshRateHz);
        AddNullable(command, "$variableRefreshRateEnabled", sample.VariableRefreshRateEnabled);
        AddNullable(command, "$gameModeEnabled", sample.GameModeEnabled);

        command.Parameters.AddWithValue("$timestampUtc", sample.TimestampUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$memoryUsedPercent", sample.MemoryUsedPercent);
        command.Parameters.AddWithValue("$availableMemoryMb", sample.AvailableMemoryMb);
        command.Parameters.AddWithValue("$topProcessName", (object?)sample.TopProcessName ?? DBNull.Value);
        command.Parameters.AddWithValue("$gameProcessName", (object?)sample.GameProcessName ?? DBNull.Value);
        command.Parameters.AddWithValue("$gamingTelemetryActive", sample.GamingTelemetryActive ? 1 : 0);
        command.Parameters.AddWithValue("$uptimeSeconds", (long)sample.Uptime.TotalSeconds);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SavePerformanceEventAsync(PerformanceEvent performanceEvent, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);

        SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO performance_events (
              started_at_utc,
              ended_at_utc,
              severity,
              summary,
              likely_cause,
              foreground_app,
              trigger_cpu_percent,
              trigger_cpu_frequency_mhz,
              trigger_cpu_queue_length,
              trigger_memory_percent,
              trigger_available_memory_mb,
              trigger_commit_percent,
              trigger_disk_percent,
              trigger_disk_queue_length,
              trigger_disk_response_ms,
              trigger_network_latency_ms,
              trigger_gpu_percent,
              trigger_gpu_memory_percent,
              trigger_gpu_temperature_celsius,
              trigger_game_process_name,
              trigger_gaming_telemetry_active,
              trigger_fps,
              trigger_frame_time_ms,
              trigger_frame_time_p95_ms,
              trigger_input_latency_ms,
              trigger_render_latency_ms,
              trigger_refresh_rate_hz,
              trigger_variable_refresh_rate_enabled,
              trigger_game_mode_enabled
            )
            VALUES (
              $startedAtUtc,
              $endedAtUtc,
              $severity,
              $summary,
              $likelyCause,
              $foregroundApp,
              $triggerCpuPercent,
              $triggerCpuFrequencyMHz,
              $triggerCpuQueueLength,
              $triggerMemoryPercent,
              $triggerAvailableMemoryMb,
              $triggerCommitPercent,
              $triggerDiskPercent,
              $triggerDiskQueueLength,
              $triggerDiskResponseMs,
              $triggerNetworkLatencyMs,
              $triggerGpuPercent,
              $triggerGpuMemoryPercent,
              $triggerGpuTemperatureCelsius,
              $triggerGameProcessName,
              $triggerGamingTelemetryActive,
              $triggerFps,
              $triggerFrameTimeMs,
              $triggerFrameTimeP95Ms,
              $triggerInputLatencyMs,
              $triggerRenderLatencyMs,
              $triggerRefreshRateHz,
              $triggerVariableRefreshRateEnabled,
              $triggerGameModeEnabled
            );
            """;

        AddNullable(command, "$triggerCpuPercent", performanceEvent.TriggerSample.CpuUsagePercent);
        AddNullable(command, "$triggerCpuFrequencyMHz", performanceEvent.TriggerSample.CpuFrequencyMHz);
        AddNullable(command, "$triggerCpuQueueLength", performanceEvent.TriggerSample.CpuQueueLength);
        AddNullable(command, "$triggerAvailableMemoryMb", (long?)performanceEvent.TriggerSample.AvailableMemoryMb);
        AddNullable(command, "$triggerCommitPercent", performanceEvent.TriggerSample.CommitUsedPercent);
        AddNullable(command, "$triggerDiskPercent", performanceEvent.TriggerSample.DiskActiveTimePercent);
        AddNullable(command, "$triggerDiskQueueLength", performanceEvent.TriggerSample.DiskQueueLength);
        AddNullable(command, "$triggerDiskResponseMs", performanceEvent.TriggerSample.DiskAverageResponseMs);
        AddNullable(command, "$triggerNetworkLatencyMs", performanceEvent.TriggerSample.NetworkLatencyMs);
        AddNullable(command, "$triggerGpuPercent", performanceEvent.TriggerSample.GpuUsagePercent);
        AddNullable(command, "$triggerGpuMemoryPercent", performanceEvent.TriggerSample.GpuMemoryUsedPercent);
        AddNullable(command, "$triggerGpuTemperatureCelsius", performanceEvent.TriggerSample.GpuTemperatureCelsius);
        AddNullable(command, "$triggerFps", performanceEvent.TriggerSample.FramesPerSecond);
        AddNullable(command, "$triggerFrameTimeMs", performanceEvent.TriggerSample.FrameTimeMs);
        AddNullable(command, "$triggerFrameTimeP95Ms", performanceEvent.TriggerSample.FrameTimeP95Ms);
        AddNullable(command, "$triggerInputLatencyMs", performanceEvent.TriggerSample.InputLatencyMs);
        AddNullable(command, "$triggerRenderLatencyMs", performanceEvent.TriggerSample.RenderLatencyMs);
        AddNullable(command, "$triggerRefreshRateHz", performanceEvent.TriggerSample.DisplayRefreshRateHz);
        AddNullable(command, "$triggerVariableRefreshRateEnabled", performanceEvent.TriggerSample.VariableRefreshRateEnabled);
        AddNullable(command, "$triggerGameModeEnabled", performanceEvent.TriggerSample.GameModeEnabled);

        command.Parameters.AddWithValue("$startedAtUtc", performanceEvent.StartedAtUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$endedAtUtc", performanceEvent.EndedAtUtc.UtcDateTime.ToString("O"));
        command.Parameters.AddWithValue("$severity", (int)performanceEvent.Severity);
        command.Parameters.AddWithValue("$summary", performanceEvent.Summary);
        command.Parameters.AddWithValue("$likelyCause", performanceEvent.LikelyCause);
        command.Parameters.AddWithValue("$foregroundApp", (object?)performanceEvent.ForegroundApp ?? DBNull.Value);
        command.Parameters.AddWithValue("$triggerGameProcessName", (object?)performanceEvent.TriggerSample.GameProcessName ?? DBNull.Value);
        command.Parameters.AddWithValue("$triggerGamingTelemetryActive", performanceEvent.TriggerSample.GamingTelemetryActive ? 1 : 0);
        command.Parameters.AddWithValue("$triggerMemoryPercent", performanceEvent.TriggerSample.MemoryUsedPercent);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection CreateConnection() => new($"Data Source={_databasePath}");

    private static void AddNullable(SqliteCommand command, string parameterName, double? value)
    {
        command.Parameters.AddWithValue(parameterName, value is null ? DBNull.Value : value);
    }

    private static void AddNullable(SqliteCommand command, string parameterName, long? value)
    {
        command.Parameters.AddWithValue(parameterName, value is null ? DBNull.Value : value);
    }

    private static void AddNullable(SqliteCommand command, string parameterName, bool? value)
    {
        command.Parameters.AddWithValue(parameterName, value is null ? DBNull.Value : value.Value ? 1 : 0);
    }

    private static async Task EnsureColumnsAsync(
        SqliteConnection connection,
        string tableName,
        IReadOnlyList<(string Name, string Definition)> columns,
        CancellationToken cancellationToken)
    {
        HashSet<string> existingColumns = await GetColumnNamesAsync(connection, tableName, cancellationToken);

        foreach ((string name, string definition) in columns)
        {
            if (existingColumns.Contains(name))
            {
                continue;
            }

            SqliteCommand addColumnCommand = connection.CreateCommand();
            addColumnCommand.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {definition};";
            await addColumnCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<HashSet<string>> GetColumnNamesAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName});";

        HashSet<string> columnNames = new(StringComparer.OrdinalIgnoreCase);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            columnNames.Add(reader.GetString(1));
        }

        return columnNames;
    }

    private static void EnsureSqliteInitialized()
    {
        if (_sqliteInitialized)
        {
            return;
        }

        lock (SqliteInitLock)
        {
            if (_sqliteInitialized)
            {
                return;
            }

            SQLitePCL.Batteries_V2.Init();
            _sqliteInitialized = true;
        }
    }
}
