using System.Runtime.InteropServices;
using ContextFlow.Infrastructure;
using ContextFlow.Interop;
using ContextFlow.Models;

namespace ContextFlow.Services;

/// <summary>
/// Non-blocking 500 ms telemetry pipeline:
///   CPU  — GetSystemTimes deltas
///   RAM  — GlobalMemoryStatusEx
///   GPU  — PDH GPU Engine utilization counters (per-engine aggregation)
/// No network, performance counter service dependency, or GPU vendor SDK is needed.
/// </summary>
public sealed class TelemetryService : IAsyncDisposable
{
    private const string GpuCounterPath = @"\GPU Engine(*)\Utilization Percentage";

    private readonly AppLog _log;
    private readonly object _gate = new();
    private readonly object _sampleGate = new();
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private TelemetrySnapshot _lastSample;

    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;
    private bool _hasCpuBaseline;

    private IntPtr _gpuQuery;
    private IntPtr _gpuCounter;
    private bool _gpuInitialized;
    private bool _gpuUnavailable;
    private int _gpuWarmupSamples;

    public TelemetryService(AppLog log)
    {
        _log = log;
    }

    public event Action<TelemetrySnapshot>? SampleReceived;

    public TelemetrySnapshot LastSample
    {
        get
        {
            lock (_sampleGate)
            {
                return _lastSample;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_cts is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _pumpTask = Task.Run(() => RunAsync(_cts.Token));
        }

        _log.Info("Telemetry service started.");
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // Prime the PDH counters so the first meaningful GPU sample arrives
        // shortly after the UI is visible.
        TryInitializeGpu();

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var sample = Capture();
                lock (_sampleGate)
                {
                    _lastSample = sample;
                }

                SampleReceived?.Invoke(sample);
            }
            catch (Exception ex)
            {
                _log.Error("Telemetry sample failed.", ex);
            }

            try
            {
                await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private TelemetrySnapshot Capture()
    {
        var cpu = ReadCpuPercent();
        var ram = ReadMemory(out var ramUsedGb, out var ramTotalGb);
        var gpu = ReadGpuPercent();
        return new TelemetrySnapshot(cpu, ram, gpu, ramUsedGb, ramTotalGb, DateTimeOffset.Now);
    }

    private double ReadCpuPercent()
    {
        if (!NativeMethods.GetSystemTimes(out var idleFileTime, out var kernelFileTime, out var userFileTime))
        {
            return 0;
        }

        var idle = idleFileTime.ToUInt64();
        var kernel = kernelFileTime.ToUInt64();
        var user = userFileTime.ToUInt64();

        if (!_hasCpuBaseline)
        {
            _previousIdle = idle;
            _previousKernel = kernel;
            _previousUser = user;
            _hasCpuBaseline = true;
            return 0;
        }

        var idleDelta = idle - _previousIdle;
        var kernelDelta = kernel - _previousKernel;
        var userDelta = user - _previousUser;
        _previousIdle = idle;
        _previousKernel = kernel;
        _previousUser = user;

        var total = kernelDelta + userDelta;
        if (total == 0)
        {
            return 0;
        }

        // Kernel time includes idle time on Windows.
        var usage = (1.0 - (double)idleDelta / total) * 100.0;
        return Math.Clamp(usage, 0, 100);
    }

    private static double ReadMemory(out double usedGb, out double totalGb)
    {
        var status = NativeMethods.MEMORYSTATUSEX.Create();
        if (!NativeMethods.GlobalMemoryStatusEx(ref status) || status.ullTotalPhys == 0)
        {
            usedGb = 0;
            totalGb = 0;
            return 0;
        }

        var total = status.ullTotalPhys;
        var used = total - Math.Min(total, status.ullAvailPhys);
        totalGb = total / 1024d / 1024d / 1024d;
        usedGb = used / 1024d / 1024d / 1024d;
        return Math.Clamp(used * 100d / total, 0, 100);
    }

    private double? ReadGpuPercent()
    {
        if (_gpuUnavailable)
        {
            return null;
        }

        if (!_gpuInitialized)
        {
            TryInitializeGpu();
            return null;
        }

        var collectStatus = NativeMethods.PdhCollectQueryData(_gpuQuery);
        if (collectStatus != 0)
        {
            DisableGpu("GPU PDH collection failed.");
            return null;
        }

        if (_gpuWarmupSamples++ < 2)
        {
            return null;
        }

        if (!TryReadGpuCounter(out var perEngineValues))
        {
            return null;
        }

        if (perEngineValues.Count == 0)
        {
            return 0;
        }

        // Each process/engine instance belongs to an engine. Summing per engine
        // and taking the busiest engine gives a stable aggregate between
        // Task Manager's detailed view and common GPU monitoring tools.
        var perEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (instanceName, value) in perEngineValues)
        {
            var key = NormalizeEngineInstance(instanceName);
            perEngine.TryGetValue(key, out var current);
            perEngine[key] = current + value;
        }

        return Math.Clamp(perEngine.Values.DefaultIfEmpty(0).Max(), 0, 100);
    }

    private bool TryReadGpuCounter(out List<(string Instance, double Value)> values)
    {
        values = new List<(string Instance, double Value)>();
        if (_gpuCounter == IntPtr.Zero)
        {
            return false;
        }

        uint bufferSize = 0;
        var status = NativeMethods.PdhGetFormattedCounterArray(
            _gpuCounter,
            NativeMethods.PDH_FMT_DOUBLE,
            ref bufferSize,
            out var itemCount,
            IntPtr.Zero);

        if (status != NativeMethods.PDH_MORE_DATA && status != 0)
        {
            return false;
        }

        if (bufferSize == 0 || itemCount == 0)
        {
            return true;
        }

        var buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            status = NativeMethods.PdhGetFormattedCounterArray(
                _gpuCounter,
                NativeMethods.PDH_FMT_DOUBLE,
                ref bufferSize,
                out itemCount,
                buffer);

            if (status != 0)
            {
                return false;
            }

            var itemSize = Marshal.SizeOf<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM>();
            for (var i = 0; i < itemCount; i++)
            {
                var itemPtr = IntPtr.Add(buffer, i * itemSize);
                var item = Marshal.PtrToStructure<NativeMethods.PDH_FMT_COUNTERVALUE_ITEM>(itemPtr);
                if (item.FmtValue.CStatus != NativeMethods.PDH_CSTATUS_VALID_DATA &&
                    item.FmtValue.CStatus != NativeMethods.PDH_CSTATUS_NEW_DATA)
                {
                    continue;
                }

                var instance = item.szName == IntPtr.Zero
                    ? string.Empty
                    : Marshal.PtrToStringUni(item.szName) ?? string.Empty;
                values.Add((instance, item.FmtValue.DoubleValue));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return true;
    }

    private static string NormalizeEngineInstance(string instanceName)
    {
        // Instance shape:
        // pid_1234_luid_0x00000000_0x0000D1F5_phys_0_eng_0_engtype_3D
        var pidIndex = instanceName.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
        if (pidIndex == 0)
        {
            var nextSeparator = instanceName.IndexOf('_', 4);
            if (nextSeparator > 0)
            {
                return instanceName[(nextSeparator + 1)..];
            }
        }

        return instanceName;
    }

    private void TryInitializeGpu()
    {
        if (_gpuInitialized || _gpuUnavailable)
        {
            return;
        }

        try
        {
            var openStatus = NativeMethods.PdhOpenQuery(null, IntPtr.Zero, out var query);
            if (openStatus != 0)
            {
                DisableGpu($"PdhOpenQuery failed with 0x{openStatus:X8}.");
                return;
            }

            var counterStatus = NativeMethods.PdhAddEnglishCounter(query, GpuCounterPath, IntPtr.Zero, out var counter);
            if (counterStatus != 0)
            {
                NativeMethods.PdhCloseQuery(query);
                DisableGpu($"GPU Engine counters unavailable (0x{counterStatus:X8}).");
                return;
            }

            _gpuQuery = query;
            _gpuCounter = counter;
            _gpuInitialized = true;
            _gpuWarmupSamples = 0;
            NativeMethods.PdhCollectQueryData(_gpuQuery);
            _log.Info("GPU telemetry initialized through PDH GPU Engine counters.");
        }
        catch (DllNotFoundException)
        {
            DisableGpu("pdh.dll is not available.");
        }
        catch (Exception ex)
        {
            DisableGpu("GPU telemetry initialization failed.");
            _log.Error("GPU telemetry initialization error.", ex);
        }
    }

    private void DisableGpu(string reason)
    {
        _gpuUnavailable = true;
        _log.Info(reason);
        CloseGpuQuery();
    }

    private void CloseGpuQuery()
    {
        if (_gpuQuery != IntPtr.Zero)
        {
            try
            {
                NativeMethods.PdhCloseQuery(_gpuQuery);
            }
            catch
            {
                // Best effort.
            }

            _gpuQuery = IntPtr.Zero;
            _gpuCounter = IntPtr.Zero;
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cts;
        Task? pump;
        lock (_gate)
        {
            cts = _cts;
            pump = _pumpTask;
            _cts = null;
            _pumpTask = null;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            try
            {
                if (pump is not null)
                {
                    await pump.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        CloseGpuQuery();
        cts?.Dispose();
        _log.Info("Telemetry service stopped.");
    }
}
