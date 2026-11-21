namespace PerformanceTracker.Core.Models;

public sealed record AppMemorySample(string Key, string DisplayName, DateTimeOffset TimestampUtc, long MemoryBytes, double CpuPercent, int ProcessCount);
