namespace ContextFlow.Models;

/// <summary>
/// One asynchronous sample of system telemetry. Values are percentages in the
/// range 0..100. GPU is null when the platform exposes no GPU Engine PDH data.
/// </summary>
public readonly record struct TelemetrySnapshot(
    double CpuPercent,
    double RamPercent,
    double? GpuPercent,
    double RamUsedGb,
    double RamTotalGb,
    DateTimeOffset Timestamp)
{
    public bool HasGpu => GpuPercent.HasValue;
}
