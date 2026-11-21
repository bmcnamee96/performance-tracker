namespace PerformanceTracker.Core.Services;

public sealed class AlertRules
{
    private static readonly TimeSpan Sustain = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(30);
    private readonly Dictionary<string, DateTimeOffset> _growthAlerts = new(StringComparer.Ordinal);
    private DateTimeOffset? _highSince;
    private DateTimeOffset? _lastMemoryAlert;
    private double? _alertedAtPercent;
    private double _threshold;

    public bool Memory(DateTimeOffset now, double percent, double threshold, bool quiet)
    {
        if (_threshold != threshold || percent <= threshold)
        {
            _highSince = null;
            _alertedAtPercent = null;
            _threshold = threshold;
        }

        if (percent <= threshold)
        {
            return false;
        }

        _highSince ??= now;
        if (quiet || now - _highSince < Sustain || now - _lastMemoryAlert < Cooldown ||
            percent < _alertedAtPercent + 3)
        {
            return false;
        }

        _lastMemoryAlert = now;
        _alertedAtPercent = percent;
        return true;
    }

    public bool Growth(DateTimeOffset now, string key, bool quiet)
    {
        if (quiet || (_growthAlerts.TryGetValue(key, out DateTimeOffset previous) && now - previous < Cooldown))
        {
            return false;
        }

        _growthAlerts[key] = now;
        return true;
    }
}
