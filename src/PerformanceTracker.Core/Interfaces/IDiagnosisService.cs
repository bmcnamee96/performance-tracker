using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Core.Interfaces;

public interface IDiagnosisService
{
    string DescribeLikelyCause(MetricSample sample);
}

