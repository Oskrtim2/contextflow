using System.Windows;
using System.Windows.Media;

namespace ContextFlow.Controls;

/// <summary>Lightweight sparkline for the last 60 telemetry samples.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IEnumerable<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeBrushProperty = DependencyProperty.Register(
        nameof(StrokeBrush), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(Brushes.Indigo, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable<double>? Points
    {
        get => (IEnumerable<double>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public Brush StrokeBrush
    {
        get => (Brush)GetValue(StrokeBrushProperty);
        set => SetValue(StrokeBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width;
        var height = double.IsInfinity(availableSize.Height) ? 30 : availableSize.Height;
        return new Size(Math.Max(20, width), Math.Max(12, height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var values = Points?.ToArray();
        if (values is null || values.Length < 2 || ActualWidth <= 1 || ActualHeight <= 1)
        {
            return;
        }

        var width = ActualWidth;
        var height = ActualHeight;
        var stepX = width / (values.Length - 1);
        var line = new StreamGeometry();
        var fill = new StreamGeometry();

        using (var context = line.Open())
        {
            var firstY = MapY(values[0], height);
            context.BeginFigure(new Point(0, firstY), false, false);
            for (var i = 1; i < values.Length; i++)
            {
                context.LineTo(new Point(i * stepX, MapY(values[i], height)), true, false);
            }
        }

        using (var context = fill.Open())
        {
            var firstY = MapY(values[0], height);
            context.BeginFigure(new Point(0, height), true, true);
            context.LineTo(new Point(0, firstY), true, false);
            for (var i = 1; i < values.Length; i++)
            {
                context.LineTo(new Point(i * stepX, MapY(values[i], height)), true, false);
            }

            context.LineTo(new Point(width, height), true, false);
        }

        line.Freeze();
        fill.Freeze();

        drawingContext.PushOpacity(0.12);
        drawingContext.DrawGeometry(StrokeBrush, null, fill);
        drawingContext.Pop();

        var pen = new Pen(StrokeBrush, 1.4)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        drawingContext.DrawGeometry(null, pen, line);
    }

    private static double MapY(double value, double height)
    {
        var normalized = 1 - Math.Clamp(value, 0, 100) / 100d;
        return 1 + normalized * Math.Max(1, height - 2);
    }
}
