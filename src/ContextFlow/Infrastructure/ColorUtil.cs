using System.Windows.Media;

namespace ContextFlow.Infrastructure;

public static class ColorUtil
{
    public static Color ParseColor(string value, Color fallback)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }
        catch
        {
            return fallback;
        }
    }

    public static SolidColorBrush BrushFromHex(string value, Color? fallback = null)
    {
        var color = ParseColor(value, fallback ?? Color.FromRgb(0x63, 0x66, 0xF1));
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush WithAlpha(string value, byte alpha)
    {
        var color = ParseColor(value, Color.FromRgb(0x63, 0x66, 0xF1));
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
