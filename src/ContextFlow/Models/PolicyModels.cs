namespace ContextFlow.Models;

/// <summary>
/// Features consumed by the offline policy model. Values are normalized to
/// 0..1; the feature order is part of the policy.onnx contract.
/// </summary>
public readonly record struct PolicyFeatures(
    float IdlePressure,
    float InactiveCapsule,
    float MemoryPressure,
    float WorkingSetPressure,
    float CpuPressure,
    float ViewportDistance,
    float WindowCountPressure,
    float Minimized,
    float Foreground)
{
    public float[] ToArray() =>
    [
        IdlePressure,
        InactiveCapsule,
        MemoryPressure,
        WorkingSetPressure,
        CpuPressure,
        ViewportDistance,
        WindowCountPressure,
        Minimized,
        Foreground
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
