namespace ContextFlow.Models;

/// <summary>
/// Features consumed by the offline policy model (v2). Values are normalized
/// to 0..1; the feature order is part of the policy.onnx contract and must
/// match tools/generate_policy_model.py.
/// </summary>
public readonly record struct PolicyFeatures(
    float IdlePressure,
    float InactivePage,
    float MemoryPressure,
    float WorkingSetPressure,
    float SystemCpuPressure,
    float BackgroundCpuShare,
    float ViewportDistance,
    float Minimized,
    float Foreground,
    float LearnedAffinity,
    float LearnedRegret,
    float PredictedUseSoon)
{
    public const int Count = 12;

    public float[] ToArray() =>
    [
        IdlePressure,
        InactivePage,
        MemoryPressure,
        WorkingSetPressure,
        SystemCpuPressure,
        BackgroundCpuShare,
        ViewportDistance,
        Minimized,
        Foreground,
        LearnedAffinity,
        LearnedRegret,
        PredictedUseSoon
    ];
}

public enum OptimizationKind
{
    None,
    ThrottlePriority,
    TrimWorkingSet,
    ThrottleAndTrim
}

/// <summary>Result returned by the local policy engine.</summary>
public sealed record PolicyDecision(
    double Score,
    OptimizationKind Kind,
    string Rationale);
