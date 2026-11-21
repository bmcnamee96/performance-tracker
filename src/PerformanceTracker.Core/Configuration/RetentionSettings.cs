namespace PerformanceTracker.Core.Configuration;

public sealed class RetentionSettings
{
    public int AppMemoryRetentionDays { get; set; } = 7;

    public int RawSampleRetentionHours { get; set; } = 72;

    public int EventRetentionDays { get; set; } = 90;

    public int DailySummaryRetentionDays { get; set; } = 365;
}
