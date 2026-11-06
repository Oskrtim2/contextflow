namespace ContextFlow.Models;

/// <summary>
/// A physical-pixel rectangle in the Windows virtual desktop coordinate space.
/// Kept platform-neutral so it can cross service/UI boundaries without tying
/// the domain model to WPF or Win32.
/// </summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public int CenterX => X + Width / 2;
    public int CenterY => Y + Height / 2;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) =>
        x >= X && x < Right && y >= Y && y < Bottom;

    public bool IntersectsWith(ScreenRect other) =>
        X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;

    public double DistanceTo(double x, double y)
    {
        var dx = CenterX - x;
        var dy = CenterY - y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public override string ToString() => $"{X}, {Y}";
}
