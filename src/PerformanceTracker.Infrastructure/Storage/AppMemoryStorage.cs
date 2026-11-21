using Microsoft.Data.Sqlite;
using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Infrastructure.Storage;

public sealed partial class SqliteSampleRepository
{
    public void SaveAppMemorySamples(IReadOnlyList<AppMemorySample> samples, DateTimeOffset? pruneBefore)
    {
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO app_memory_samples
            (minute, app_key, display_name, memory_bytes, cpu_percent, process_count)
            VALUES ($minute, $key, $name, $memory, $cpu, $count);
            """;
        SqliteParameter minute = command.Parameters.Add("$minute", SqliteType.Integer);
        SqliteParameter key = command.Parameters.Add("$key", SqliteType.Text);
        SqliteParameter name = command.Parameters.Add("$name", SqliteType.Text);
        SqliteParameter memory = command.Parameters.Add("$memory", SqliteType.Integer);
        SqliteParameter cpu = command.Parameters.Add("$cpu", SqliteType.Real);
        SqliteParameter count = command.Parameters.Add("$count", SqliteType.Integer);
        command.Prepare();
        foreach (AppMemorySample sample in samples)
        {
            minute.Value = ToMinute(sample.TimestampUtc);
            key.Value = sample.Key;
            name.Value = sample.DisplayName;
            memory.Value = sample.MemoryBytes;
            cpu.Value = sample.CpuPercent;
            count.Value = sample.ProcessCount;
            command.ExecuteNonQuery();
        }

        if (pruneBefore is DateTimeOffset cutoff)
        {
            command.Parameters.Clear();
            command.CommandText = "DELETE FROM app_memory_samples WHERE minute < $cutoff;";
            command.Parameters.AddWithValue("$cutoff", ToMinute(cutoff));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyList<AppMemorySample> ReadAppMemorySamples(string? key, DateTimeOffset since)
    {
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = key is null
            ? "SELECT * FROM app_memory_samples WHERE minute >= $since ORDER BY minute;"
            : "SELECT * FROM app_memory_samples WHERE app_key = $key AND minute >= $since ORDER BY minute;";
        command.Parameters.AddWithValue("$since", ToMinute(since));
        if (key is not null)
        {
            command.Parameters.AddWithValue("$key", key);
        }

        using SqliteDataReader reader = command.ExecuteReader();
        List<AppMemorySample> samples = [];
        while (reader.Read())
        {
            samples.Add(new(reader.GetString(1), reader.GetString(2), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0) * 60),
                reader.GetInt64(3), reader.GetDouble(4), reader.GetInt32(5)));
        }

        return samples;
    }

    private static long ToMinute(DateTimeOffset timestamp) => timestamp.ToUnixTimeSeconds() / 60;
}
