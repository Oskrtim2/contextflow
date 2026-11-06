namespace ContextFlow.Models;

public enum OptimizationEventKind
{
    PolicyStarted,
    PolicyStopped,
    PriorityThrottled,
    PriorityRestored,
    WorkingSetTrimmed,
    CompositionCloaked,
    CompositionRestored,
    CapsuleRouted,
    WindowReassigned,
    Skipped,
    Error
}

/// <summary>Activity record surfaced in the HUD without exposing implementation details.</summary>
public sealed record OptimizationEvent(
    DateTimeOffset Timestamp,
    string ProcessName,
    OptimizationEventKind Kind,
    string Detail,
    bool Succeeded)
{
    public string TimeLabel => Timestamp.ToLocalTime().ToString("HH:mm:ss");
}
