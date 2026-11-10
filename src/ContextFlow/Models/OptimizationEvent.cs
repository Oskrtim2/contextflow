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
    ProtectedAudio,
    LearningAdjusted,
    ProcessEnded,
    Error
}

/// <summary>Activity record surfaced in the HUD without exposing implementation details.</summary>
public sealed record OptimizationEvent(
    DateTimeOffset Timestamp,
    string ProcessName,
    OptimizationEventKind Kind,
    string Detail,
    bool Succeeded,
    string? DetailKey = null,
    object?[]? DetailArgs = null)
{
    /// <summary>Detail localized on the UI thread when a resource key is available.</summary>
    public string LocalizedDetail
    {
        get
        {
            if (DetailKey is null)
            {
                return Detail;
            }

            try
            {
                var format = Services.LocalizationService.Get(DetailKey);
                return format == DetailKey ? Detail : string.Format(format, DetailArgs ?? Array.Empty<object?>());
            }
            catch (FormatException)
            {
                return Detail;
            }
        }
    }

    public string TimeLabel => Timestamp.ToLocalTime().ToString("HH:mm:ss");
}
