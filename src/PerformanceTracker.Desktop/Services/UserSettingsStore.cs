using System.IO;
using System.Text.Json;

namespace PerformanceTracker.Desktop.Services;

public sealed record AlertSettings(bool Memory = true, bool Growth = true, int Threshold = 90);

public sealed class UserSettingsStore
{
    private readonly string _path = Environment.GetEnvironmentVariable("PERFORMANCE_TRACKER_ALERT_SETTINGS") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PerformanceTracker", "alerts.json");

    public UserSettingsStore()
    {
        try
        {
            if (File.Exists(_path))
            {
                Current = JsonSerializer.Deserialize<AlertSettings>(File.ReadAllText(_path)) ?? new();
                Current = Current with { Threshold = Math.Clamp(Current.Threshold, 1, 99) };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Current = new();
        }
    }

    public AlertSettings Current { get; private set; } = new();

    public void Save(AlertSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings));
            File.Move(temporary, _path, overwrite: true);
            Current = settings;
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
