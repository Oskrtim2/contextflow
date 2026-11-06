using ContextFlow.Infrastructure;
using ContextFlow.Models;

namespace ContextFlow.ViewModels;

public sealed class ActivityItemViewModel
{
    public ActivityItemViewModel(OptimizationEvent optimizationEvent)
    {
        Event = optimizationEvent;
        ActionLabel = optimizationEvent.Kind switch
        {
            OptimizationEventKind.PriorityThrottled => "Priority throttled",
            OptimizationEventKind.PriorityRestored => "Priority restored",
            OptimizationEventKind.WorkingSetTrimmed => "Working set trimmed",
            OptimizationEventKind.CompositionCloaked => "Isolation enabled",
            OptimizationEventKind.CompositionRestored => "Isolation disabled",
            OptimizationEventKind.CapsuleRouted => "Capsule routed",
            OptimizationEventKind.WindowReassigned => "Window reassigned",
            OptimizationEventKind.PolicyStarted => "AI policy started",
            OptimizationEventKind.PolicyStopped => "AI policy paused",
            OptimizationEventKind.Skipped => "Skipped",
            _ => "Policy error"
        };
        IconGlyph = optimizationEvent.Kind switch
        {
            OptimizationEventKind.PriorityThrottled => "↓",
            OptimizationEventKind.PriorityRestored => "↑",
            OptimizationEventKind.WorkingSetTrimmed => "◒",
            OptimizationEventKind.CompositionCloaked => "◐",
            OptimizationEventKind.CompositionRestored => "◑",
            OptimizationEventKind.CapsuleRouted => "→",
            OptimizationEventKind.WindowReassigned => "⇢",
            OptimizationEventKind.PolicyStarted => "▶",
            OptimizationEventKind.PolicyStopped => "Ⅱ",
            _ => "!"
        };
    }

    public OptimizationEvent Event { get; }
    public string ProcessName => Event.ProcessName;
    public string Detail => Event.Detail;
    public string TimeLabel => Event.TimeLabel;
    public string ActionLabel { get; }
    public string IconGlyph { get; }
    public bool IsError => !Event.Succeeded;
    public bool IsSuccess => Event.Succeeded;
}
