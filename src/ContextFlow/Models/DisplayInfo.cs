namespace ContextFlow.Models;

/// <summary>
/// Immutable snapshot of a physical monitor. Coordinates are in physical pixels.
/// </summary>
public sealed record DisplayInfo(
    string DeviceName,
    string Label,
    bool IsPrimary,
    ScreenRect Bounds,
    ScreenRect WorkArea,
    int Index,
    uint Dpi)
{
    public string ResolutionLabel => $"{Bounds.Width} × {Bounds.Height}";

    public string DetailLabel => IsPrimary ? $"{ResolutionLabel} · Primary" : ResolutionLabel;

    public double Scale => Dpi / 96.0;
}
