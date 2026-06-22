using PerformanceTracker.Core.Interfaces;
using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Core.Services;

public sealed class SimpleDiagnosisService : IDiagnosisService
{
    public string DescribeLikelyCause(MetricSample sample)
    {
        if (sample.DiskActiveTimePercent is >= 90)
        {
            return "Likely disk-related stutter: disk activity stayed elevated during the captured interval.";
        }

        if (sample.MemoryUsedPercent >= 90)
        {
            return "Likely RAM pressure: available memory was low during the captured interval.";
        }

        if (sample.CpuUsagePercent is >= 90)
        {
            return "Likely CPU bottleneck: CPU usage remained high during the captured interval.";
        }

        return "Performance changed, but the current scaffold could not classify the cause confidently.";
    }
}

