using ContextFlow.Infrastructure;
using ContextFlow.Models;
using ContextFlow.Services;

namespace ContextFlow.ViewModels;

public sealed class ActivityItemViewModel
{
    public ActivityItemViewModel(OptimizationEvent optimizationEvent)
    {
        Event = optimizationEvent;
        ActionLabel = optimizationEvent.Kind switch
        {
            OptimizationEventKind.PriorityThrottled => LocalizationService.Get("EventPriorityThrottled"),
            OptimizationEventKind.PriorityRestored => LocalizationService.Get("EventPriorityRestored"),
            OptimizationEventKind.WorkingSetTrimmed => LocalizationService.Get("EventWorkingSetTrimmed"),
            OptimizationEventKind.CompositionCloaked => LocalizationService.Get("EventIsolationEnabled"),
            OptimizationEventKind.CompositionRestored => LocalizationService.Get("EventIsolationDisabled"),
            OptimizationEventKind.CapsuleRouted => LocalizationService.Get("EventPageRouted"),
            OptimizationEventKind.WindowReassigned => LocalizationService.Get("EventWindowReassigned"),
            OptimizationEventKind.PolicyStarted => LocalizationService.Get("EventPolicyStarted"),
            OptimizationEventKind.PolicyStopped => LocalizationService.Get("EventPolicyPaused"),
            OptimizationEventKind.Skipped => LocalizationService.Get("EventSkipped"),
            _ => LocalizationService.Get("EventPolicyError")
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
