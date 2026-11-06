using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ContextFlow.Controls;

/// <summary>Animated 270° arc gauge used for CPU, memory, and GPU telemetry.</summary>
public sealed class GaugeArc : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(GaugeArc),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(GaugeArc),
        new FrameworkPropertyMetadata(Brushes.Indigo, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(GaugeArc),
        new FrameworkPropertyMetadata(Brushes.LightGray, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty RenderedValueProperty = DependencyProperty.Register(
        nameof(RenderedValue), typeof(double), typeof(GaugeArc),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double StartAngle = 135;
    private const double TotalSweep = 270;

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    private double RenderedValue
    {
        get => (double)GetValue(RenderedValueProperty);
        set => SetValue(RenderedValueProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (GaugeArc)d;
        var target = Math.Clamp((double)e.NewValue, 0, 100);
        var animation = new DoubleAnimation
        {
            From = gauge.RenderedValue,
            To = target,
            Duration = TimeSpan.FromMilliseconds(520),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        gauge.BeginAnimation(RenderedValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = Math.Min(
            double.IsInfinity(availableSize.Width) ? 96 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 96 : availableSize.Height);
        return new Size(Math.Max(32, side), Math.Max(32, side));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var side = Math.Min(ActualWidth, ActualHeight);
        var thickness = Math.Max(5, side * 0.085);
        var radius = Math.Max(1, (side - thickness) / 2 - 2);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        var trackPen = new Pen(TrackBrush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        var valuePen = new Pen(AccentBrush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        trackPen.Freeze();
        valuePen.Freeze();

        drawingContext.DrawGeometry(null, trackPen, CreateArc(center, radius, StartAngle, TotalSweep));
        var sweep = TotalSweep * Math.Clamp(RenderedValue, 0, 100) / 100.0;
        if (sweep > 0.3)
        {
            drawingContext.DrawGeometry(null, valuePen, CreateArc(center, radius, StartAngle, sweep));
        }
    }

    private static Geometry CreateArc(Point center, double radius, double startAngleDegrees, double sweepDegrees)
    {
        var start = PointOnCircle(center, radius, startAngleDegrees);
        var end = PointOnCircle(center, radius, startAngleDegrees + sweepDegrees);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, false, false);
            context.ArcTo(
                end,
                new Size(radius, radius),
                0,
                sweepDegrees > 180,
                SweepDirection.Clockwise,
                true,
                false);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180d;
        return new Point(
            center.X + radius * Math.Cos(radians),
            center.Y + radius * Math.Sin(radians));
    }
}
