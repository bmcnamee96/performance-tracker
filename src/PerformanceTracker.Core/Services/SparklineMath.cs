namespace PerformanceTracker.Core.Services;

/// <summary>Geometry for the dashboard's small trend charts.</summary>
public static class SparklineMath
{
    /// <summary>
    /// Maps values onto a width x height box: x spread evenly, y = 0 at the top for <paramref name="max"/>.
    /// Missing values are skipped.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)> Points(IReadOnlyList<double?> values, double width, double height, double max)
    {
        if (values.Count == 0 || width <= 0 || height <= 0 || max <= 0)
        {
            return [];
        }

        double step = values.Count == 1 ? 0 : width / (values.Count - 1);
        List<(double X, double Y)> points = [];
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] is double v)
            {
                double clamped = Math.Clamp(v / max, 0, 1);
                points.Add((i * step, height - clamped * height));
            }
        }

        return points;
    }
}
