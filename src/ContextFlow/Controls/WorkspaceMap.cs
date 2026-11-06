using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ContextFlow.ViewModels;

namespace ContextFlow.Controls;

/// <summary>
/// Pannable infinite-canvas overview. Capsules are laid out using their logical
/// canvas coordinates; the map supports click-to-activate, drag-to-pan, and
/// Ctrl+wheel zooming. It is a view of the workspace topology, not a mirror of
/// the physical Windows desktop.
/// </summary>
public sealed class WorkspaceMap : FrameworkElement
{
    private const double Padding = 18;
    private readonly Dictionary<string, Rect> _hitRects = new(StringComparer.Ordinal);
    private IEnumerable<CapsuleItemViewModel>? _subscribedItems;
    private INotifyCollectionChanged? _subscribedCollection;
    private Point _dragStart;
    private double _panStartX;
    private double _panStartY;
    private bool _isDragging;
    private bool _didPan;
    private double _scale = 0.1;
    private double _panX;
    private double _panY;
    private double _contentMinX;
    private double _contentMinY;
    private bool _needsFit = true;
    private CapsuleItemViewModel? _pressedCapsule;

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable<CapsuleItemViewModel>), typeof(WorkspaceMap),
        new FrameworkPropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty ActiveCapsuleProperty = DependencyProperty.Register(
        nameof(ActiveCapsule), typeof(CapsuleItemViewModel), typeof(WorkspaceMap),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ActivateCommandProperty = DependencyProperty.Register(
        nameof(ActivateCommand), typeof(ICommand), typeof(WorkspaceMap),
        new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty SurfaceBrushProperty = RegisterBrush(nameof(SurfaceBrush));
    public static readonly DependencyProperty BorderBrushProperty = RegisterBrush(nameof(BorderBrush));
    public static readonly DependencyProperty TextPrimaryBrushProperty = RegisterBrush(nameof(TextPrimaryBrush));
    public static readonly DependencyProperty TextSecondaryBrushProperty = RegisterBrush(nameof(TextSecondaryBrush));
    public static readonly DependencyProperty AccentBrushProperty = RegisterBrush(nameof(AccentBrush));
    public static readonly DependencyProperty GridBrushProperty = RegisterBrush(nameof(GridBrush));

    public WorkspaceMap()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.SizeAll;
        ToolTip = "Drag to pan · Ctrl+wheel to zoom · click a capsule to activate";
        Loaded += (_, _) =>
        {
            if (_needsFit)
            {
                FitToContent();
            }
        };
        SizeChanged += (_, _) =>
        {
            if (_needsFit)
            {
                FitToContent();
            }
        };
    }

    public IEnumerable<CapsuleItemViewModel>? ItemsSource
    {
        get => (IEnumerable<CapsuleItemViewModel>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public CapsuleItemViewModel? ActiveCapsule
    {
        get => (CapsuleItemViewModel?)GetValue(ActiveCapsuleProperty);
        set => SetValue(ActiveCapsuleProperty, value);
    }

    public ICommand? ActivateCommand
    {
        get => (ICommand?)GetValue(ActivateCommandProperty);
        set => SetValue(ActivateCommandProperty, value);
    }

    public Brush SurfaceBrush
    {
        get => (Brush)GetValue(SurfaceBrushProperty);
        set => SetValue(SurfaceBrushProperty, value);
    }

    public Brush BorderBrush
    {
        get => (Brush)GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    public Brush TextPrimaryBrush
    {
        get => (Brush)GetValue(TextPrimaryBrushProperty);
        set => SetValue(TextPrimaryBrushProperty, value);
    }

    public Brush TextSecondaryBrush
    {
        get => (Brush)GetValue(TextSecondaryBrushProperty);
        set => SetValue(TextSecondaryBrushProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    private static DependencyProperty RegisterBrush(string name) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(WorkspaceMap),
            new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var map = (WorkspaceMap)d;
        map.Unsubscribe();
        map.Subscribe(e.NewValue as IEnumerable<CapsuleItemViewModel>);
        map._needsFit = true;
        map.InvalidateVisual();
    }

    private void Subscribe(IEnumerable<CapsuleItemViewModel>? items)
    {
        _subscribedItems = items;
        if (items is null)
        {
            return;
        }

        if (items is INotifyCollectionChanged collection)
        {
            _subscribedCollection = collection;
            collection.CollectionChanged += OnCollectionChanged;
        }

        foreach (var item in items)
        {
            item.PropertyChanged += OnCapsulePropertyChanged;
        }
    }

    private void Unsubscribe()
    {
        if (_subscribedCollection is not null)
        {
            _subscribedCollection.CollectionChanged -= OnCollectionChanged;
            _subscribedCollection = null;
        }

        if (_subscribedItems is not null)
        {
            foreach (var item in _subscribedItems)
            {
                item.PropertyChanged -= OnCapsulePropertyChanged;
            }
        }

        _subscribedItems = null;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (CapsuleItemViewModel item in e.OldItems)
            {
                item.PropertyChanged -= OnCapsulePropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (CapsuleItemViewModel item in e.NewItems)
            {
                item.PropertyChanged += OnCapsulePropertyChanged;
            }
        }

        _needsFit = true;
        FitToContent();
        InvalidateVisual();
    }

    private void OnCapsulePropertyChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    public void FitToContent()
    {
        if (_subscribedItems is null || ActualWidth <= Padding * 2 || ActualHeight <= Padding * 2)
        {
            return;
        }

        var items = _subscribedItems.ToList();
        if (items.Count == 0)
        {
            _needsFit = false;
            return;
        }

        var minX = items.Min(i => i.Model.CanvasX);
        var minY = items.Min(i => i.Model.CanvasY);
        var maxX = items.Max(i => i.Model.CanvasX + i.Model.CanvasWidth);
        var maxY = items.Max(i => i.Model.CanvasY + i.Model.CanvasHeight);
        var spanX = Math.Max(1, maxX - minX);
        var spanY = Math.Max(1, maxY - minY);
        var availableWidth = Math.Max(1, ActualWidth - Padding * 2);
        var availableHeight = Math.Max(1, ActualHeight - Padding * 2);

        _contentMinX = minX;
        _contentMinY = minY;
        _scale = Math.Clamp(
            Math.Min(availableWidth / spanX, availableHeight / spanY),
            0.012,
            0.26);
        _panX = 0;
        _panY = 0;
        _needsFit = false;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var bounds = new Rect(0, 0, Math.Max(0, ActualWidth), Math.Max(0, ActualHeight));
        drawingContext.DrawRectangle(Brushes.Transparent, null, bounds);
        DrawGrid(drawingContext);
        _hitRects.Clear();

        if (_subscribedItems is null)
        {
            return;
        }

        if (_needsFit && ActualWidth > 0)
        {
            FitToContent();
        }

        var items = _subscribedItems.ToList();
        var viewport = new Rect(Padding, Padding, Math.Max(1, ActualWidth - Padding * 2), Math.Max(1, ActualHeight - Padding * 2));
        foreach (var capsule in items)
        {
            var x = Padding + (capsule.Model.CanvasX - _contentMinX) * _scale + _panX;
            var y = Padding + (capsule.Model.CanvasY - _contentMinY) * _scale + _panY;
            var width = Math.Max(62, capsule.Model.CanvasWidth * _scale);
            var height = Math.Max(38, capsule.Model.CanvasHeight * _scale);
            var rect = new Rect(x, y, width, height);
            _hitRects[capsule.Id] = rect;

            var isActive = ReferenceEquals(capsule, ActiveCapsule);
            var fill = isActive ? capsule.AccentSoftBrush : SurfaceBrush;
            var border = isActive ? capsule.AccentBrush : BorderBrush;
            var borderPen = new Pen(border, isActive ? 1.8 : 1);
            borderPen.Freeze();
            drawingContext.DrawRoundedRectangle(fill, borderPen, rect, 10, 10);

            var accentBar = new Rect(rect.X, rect.Y, isActive ? 4 : 3, rect.Height);
            drawingContext.DrawRoundedRectangle(capsule.AccentBrush, null, accentBar, 1.5, 1.5);

            if (rect.Width > 72 && rect.Height > 30)
            {
                var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var label = new FormattedText(
                    capsule.Name,
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    10.5,
                    isActive ? capsule.AccentBrush : TextPrimaryBrush,
                    pixelsPerDip)
                {
                    MaxTextWidth = Math.Max(12, rect.Width - 18),
                    MaxTextHeight = Math.Max(12, rect.Height - 10),
                    Trimming = TextTrimming.CharacterEllipsis
                };
                drawingContext.DrawText(label, new Point(rect.X + 10, rect.Y + 8));

                if (rect.Height >= 48)
                {
                    var sub = new FormattedText(
                        $"{capsule.WindowCountText} · {ShortenDisplay(capsule.TargetDisplayLabel)}",
                        CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight,
                        new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                        8.4,
                        TextSecondaryBrush,
                        pixelsPerDip)
                    {
                        MaxTextWidth = Math.Max(12, rect.Width - 18),
                        Trimming = TextTrimming.CharacterEllipsis
                    };
                    drawingContext.DrawText(sub, new Point(rect.X + 10, rect.Bottom - 19));
                }
            }

            if (isActive)
            {
                var activeDot = new Point(rect.Right - 10, rect.Top + 10);
                drawingContext.DrawEllipse(capsule.AccentBrush, null, activeDot, 3.2, 3.2);
            }
        }

        // Tiny canvas badge in the lower-left corner.
        var badgeText = new FormattedText(
            "∞ WORKSPACE",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            8,
            TextSecondaryBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawingContext.DrawText(badgeText, new Point(Padding, ActualHeight - 16));
    }

    private void DrawGrid(DrawingContext drawingContext)
    {
        if (ActualWidth < 2 || ActualHeight < 2)
        {
            return;
        }

        var brush = GridBrush;
        var spacing = Math.Clamp(28 * Math.Max(_scale, 0.04) / 0.12, 18, 52);
        var offsetX = (_panX % spacing + spacing) % spacing;
        var offsetY = (_panY % spacing + spacing) % spacing;
        const double dotRadius = 0.8;
        for (var x = offsetX; x < ActualWidth; x += spacing)
        {
            for (var y = offsetY; y < ActualHeight; y += spacing)
            {
                drawingContext.DrawEllipse(brush, null, new Point(x, y), dotRadius, dotRadius);
            }
        }
    }

    private static string ShortenDisplay(string label)
    {
        var dot = label.IndexOf('·');
        return dot > 0 ? label[..dot].Trim() : label;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            FitToContent();
            e.Handled = true;
            return;
        }

        Focus();
        _dragStart = e.GetPosition(this);
        _panStartX = _panX;
        _panStartY = _panY;
        _isDragging = true;
        _didPan = false;
        _pressedCapsule = HitTestCapsule(_dragStart);
        CaptureMouse();
        Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);
        if (_isDragging && e.LeftButton == MouseButtonState.Pressed)
        {
            var delta = point - _dragStart;
            if (!_didPan && (Math.Abs(delta.X) > 3 || Math.Abs(delta.Y) > 3))
            {
                _didPan = true;
            }

            if (_didPan)
            {
                _panX = _panStartX + delta.X;
                _panY = _panStartY + delta.Y;
                InvalidateVisual();
            }
        }
        else
        {
            Cursor = HitTestCapsule(point) is not null ? Cursors.Hand : Cursors.SizeAll;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_isDragging)
        {
            return;
        }

        _isDragging = false;
        ReleaseMouseCapture();
        var pressed = _pressedCapsule;
        _pressedCapsule = null;
        Cursor = _didPan ? Cursors.SizeAll : (pressed is not null ? Cursors.Hand : Cursors.SizeAll);
        if (!_didPan && pressed is not null)
        {
            SetCurrentValue(ActiveCapsuleProperty, pressed);
            if (ActivateCommand?.CanExecute(pressed) == true)
            {
                ActivateCommand.Execute(pressed);
            }
        }

        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            var pointer = e.GetPosition(this);
            var worldX = (pointer.X - Padding - _panX) / _scale + _contentMinX;
            var worldY = (pointer.Y - Padding - _panY) / _scale + _contentMinY;
            var factor = e.Delta > 0 ? 1.14 : 1 / 1.14;
            _scale = Math.Clamp(_scale * factor, 0.012, 1.2);
            _panX = pointer.X - Padding - (worldX - _contentMinX) * _scale;
            _panY = pointer.Y - Padding - (worldY - _contentMinY) * _scale;
        }
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            _panX += e.Delta * 0.55;
        }
        else
        {
            _panY += e.Delta * 0.55;
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_isDragging)
        {
            Cursor = Cursors.SizeAll;
        }
    }

    private CapsuleItemViewModel? HitTestCapsule(Point point)
    {
        if (_subscribedItems is null)
        {
            return null;
        }

        foreach (var capsule in _subscribedItems.Reverse())
        {
            if (_hitRects.TryGetValue(capsule.Id, out var rect) && rect.Contains(point))
            {
                return capsule;
            }
        }

        return null;
    }
}
