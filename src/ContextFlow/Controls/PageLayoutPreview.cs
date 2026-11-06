using System.Windows;
using System.Windows.Media;
using ContextFlow.Models;

namespace ContextFlow.Controls;

/// <summary>
/// Small visual preview of the split layout used by a page. It mirrors the
/// slot geometry used by WindowManager so the map communicates the actual
/// tiling before the user jumps to the page.
/// </summary>
public sealed class PageLayoutPreview : FrameworkElement
{
    public static readonly DependencyProperty WindowCountProperty = DependencyProperty.Register(
        nameof(WindowCount),
        typeof(int),
        typeof(PageLayoutPreview),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LayoutProperty = DependencyProperty.Register(
        nameof(Layout),
        typeof(PageLayout),
        typeof(PageLayoutPreview),
        new FrameworkPropertyMetadata(PageLayout.Auto, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush),
        typeof(Brush),
        typeof(PageLayoutPreview),
        new FrameworkPropertyMetadata(Brushes.Transparent, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SlotBrushProperty = DependencyProperty.Register(
        nameof(SlotBrush),
        typeof(Brush),
        typeof(PageLayoutPreview),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public PageLayoutPreview()
    {
        IsHitTestVisible = false;
        MinWidth = 44;
        MinHeight = 28;
    }

    public int WindowCount
    {
        get => (int)GetValue(WindowCountProperty);
        set => SetValue(WindowCountProperty, value);
    }

    public PageLayout Layout
    {
        get => (PageLayout)GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public Brush SlotBrush
    {
        get => (Brush)GetValue(SlotBrushProperty);
        set => SetValue(SlotBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth < 2 || ActualHeight < 2)
        {
            return;
        }

        var count = WindowCount;
        var gap = 2.0;
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);

        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(18, 99, 102, 241)),
            null,
            bounds,
            5,
            5);

        if (count <= 0)
        {
            var emptyPen = new Pen(AccentBrush, 1)
            {
                DashStyle = DashStyles.Dash
            };
            emptyPen.Freeze();
            drawingContext.DrawRoundedRectangle(null, emptyPen, new Rect(3, 3, Math.Max(1, ActualWidth - 6), Math.Max(1, ActualHeight - 6)), 4, 4);
            return;
        }

        var slots = BuildSlots(count, Layout);

        var borderPen = new Pen(AccentBrush, 1);
        borderPen.Freeze();

        foreach (var slot in slots)
        {
            var x = slot.X * (ActualWidth - gap) + gap / 2;
            var y = slot.Y * (ActualHeight - gap) + gap / 2;
            var width = Math.Max(1, slot.Width * (ActualWidth - gap) - gap);
            var height = Math.Max(1, slot.Height * (ActualHeight - gap) - gap);
            var rect = new Rect(x, y, width, height);
            drawingContext.DrawRoundedRectangle(SlotBrush, borderPen, rect, 3, 3);
        }
    }

    private static IReadOnlyList<Rect> BuildSlots(int count, PageLayout layout)
    {
        var slots = new List<Rect>(count);
        if (count <= 1)
        {
            slots.Add(new Rect(0, 0, 1, 1));
            return slots;
        }

        if (layout == PageLayout.Auto && count == 3)
        {
            slots.Add(new Rect(0, 0, 0.5, 1));
            slots.Add(new Rect(0.5, 0, 0.5, 0.5));
            slots.Add(new Rect(0.5, 0.5, 0.5, 0.5));
            return slots;
        }

        int columns;
        int rows;
        if (layout == PageLayout.Columns)
        {
            columns = count;
            rows = 1;
        }
        else if (layout == PageLayout.Rows)
        {
            columns = 1;
            rows = count;
        }
        else
        {
            columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(count)));
            rows = Math.Max(1, (int)Math.Ceiling(count / (double)columns));
        }

        var cellWidth = 1.0 / columns;
        var cellHeight = 1.0 / rows;
        for (var i = 0; i < count; i++)
        {
            var row = i / columns;
            var column = i % columns;
            slots.Add(new Rect(column * cellWidth, row * cellHeight, cellWidth, cellHeight));
        }

        return slots;
    }
}
