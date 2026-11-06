namespace ContextFlow.Models;

/// <summary>
/// Persistent definition of a workflow capsule. The canvas coordinates form
/// a logical and effectively infinite 2D workspace; they are not Windows
/// desktop coordinates. TargetDisplay maps the capsule to a physical monitor.
/// </summary>
public sealed class CapsuleDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Capsule";
    public string Accent { get; set; } = "#6366F1";

    public double CanvasX { get; set; }
    public double CanvasY { get; set; }
    public double CanvasWidth { get; set; } = 1920;
    public double CanvasHeight { get; set; } = 1080;

    /// <summary>Stable monitor device name, for example \\.\DISPLAY1.</summary>
    public string TargetDisplay { get; set; } = string.Empty;

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public CapsuleDefinition Clone() => new()
    {
        Id = Id,
        Name = Name,
        Accent = Accent,
        CanvasX = CanvasX,
        CanvasY = CanvasY,
        CanvasWidth = CanvasWidth,
        CanvasHeight = CanvasHeight,
        TargetDisplay = TargetDisplay,
        CreatedUtc = CreatedUtc
    };
}
