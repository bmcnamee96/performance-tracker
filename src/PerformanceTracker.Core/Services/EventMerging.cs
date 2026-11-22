using PerformanceTracker.Core.Models;

namespace PerformanceTracker.Core.Services;

/// <summary>
/// The detector reports a fresh event every sample while a slowdown lasts, and under heavy load the reported
/// kind flips back and forth (memory, download, memory...). This folds overlapping reports into one entry per
/// slowdown so the history reads "Memory full · 12 min" instead of dozens of near-identical rows.
/// </summary>
public static class EventMerging
{
    /// <summary>Largest gap between two reports that still counts as the same slowdown.</summary>
    public static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(10);

    /// <summary>Returns the combined event when <paramref name="next"/> continues <paramref name="previous"/>, otherwise null.</summary>
    public static PerformanceEvent? TryMerge(PerformanceEvent previous, PerformanceEvent next)
    {
        bool continues = next.StartedAtUtc <= previous.EndedAtUtc + MaxGap && next.EndedAtUtc >= previous.EndedAtUtc;
        if (!continues)
        {
            return null;
        }

        // The title only changes when things get worse, so the entry doesn't flicker between kinds.
        bool worse = next.Severity > previous.Severity;
        return next with
        {
            StartedAtUtc = previous.StartedAtUtc < next.StartedAtUtc ? previous.StartedAtUtc : next.StartedAtUtc,
            Severity = worse ? next.Severity : previous.Severity,
            Summary = worse ? next.Summary : previous.Summary,
            // Keep a named culprit once one has been found; the generic text is less useful.
            LikelyCause = NamesAnApp(next.LikelyCause) || !NamesAnApp(previous.LikelyCause) ? next.LikelyCause : previous.LikelyCause,
            ForegroundApp = next.ForegroundApp ?? previous.ForegroundApp
        };
    }

    /// <summary>How long the slowdown lasted, in plain words ("under a minute", "3 min", "1 h 5 min").</summary>
    public static string DescribeDuration(PerformanceEvent performanceEvent)
    {
        TimeSpan length = performanceEvent.EndedAtUtc - performanceEvent.StartedAtUtc;
        return length switch
        {
            { TotalMinutes: < 1 } => "under a minute",
            { TotalHours: < 1 } => $"{(int)Math.Round(length.TotalMinutes)} min",
            _ => length.Minutes == 0 ? $"{(int)length.TotalHours} h" : $"{(int)length.TotalHours} h {length.Minutes} min"
        };
    }

    private static bool NamesAnApp(string cause) =>
        cause.Contains("jumped to", StringComparison.Ordinal) ||
        cause.Contains("grew by", StringComparison.Ordinal) ||
        cause.Contains("Biggest app:", StringComparison.Ordinal);
}
