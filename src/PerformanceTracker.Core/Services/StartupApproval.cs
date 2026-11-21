using System.Buffers.Binary;

namespace PerformanceTracker.Core.Services;

public sealed record StartupApprovalRecord(string Bucket, string Name, object? Value);

public static class StartupApproval
{
    public static bool? Read(byte[]? data) => data switch
    {
        null => true,
        [2 or 6, ..] when data.Length == 12 => true,
        [3 or 7, ..] when data.Length == 12 => false,
        _ => null
    };

    public static bool? Match(ILookup<string, StartupApprovalRecord> approvals, string bucket, string name)
    {
        object? value = approvals[bucket].FirstOrDefault(entry =>
            string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;
        return value is null or byte[]? Read(value as byte[]) : null;
    }

    public static byte[] Write(bool enabled, DateTimeOffset now, byte[]? previous = null)
    {
        byte[] data = new byte[12];
        data[0] = (byte)((previous is [6 or 7, ..] ? 6 : 2) + (enabled ? 0 : 1));
        if (!enabled)
        {
            BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(4), now.ToFileTime());
        }

        return data;
    }

    public static bool RecommendedToKeep(string processName, string entryName) =>
        ProcessCatalog.IsProtected(processName) ||
        KeepTerms.Any(term => processName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            entryName.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] KeepTerms =
    [
        "securityhealth", "defender", "antivirus", "realtek", "rtk", "audio", "driver",
        "nvidia", "radeon", "igfx", "synaptics", "touchpad", "lghub", "logitech", "razer",
        "armoury", "onedrive", "dropbox", "googledrive", "icloud"
    ];
}
