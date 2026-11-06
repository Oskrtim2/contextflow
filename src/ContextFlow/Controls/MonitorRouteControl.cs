using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ContextFlow.ViewModels;

namespace ContextFlow.Controls;

/// <summary>
/// Segmented, draggable display switch. Dragging or clicking a segment updates
/// SelectedIndex (two-way bound to the active capsule's target display), and the
/// view-model routes the capsule's entire window set to that physical monitor.
/// </summary>
public sealed class MonitorRouteControl : FrameworkElement
{
    private bool _dragging;
    private int? _previewIndex;
    private IEnumerable<DisplayItemViewModel>? _items;

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable<DisplayItemViewModel>), typeof(MonitorRouteControl),
        new FrameworkPropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(MonitorRouteControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentBrushProperty = RegisterBrush(nameof(AccentBrush));
    public static readonly DependencyProperty TrackBrushProperty = RegisterBrush(nameof(TrackBrush));
    public static readonly DependencyProperty TextBrushProperty = RegisterBrush(nameof(TextBrush));
    public static readonly DependencyProperty MutedTextBrushProperty = RegisterBrush(nameof(MutedTextBrush));
    public static readonly DependencyProperty BorderBrushProperty = RegisterBrush(nameof(BorderBrush));
    public static readonly DependencyProperty SurfaceBrushProperty = RegisterBrush(nameof(SurfaceBrush));

    public MonitorRouteControl()
    {
        Cursor = Cursors.Hand;
        Focusable = true;
        ToolTip = "Drag or click to route the active capsule's windows to another display";
    }

    public IEnumerable<DisplayItemViewModel>? ItemsSource
    {
        get => (IEnumerable<DisplayItemViewModel>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
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

    public Brush TextBrush
    {
        get => (Brush)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public Brush MutedTextBrush
    {
        get => (Brush)GetValue(MutedTextBrushProperty);
        set => SetValue(MutedTextBrushProperty, value);
    }

    public Brush BorderBrush
    {
        get => (Brush)GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public Brush SurfaceBrush
    {
        get => (Brush)GetValue(SurfaceBrushProperty);
        set => SetValue(SurfaceBrushProperty, value);
    }

    private static DependencyProperty RegisterBrush(string name) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(MonitorRouteControl),
            new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (MonitorRouteControl)d;
        if (control._items is INotifyCollectionChanged oldCollection)
        {
            oldCollection.CollectionChanged -= control.OnCollectionChanged;
        }

        control._items = e.NewValue as IEnumerable<DisplayItemViewModel>;
        if (control._items is INotifyCollectionChanged newCollection)
        {
            newCollection.CollectionChanged += control.OnCollectionChanged;
        }

        control.InvalidateVisual();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 220 : availableSize.Width;
        return new Size(Math.Max(80, width), 56);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 1 || ActualHeight <= 1)
        {
            return;
        }

        var displays = _items?.ToList() ?? new List<DisplayItemViewModel>();
        var track = new Rect(0, 2, ActualWidth, Math.Max(24, ActualHeight - 4));
        var radius = track.Height / 2;
        var borderPen = new Pen(BorderBrush, 1);

        drawingContext.DrawRoundedRectangle(TrackBrush, borderPen, track, radius, radius);
        if (displays.Count == 0)
        {
            var empty = CreateText("NO DISPLAYS", 9, MutedTextBrush, FontWeights.SemiBold);
            drawingContext.DrawText(empty, new Point((ActualWidth - empty.Width) / 2, (ActualHeight - empty.Height) / 2));
            return;
        }

        var segmentWidth = ActualWidth / displays.Count;
        var selected = Math.Clamp(_previewIndex ?? SelectedIndex, 0, displays.Count - 1);
        var thumbRect = new Rect(selected * segmentWidth + 3, 5, Math.Max(1, segmentWidth - 6), Math.Max(1, track.Height - 6));
        var accentPen = new Pen(AccentBrush, 1);
        drawingContext.DrawRoundedRectangle(AccentBrush, null, thumbRect, Math.Max(4, thumbRect.Height / 2), Math.Max(4, thumbRect.Height / 2));

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (var i = 0; i < displays.Count; i++)
        {
            var display = displays[i];
            var isSelected = i == selected;
            var left = i * segmentWidth;
            var centerX = left + segmentWidth / 2;
            var labelBrush = isSelected ? Brushes.White : TextBrush;
            var subBrush = isSelected ? Brushes.White : MutedTextBrush;

            var label = new FormattedText(
                display.Label.ToUpperInvariant(),
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                8.7,
                labelBrush,
                pixelsPerDip);
            var sub = new FormattedText(
                display.ResolutionLabel,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                8.2,
                subBrush,
                pixelsPerDip);

            drawingContext.DrawText(label, new Point(centerX - label.Width / 2, 23));
            drawingContext.DrawText(sub, new Point(centerX - sub.Width / 2, 36));
        }

        if (displays.Count > 1)
        {
            var separatorPen = new Pen(BorderBrush, 0.7);
            for (var i = 1; i < displays.Count; i++)
            {
                var x = i * segmentWidth;
                drawingContext.DrawLine(separatorPen, new Point(x, 12), new Point(x, ActualHeight - 12));
            }
        }
    }

    private FormattedText CreateText(string text, double size, Brush brush, FontWeight weight) =>
        new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
            size,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private int GetIndexFromPoint(Point point)
    {
        var count = _items?.Count() ?? 0;
        if (count == 0)
        {
            return 0;
        }

        return Math.Clamp((int)(point.X / Math.Max(1, ActualWidth) * count), 0, count - 1);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        _dragging = true;
        CaptureMouse();
        _previewIndex = GetIndexFromPoint(e.GetPosition(this));
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging && e.LeftButton == MouseButtonState.Pressed)
        {
            var index = GetIndexFromPoint(e.GetPosition(this));
            if (_previewIndex != index)
            {
                _previewIndex = index;
                InvalidateVisual();
            }
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleaseMouseCapture();
        var finalIndex = GetIndexFromPoint(e.GetPosition(this));
        _previewIndex = null;
        // The view-model setter performs the actual window routing once the
        // toggle is released, not for each intermediate drag position.
        if (finalIndex != SelectedIndex)
        {
            SetCurrentValue(SelectedIndexProperty, finalIndex);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_dragging)
        {
            Cursor = Cursors.Hand;
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging)
        {
            _dragging = false;
            _previewIndex = null;
            InvalidateVisual();
        }

        Cursor = Cursors.Hand;
    }
}
