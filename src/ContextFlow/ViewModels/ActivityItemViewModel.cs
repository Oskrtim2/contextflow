using ContextFlow.Infrastructure;
using ContextFlow.Models;
using ContextFlow.Services;

namespace ContextFlow.ViewModels;

public sealed class ActivityItemViewModel
{
    public ActivityItemViewModel(OptimizationEvent optimizationEvent)
    {
        Event = optimizationEvent;
        Detail = optimizationEvent.LocalizedDetail;
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
            OptimizationEventKind.ProtectedAudio => LocalizationService.Get("EventAudioProtected"),
            OptimizationEventKind.LearningAdjusted => LocalizationService.Get("EventLearning"),
            OptimizationEventKind.ProcessEnded => LocalizationService.Get("EventProcessEnded"),
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
            OptimizationEventKind.ProtectedAudio => "♪",
            OptimizationEventKind.LearningAdjusted => "✦",
            OptimizationEventKind.ProcessEnded => "✕",
            _ => "!"
        };
    }

    public OptimizationEvent Event { get; }
    public string ProcessName => Event.ProcessName;
    public string Detail { get; } = string.Empty;
    public string TimeLabel => Event.TimeLabel;
    public string ActionLabel { get; }
    public string IconGlyph { get; }
    public bool IsError => !Event.Succeeded;
    public bool IsSuccess => Event.Succeeded;
}
