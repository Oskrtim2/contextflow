using System.Windows.Media;

namespace ContextFlow.Models;

/// <summary>
/// Immutable snapshot of a managed top-level window. Bounds are physical pixels
/// from DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS), falling back to
/// GetWindowRect when DWM is unavailable.
/// </summary>
public sealed record ManagedWindow(
    IntPtr Handle,
    int ProcessId,
    string ProcessName,
    string ExecutablePath,
    string Title,
    ScreenRect Bounds,
    bool IsMinimized,
    bool IsForeground,
    bool IsCloaked,
    string CapsuleId,
    string MonitorDevice,
    ImageSource? Icon,
    DateTime FirstSeenUtc,
    DateTime LastForegroundUtc)
{
    public string CoordinatesLabel => $"{Bounds.X}, {Bounds.Y}";
}
