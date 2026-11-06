using System.Diagnostics;
using ContextFlow.Infrastructure;
using ContextFlow.Interop;
using ContextFlow.Models;

namespace ContextFlow.Services;

/// <summary>
/// Applies conservative, explainable resource policies to inactive window
/// groups. The manager never touches foreground windows, the active capsule,
/// protected system processes, or high-priority processes. All original
/// process priorities are restored when the app exits or a group becomes active.
/// </summary>
public sealed class HardwareAIOptimizer : IAsyncDisposable
{
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services",
        "lsass", "lsaiso", "svchost", "dwm", "audiodg", "fontdrvhost", "sihost",
        "taskhostw", "ctfmon", "explorer", "searchindexer", "msmpeng", "securityhealthservice",
        "nissrv", "applicationframehost", "shellexperiencehost", "startmenuexperiencehost",
        "textinputhost", "gamebar", "gamebarftserver", "runtimebroker"
    };

    private readonly WindowManager _windows;
    private readonly TelemetryService _telemetry;
    private readonly LocalAiPolicyEngine _policy;
    private readonly SettingsService _settings;
    private readonly SessionGuard _sessionGuard;
    private readonly Func<IReadOnlyList<CapsuleDefinition>> _capsuleProvider;
    private readonly AppLog _log;
    private readonly SemaphoreSlim _evaluationGate = new(1, 1);
    private readonly object _throttleGate = new();
    private readonly Dictionary<int, ThrottleRecord> _throttled = new();
    private readonly Dictionary<int, DateTime> _lastTrim = new();

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private volatile bool _isEnabled;
    private volatile bool _isDisposed;

    public HardwareAIOptimizer(
        WindowManager windows,
        TelemetryService telemetry,
        LocalAiPolicyEngine policy,
        SettingsService settings,
        SessionGuard sessionGuard,
        Func<IReadOnlyList<CapsuleDefinition>> capsuleProvider,
        AppLog log)
    {
        _windows = windows;
        _telemetry = telemetry;
        _policy = policy;
        _settings = settings;
        _sessionGuard = sessionGuard;
        _capsuleProvider = capsuleProvider;
        _log = log;
    }

    public event Action<OptimizationEvent>? ActivityReported;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value)
            {
                return;
            }

            _isEnabled = value;
            if (!value)
            {
                RestoreAllPriorities("AI optimization paused.");
            }
            else
            {
                Report(new OptimizationEvent(
                    DateTimeOffset.Now,
                    "ContextFlow",
                    OptimizationEventKind.PolicyStarted,
                    "Local AI optimization enabled.",
                    true));
            }
        }
    }

    public string EngineLabel => _policy.EngineLabel;

    public bool IsOnnxAvailable => _policy.IsOnnxAvailable;

    public void Start()
    {
        if (_loopTask is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunAsync(_cts.Token));
        _log.Info("Hardware AI optimizer started.");
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // Allow the first UI frame and telemetry sample to settle.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (!cancellationToken.IsCancellationRequested)
        {
            await EvaluateSafeAsync(cancellationToken).ConfigureAwait(false);

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

    public async Task OptimizeNowAsync()
    {
        await EvaluateSafeAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task EvaluateSafeAsync(CancellationToken cancellationToken)
    {
        if (!_isEnabled || _isDisposed)
        {
            RestoreInactiveProcesses();
            return;
        }

        await _evaluationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => EvaluateAndApply(cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex)
        {
            _log.Error("AI optimization pass failed.", ex);
            Report(new OptimizationEvent(
                DateTimeOffset.Now,
                "ContextFlow",
                OptimizationEventKind.Error,
                "Optimization pass failed. The policy will retry.",
                false));
        }
        finally
        {
            _evaluationGate.Release();
        }
    }

    private void EvaluateAndApply(CancellationToken cancellationToken)
    {
        var snapshot = _windows.Snapshot;
        if (snapshot.Count == 0)
        {
            return;
        }

        var capsules = _capsuleProvider().ToDictionary(c => c.Id, StringComparer.Ordinal);
        var activeCapsuleId = _windows.ActiveCapsuleId;
        var telemetry = _telemetry.LastSample;
        var foregroundPids = snapshot.Where(w => w.IsForeground).Select(w => w.ProcessId).ToHashSet();
        var activePids = snapshot
            .Where(w => string.Equals(w.CapsuleId, activeCapsuleId, StringComparison.Ordinal))
            .Select(w => w.ProcessId)
            .ToHashSet();

        // A group that becomes active must regain its original scheduling class.
        RestorePrioritiesForPids(activePids);
        RestorePrioritiesForPids(foregroundPids);

        var currentPids = snapshot.Select(w => w.ProcessId).ToHashSet();
        RestoreMissingProcesses(currentPids);

        var candidates = new List<Candidate>();
        foreach (var group in snapshot.GroupBy(w => w.ProcessId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processId = group.Key;
            var windows = group.ToList();
            if (foregroundPids.Contains(processId) || activePids.Contains(processId))
            {
                continue;
            }

            var first = windows[0];
            if (IsProtected(first.ProcessName))
            {
                continue;
            }

            if (_settings.Current.ProcessExclusions.Any(p =>
                    string.Equals(p, first.ProcessName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var capsuleId = first.CapsuleId;
            if (string.Equals(capsuleId, activeCapsuleId, StringComparison.Ordinal))
            {
                continue;
            }

            var lastActivity = windows
                .Select(w => w.LastForegroundUtc == DateTime.MinValue ? w.FirstSeenUtc : w.LastForegroundUtc)
                .DefaultIfEmpty(DateTime.UtcNow)
                .Max();
            var idle = DateTime.UtcNow - lastActivity;
            if (idle < TimeSpan.FromSeconds(45))
            {
                continue;
            }

            if (!TryGetWorkingSetBytes(processId, out var workingSetBytes))
            {
                continue;
            }

            var workingSetMb = workingSetBytes / 1024d / 1024d;
            if (workingSetMb < 48)
            {
                continue;
            }

            var distance = ComputeViewportDistance(capsules, capsuleId, activeCapsuleId);
            var features = new PolicyFeatures(
                IdlePressure: (float)Math.Clamp(idle.TotalSeconds / 300d, 0, 1),
                InactiveCapsule: 1,
                MemoryPressure: (float)Math.Clamp(telemetry.RamPercent / 100d, 0, 1),
                WorkingSetPressure: (float)Math.Clamp(workingSetMb / 4096d, 0, 1),
                CpuPressure: (float)Math.Clamp(telemetry.CpuPercent / 100d, 0, 1),
                ViewportDistance: (float)Math.Clamp(distance, 0, 1),
                WindowCountPressure: (float)Math.Clamp(windows.Count / 10d, 0, 1),
                Minimized: windows.All(w => w.IsMinimized) ? 1 : 0,
                Foreground: 0);

            var score = _policy.Evaluate(features);
            var allMinimized = windows.All(w => w.IsMinimized);
            var canTrim = score >= 0.70 &&
                          idle >= TimeSpan.FromMinutes(2) &&
                          workingSetMb >= 180 &&
                          HasTrimCooldownElapsed(processId);

            var alreadyThrottled = IsAlreadyThrottled(processId);
            var canThrottle = score >= 0.52 && idle >= TimeSpan.FromSeconds(60) && !alreadyThrottled;

            if (!canTrim && !canThrottle)
            {
                continue;
            }

            candidates.Add(new Candidate(
                processId,
                first.ProcessName,
                windows.Count,
                capsuleId,
                idle,
                workingSetMb,
                score,
                canTrim,
                canThrottle,
                allMinimized));
        }

        // Bound the blast radius of any single pass.
        foreach (var candidate in candidates
                     .OrderByDescending(c => c.Score)
                     .ThenByDescending(c => c.WorkingSetMb)
                     .Take(3))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCandidate(candidate);
        }
    }

    private static double ComputeViewportDistance(
        IReadOnlyDictionary<string, CapsuleDefinition> capsules,
        string capsuleId,
        string activeCapsuleId)
    {
        if (!capsules.TryGetValue(capsuleId, out var capsule) ||
            !capsules.TryGetValue(activeCapsuleId, out var active))
        {
            return 1;
        }

        var capsuleX = capsule.CanvasX + capsule.CanvasWidth / 2;
        var capsuleY = capsule.CanvasY + capsule.CanvasHeight / 2;
        var activeX = active.CanvasX + active.CanvasWidth / 2;
        var activeY = active.CanvasY + active.CanvasHeight / 2;
        var dx = capsuleX - activeX;
        var dy = capsuleY - activeY;
        return Math.Sqrt(dx * dx + dy * dy) / 6000d;
    }

    private void ApplyCandidate(Candidate candidate)
    {
        var performed = new List<string>();
        var success = false;

        if (candidate.CanThrottle && TryThrottlePriority(candidate.ProcessId, candidate.ProcessName, candidate.AllMinimized))
        {
            performed.Add(candidate.TargetPriority == NativeMethods.IDLE_PRIORITY_CLASS ? "Idle priority" : "Below-normal priority");
            success = true;
        }

        if (candidate.CanTrim)
        {
            var trimmed = TryTrimWorkingSet(candidate.ProcessId, candidate.WorkingSetMb);
            if (trimmed is not null)
            {
                performed.Add(trimmed);
                success = true;
                lock (_throttleGate)
                {
                    _lastTrim[candidate.ProcessId] = DateTime.UtcNow;
                }
            }
        }

        if (!success)
        {
            return;
        }

        var kind = candidate.CanTrim && candidate.CanThrottle
            ? OptimizationEventKind.WorkingSetTrimmed
            : candidate.CanTrim
                ? OptimizationEventKind.WorkingSetTrimmed
                : OptimizationEventKind.PriorityThrottled;

        var detail = $"{string.Join(" · ", performed)} · AI score {candidate.Score:P0} · idle {candidate.Idle.TotalMinutes:0.#} min";
        var optimizationEvent = new OptimizationEvent(
            DateTimeOffset.Now,
            candidate.ProcessName,
            kind,
            detail,
            true);

        _log.Info($"AI optimized {candidate.ProcessName} (pid {candidate.ProcessId}): {detail}.");
        Report(optimizationEvent);
    }

    private bool TryThrottlePriority(int processId, string processName, bool allMinimized)
    {
        ThrottleRecord? existing;
        lock (_throttleGate)
        {
            _throttled.TryGetValue(processId, out existing);
        }

        if (existing is not null)
        {
            return true;
        }

        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_INFORMATION,
            false,
            (uint)processId);
        if (handle == IntPtr.Zero)
        {
            handle = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_SET_INFORMATION,
                false,
                (uint)processId);
        }

        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var originalPriority = NativeMethods.GetPriorityClass(handle);
            if (originalPriority == 0)
            {
                return false;
            }

            if (originalPriority is NativeMethods.HIGH_PRIORITY_CLASS or NativeMethods.REALTIME_PRIORITY_CLASS)
            {
                return false;
            }

            var targetPriority = allMinimized ? NativeMethods.IDLE_PRIORITY_CLASS : NativeMethods.BELOW_NORMAL_PRIORITY_CLASS;
            var startTime = TryGetStartTime(processId);

            // Journal intent before mutating external scheduling state. If the
            // process dies or the app crashes between these calls, startup
            // recovery sees a record whose current priority may still be
            // original and safely clears it.
            _sessionGuard.RegisterPriority(processId, processName, startTime, originalPriority, targetPriority);
            if (!NativeMethods.SetPriorityClass(handle, targetPriority))
            {
                _sessionGuard.UnregisterPriority(processId);
                return false;
            }

            ApplyEcoQos(handle, true);
            lock (_throttleGate)
            {
                _throttled[processId] = new ThrottleRecord(
                    processId,
                    processName,
                    startTime,
                    originalPriority,
                    targetPriority);
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not throttle {processName}: {ex.Message}");
            return false;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private string? TryTrimWorkingSet(int processId, double beforeMb)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_QUOTA,
            false,
            (uint)processId);

        if (handle == IntPtr.Zero)
        {
            handle = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_SET_QUOTA,
                false,
                (uint)processId);
        }

        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!NativeMethods.EmptyWorkingSet(handle))
            {
                return null;
            }

            var afterMb = TryGetWorkingSetBytes(processId, out var afterBytes)
                ? afterBytes / 1024d / 1024d
                : beforeMb;

            var reclaimed = Math.Max(0, beforeMb - afterMb);
            return reclaimed >= 1
                ? $"Working set trimmed ({beforeMb:0} MB → {afterMb:0} MB, {reclaimed:0} MB reclaimed)"
                : $"Working set trimmed ({beforeMb:0} MB)";
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private bool HasTrimCooldownElapsed(int processId)
    {
        lock (_throttleGate)
        {
            return !_lastTrim.TryGetValue(processId, out var last) ||
                   DateTime.UtcNow - last >= TimeSpan.FromMinutes(15);
        }
    }

    private bool IsAlreadyThrottled(int processId)
    {
        lock (_throttleGate)
        {
            return _throttled.ContainsKey(processId);
        }
    }

    private void RestorePrioritiesForPids(IEnumerable<int> processIds)
    {
        List<ThrottleRecord> records;
        lock (_throttleGate)
        {
            records = processIds
                .Distinct()
                .Where(_throttled.ContainsKey)
                .Select(id => _throttled[id])
                .ToList();
        }

        foreach (var record in records)
        {
            if (RestorePriority(record))
            {
                lock (_throttleGate)
                {
                    _throttled.Remove(record.ProcessId);
                }

                _sessionGuard.UnregisterPriority(record.ProcessId);
            }
            else if (!IsProcessAlive(record))
            {
                lock (_throttleGate)
                {
                    _throttled.Remove(record.ProcessId);
                }

                _sessionGuard.UnregisterPriority(record.ProcessId);
            }
        }
    }

    private void RestoreMissingProcesses(HashSet<int> currentPids)
    {
        List<int> missing;
        lock (_throttleGate)
        {
            missing = _throttled.Keys.Where(pid => !currentPids.Contains(pid)).ToList();
        }

        foreach (var pid in missing)
        {
            ThrottleRecord? record;
            lock (_throttleGate)
            {
                _throttled.TryGetValue(pid, out record);
            }

            if (record is not null && (RestorePriority(record) || !IsProcessAlive(record)))
            {
                lock (_throttleGate)
                {
                    _throttled.Remove(pid);
                }

                _sessionGuard.UnregisterPriority(pid);
            }
        }
    }

    private void RestoreInactiveProcesses() => RestoreAllPriorities(null);

    public void RestoreAllPriorities(string? reason = null)
    {
        List<ThrottleRecord> records;
        lock (_throttleGate)
        {
            records = _throttled.Values.ToList();
            _throttled.Clear();
            _lastTrim.Clear();
        }

        var restored = 0;
        foreach (var record in records)
        {
            if (RestorePriority(record))
            {
                restored++;
                _sessionGuard.UnregisterPriority(record.ProcessId);
            }
            else if (!IsProcessAlive(record))
            {
                _sessionGuard.UnregisterPriority(record.ProcessId);
            }
        }

        if (restored > 0)
        {
            _log.Info($"Restored priority for {restored} process(es). {reason}");
        }
    }

    private static bool RestorePriority(ThrottleRecord record)
    {
        var handle = NativeMethods.OpenProcess(
            NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_INFORMATION,
            false,
            (uint)record.ProcessId);
        if (handle == IntPtr.Zero)
        {
            handle = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_SET_INFORMATION,
                false,
                (uint)record.ProcessId);
        }

        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!IsSameProcess(record))
            {
                return true; // PID was reused; nothing of ours to restore.
            }

            var currentPriority = NativeMethods.GetPriorityClass(handle);
            if (currentPriority == record.ThrottledTo)
            {
                if (!NativeMethods.SetPriorityClass(handle, record.OriginalPriority))
                {
                    return false;
                }
            }

            ApplyEcoQos(handle, false);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static bool IsProcessAlive(ThrottleRecord record)
    {
        try
        {
            using var process = Process.GetProcessById(record.ProcessId);
            return IsSameProcess(record) && !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSameProcess(ThrottleRecord record)
    {
        try
        {
            using var process = Process.GetProcessById(record.ProcessId);
            if (!string.Equals(process.ProcessName, record.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (record.StartTimeUtc is not null)
            {
                var start = process.StartTime.ToUniversalTime();
                if (Math.Abs((start - record.StartTimeUtc.Value).TotalSeconds) > 1)
                {
                    return false;
                }
            }

            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static DateTime? TryGetStartTime(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetWorkingSetBytes(int processId, out long bytes)
    {
        bytes = 0;
        try
        {
            using var process = Process.GetProcessById(processId);
            bytes = process.WorkingSet64;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ApplyEcoQos(IntPtr processHandle, bool enable)
    {
        try
        {
            var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
            {
                Version = NativeMethods.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = enable ? NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0
            };
            NativeMethods.SetProcessInformation(
                processHandle,
                NativeMethods.ProcessPowerThrottling,
                ref state,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>());
        }
        catch
        {
            // EcoQoS is an optional enhancement; priority class remains applied.
        }
    }

    private bool IsProtected(string processName) =>
        ProtectedProcesses.Contains(processName) ||
        string.Equals(processName, "ContextFlow", StringComparison.OrdinalIgnoreCase);

    public void SetCompositionCloaking(bool enabled)
    {
        _windows.SetCapsuleIsolation(enabled);
        Report(new OptimizationEvent(
            DateTimeOffset.Now,
            "Window isolation",
            enabled ? OptimizationEventKind.CompositionCloaked : OptimizationEventKind.CompositionRestored,
            enabled
                ? "Inactive capsule windows are isolated. DWM cloaking is attempted first; Windows may require a reversible minimize fallback."
                : "All ContextFlow-isolated windows were restored.",
            true));
    }

    public void NotifyActiveCapsuleChanged(string capsuleId)
    {
        _ = Task.Run(() =>
        {
            var activePids = _windows.Snapshot
                .Where(w => string.Equals(w.CapsuleId, capsuleId, StringComparison.Ordinal))
                .Select(w => w.ProcessId)
                .ToHashSet();
            RestorePrioritiesForPids(activePids);
        });
    }

    private void Report(OptimizationEvent optimizationEvent) => ActivityReported?.Invoke(optimizationEvent);

    public async ValueTask DisposeAsync()
    {
        _isDisposed = true;
        CancellationTokenSource? cts;
        Task? loop;
        lock (_throttleGate)
        {
            cts = _cts;
            loop = _loopTask;
            _cts = null;
            _loopTask = null;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            try
            {
                if (loop is not null)
                {
                    await loop.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Shutdown.
            }
        }

        RestoreAllPriorities("Application shutdown.");
        _evaluationGate.Dispose();
        cts?.Dispose();
        _log.Info("Hardware AI optimizer stopped.");
    }

    private sealed record Candidate(
        int ProcessId,
        string ProcessName,
        int WindowCount,
        string CapsuleId,
        TimeSpan Idle,
        double WorkingSetMb,
        double Score,
        bool CanTrim,
        bool CanThrottle,
        bool AllMinimized)
    {
        public uint TargetPriority => AllMinimized
            ? NativeMethods.IDLE_PRIORITY_CLASS
            : NativeMethods.BELOW_NORMAL_PRIORITY_CLASS;
    }

    private sealed record ThrottleRecord(
        int ProcessId,
        string ProcessName,
        DateTime? StartTimeUtc,
        uint OriginalPriority,
        uint ThrottledTo);
}
