namespace ContextFlow.Models;

/// <summary>One process from the system-wide process table, with smoothed CPU.</summary>
public sealed record ProcessStat(
    int ProcessId,
    int ParentProcessId,
    string Name,
    long CreateTime,
    double CpuPercent,
    long WorkingSetBytes,
    long PrivateWorkingSetBytes,
    long PrivateBytes,
    int Threads,
    int SessionId,
    int FamilyRootId);

/// <summary>
/// An application and every helper process it spawned (browser renderers,
/// WebView2 hosts, GPU/utility processes...). Optimizations act on the whole
/// family, so a browser is handled as one app instead of as a single process.
/// </summary>
public sealed record AppFamily(
    int RootProcessId,
    string Name,
    IReadOnlyList<int> ProcessIds,
    double CpuPercent,
    double SustainedCpuPercent,
    long WorkingSetBytes,
    long PrivateWorkingSetBytes)
{
    public double MemoryMb => PrivateWorkingSetBytes / 1024d / 1024d;
    public double WorkingSetMb => WorkingSetBytes / 1024d / 1024d;
}

/// <summary>Immutable process table captured by <c>ProcessMonitor</c>.</summary>
public sealed class ProcessSnapshot
{
    public static readonly ProcessSnapshot Empty = new(
        new Dictionary<int, ProcessStat>(),
        new Dictionary<int, AppFamily>(),
        DateTime.MinValue);

    public ProcessSnapshot(
        IReadOnlyDictionary<int, ProcessStat> processes,
        IReadOnlyDictionary<int, AppFamily> families,
        DateTime timestampUtc)
    {
        Processes = processes;
        Families = families;
        TimestampUtc = timestampUtc;
    }

    public IReadOnlyDictionary<int, ProcessStat> Processes { get; }

    /// <summary>Families keyed by root process id.</summary>
    public IReadOnlyDictionary<int, AppFamily> Families { get; }

    public DateTime TimestampUtc { get; }

    /// <summary>Returns the family that owns <paramref name="processId"/>, if known.</summary>
    public AppFamily? FamilyOf(int processId) =>
        Processes.TryGetValue(processId, out var stat) &&
        Families.TryGetValue(stat.FamilyRootId, out var family)
            ? family
            : null;
}

/// <summary>How hard the optimizer works. Persisted in settings.</summary>
public enum AiProfile
{
    Conservative,
    Balanced,
    Aggressive
}

/// <summary>Live system pressure as judged by the optimizer.</summary>
public enum PressureLevel
{
    Relaxed,
    Normal,
    High,
    Critical
}

/// <summary>Per-app optimization state surfaced in the Performance tab.</summary>
public enum AppOptimizationState
{
    None,
    Optimized,
    Protected,
    PlayingAudio,
    Active
}

/// <summary>Aggregate counters for the AI savings panel.</summary>
public readonly record struct AiStats(
    PressureLevel Pressure,
    TimeSpan PassInterval,
    int OptimizedNow,
    double ReclaimedMbSession,
    double CpuContainedPercent,
    int ActionsSession,
    int LearnedApps,
    double Accuracy,
    bool OnBattery);
