using System.Windows;
using System.Windows.Media;
using PerformanceTracker.Core.Services;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace PerformanceTracker.Desktop;

/// <summary>A small filled line chart that redraws to whatever size it is given.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double?>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline),
        new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(System.Windows.Media.Brushes.Cyan, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double?>? Values
    {
        get => (IReadOnlyList<double?>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        const double inset = 1.5;
        double height = ActualHeight - inset * 2;
        var points = SparklineMath.Points(Values ?? [], ActualWidth, height, Maximum)
            .Select(point => new Point(point.X, point.Y + inset))
            .ToArray();
        if (points.Length < 2)
        {
            return;
        }

        var line = new StreamGeometry();
        using (StreamGeometryContext context = line.Open())
        {
            context.BeginFigure(points[0], isFilled: false, isClosed: false);
            context.PolyLineTo(points[1..], isStroked: true, isSmoothJoin: true);
        }

        var area = new StreamGeometry();
        using (StreamGeometryContext context = area.Open())
        {
            context.BeginFigure(new Point(points[0].X, ActualHeight), isFilled: true, isClosed: true);
            context.PolyLineTo([.. points, new Point(points[^1].X, ActualHeight)], isStroked: false, isSmoothJoin: true);
        }

        Brush fill = Stroke.CloneCurrentValue();
        fill.Opacity = 0.16;
        drawingContext.DrawGeometry(fill, null, area);
        drawingContext.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round }, line);
    }
}
