using System.Diagnostics;
using System.Runtime.InteropServices;
using ContextFlow.Infrastructure;
using ContextFlow.Interop;
using ContextFlow.Models;

namespace ContextFlow.Services;

/// <summary>
/// Adaptive, explainable resource optimizer (AI v2).
///
/// Improvements over v1:
/// <list type="bullet">
/// <item>Acts on whole application families (a browser and all its renderers), not one PID.</item>
/// <item>Measures real per-app CPU from the process table and catches background CPU hogs.</item>
/// <item>Adapts cadence, thresholds and blast radius to live memory/CPU pressure, the selected
/// profile and battery state. Working sets are only trimmed when memory is actually scarce.</item>
/// <item>Lowers memory priority so Windows reclaims pages from idle apps first (a soft trim).</item>
/// <item>Never touches apps that are playing audio, and restores an app the instant it is focused.</item>
/// <item>Learns from the user (affinity, daily rhythm, regret) through <see cref="AdaptiveUsageModel"/>.</item>
/// </list>
/// Foreground windows, visible windows on the active page, protected system processes,
/// services (session 0), high-priority processes and user exclusions are never touched.
/// Every change is journaled and reverted on activation, pause, exit or crash recovery.
/// </summary>
public sealed class HardwareAIOptimizer : IAsyncDisposable
{
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services",
        "lsass", "lsaiso", "svchost", "dwm", "audiodg", "fontdrvhost", "sihost",
        "taskhostw", "ctfmon", "explorer", "searchindexer", "searchhost", "msmpeng", "securityhealthservice",
        "securityhealthsystray", "nissrv", "applicationframehost", "shellexperiencehost", "startmenuexperiencehost",
        "textinputhost", "gamebar", "gamebarftserver", "runtimebroker", "memcompression", "conhost",
        "dllhost", "widgets", "lockapp", "userinit", "smartscreen", "wudfhost", "spoolsv", "ContextFlow"
    };

    private readonly WindowManager _windows;
    private readonly TelemetryService _telemetry;
    private readonly LocalAiPolicyEngine _policy;
    private readonly SettingsService _settings;
    private readonly SessionGuard _sessionGuard;
    private readonly ProcessMonitor _processes;
    private readonly AdaptiveUsageModel _usage;
    private readonly Func<IReadOnlyList<CapsuleDefinition>> _capsuleProvider;
    private readonly AppLog _log;
    private readonly SemaphoreSlim _evaluationGate = new(1, 1);
    private readonly object _throttleGate = new();
    private readonly Dictionary<int, ThrottleRecord> _throttled = new();
    private readonly Dictionary<int, DateTime> _lastFamilyTrim = new();
    private readonly Dictionary<int, double> _familyCpuAtThrottle = new();
    private readonly HashSet<int> _audioReported = new();
    private readonly int _ownProcessId = Environment.ProcessId;
    private readonly int _ownSessionId = Process.GetCurrentProcess().SessionId;

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _wake;
    private Task? _loopTask;
    private volatile bool _isEnabled;
    private volatile bool _isDisposed;
    private volatile int _foregroundRoot = -1;
    private HashSet<int> _visibleActiveRoots = new();
    private double _reclaimedMb;
    private int _actions;
    private PressureLevel _pressure = PressureLevel.Normal;
    private TimeSpan _interval = TimeSpan.FromSeconds(8);

    public HardwareAIOptimizer(
        WindowManager windows,
        TelemetryService telemetry,
        LocalAiPolicyEngine policy,
        SettingsService settings,
        SessionGuard sessionGuard,
        ProcessMonitor processes,
        AdaptiveUsageModel usage,
        Func<IReadOnlyList<CapsuleDefinition>> capsuleProvider,
        AppLog log)
    {
        _windows = windows;
        _telemetry = telemetry;
        _policy = policy;
        _settings = settings;
        _sessionGuard = sessionGuard;
        _processes = processes;
        _usage = usage;
        _capsuleProvider = capsuleProvider;
        _log = log;

        _windows.WindowsChanged += OnWindowsChanged;
        _usage.RegretDetected += OnRegretDetected;
    }

    public event Action<OptimizationEvent>? ActivityReported;

    public event Action<AiStats>? StatsChanged;

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
                PublishStats();
            }
            else
            {
                Report(new OptimizationEvent(
                    DateTimeOffset.Now,
                    "ContextFlow",
                    OptimizationEventKind.PolicyStarted,
                    "Local AI optimization enabled.",
                    true,
                    "AiEnabledDetail"));
                Wake();
            }
        }
    }

    public string EngineLabel => _policy.EngineLabel;

    public bool IsOnnxAvailable => _policy.IsOnnxAvailable;

    public AdaptiveUsageModel Usage => _usage;

    public AiStats CurrentStats
    {
        get
        {
            lock (_throttleGate)
            {
                return BuildStatsLocked();
            }
        }
    }

    public void Start()
    {
        if (_loopTask is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunAsync(_cts.Token));
        _log.Info("Hardware AI optimizer v2 started.");
    }

    /// <summary>Re-evaluates immediately (profile changed, AI resumed...).</summary>
    public void Wake()
    {
        try
        {
            _wake?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Let the first telemetry and process samples settle.
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await EvaluateSafeAsync(cancellationToken).ConfigureAwait(false);

            using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _wake = wake;
            try
            {
                await Task.Delay(_interval, wake.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
            finally
            {
                _wake = null;
            }
        }
    }

    public async Task OptimizeNowAsync()
    {
        _processes.SampleNow();
        await EvaluateSafeAsync(CancellationToken.None, manual: true).ConfigureAwait(false);
    }

    private async Task EvaluateSafeAsync(CancellationToken cancellationToken, bool manual = false)
    {
        _usage.Tick();
        if (!_isEnabled || _isDisposed)
        {
            return;
        }

        await _evaluationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => EvaluateAndApply(manual, cancellationToken), cancellationToken).ConfigureAwait(false);
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
                false,
                "AiPassFailed"));
        }
        finally
        {
            _evaluationGate.Release();
            PublishStats();
        }
    }

    // ------------------------------------------------------------------ policy pass

    private void EvaluateAndApply(bool manual, CancellationToken cancellationToken)
    {
        var processes = _processes.Snapshot;
        if (processes.Processes.Count == 0)
        {
            processes = _processes.SampleNow();
        }

        var windows = _windows.Snapshot;
        var telemetry = _telemetry.LastSample;
        var onBattery = IsOnBattery();
        var profile = _settings.Current.AiProfile;
        var pressure = ComputePressure(telemetry);
        var tuning = Tuning.For(profile, pressure, onBattery, manual);
        _pressure = pressure;
        _interval = tuning.Interval;

        var capsules = _capsuleProvider().ToDictionary(c => c.Id, StringComparer.Ordinal);
        var activeCapsuleId = _windows.ActiveCapsuleId;

        int RootOf(int pid) => processes.FamilyOf(pid)?.RootProcessId ?? pid;

        // Families the user can see right now are never optimized.
        var protectedRoots = windows
            .Where(w => w.IsForeground ||
                        (string.Equals(w.CapsuleId, activeCapsuleId, StringComparison.Ordinal) &&
                         !w.IsMinimized && !w.IsCloaked))
            .Select(w => RootOf(w.ProcessId))
            .ToHashSet();
        _visibleActiveRoots = protectedRoots;

        RestoreFamilies(protectedRoots, "visible");
        RestoreMissingProcesses(processes);

        var candidates = new List<Candidate>();
        var windowedRoots = new HashSet<int>();

        foreach (var group in windows.GroupBy(w => RootOf(w.ProcessId)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            windowedRoots.Add(group.Key);
            if (protectedRoots.Contains(group.Key) ||
                !processes.Families.TryGetValue(group.Key, out var family))
            {
                continue;
            }

            var groupWindows = group.ToList();
            if (IsFamilyProtected(family, groupWindows.Select(w => w.ProcessName)))
            {
                continue;
            }

            if (CheckAudio(family))
            {
                continue;
            }

            var lastActivity = groupWindows
                .Select(w => w.LastForegroundUtc == DateTime.MinValue ? w.FirstSeenUtc : w.LastForegroundUtc)
                .Max();
            var idle = DateTime.UtcNow - lastActivity;
            var inactivePage = groupWindows.All(w => !string.Equals(w.CapsuleId, activeCapsuleId, StringComparison.Ordinal));
            var allMinimized = groupWindows.All(w => w.IsMinimized || w.IsCloaked);
            var distance = inactivePage
                ? groupWindows.Min(w => ComputeViewportDistance(capsules, w.CapsuleId, activeCapsuleId))
                : 0;

            var candidate = BuildCandidate(family, idle, inactivePage ? 1f : 0f, allMinimized, distance, telemetry, tuning);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        // Windowless user apps (tray apps, updaters, dev servers...) are only
        // considered when they burn CPU in the background or memory is scarce.
        foreach (var family in processes.Families.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (windowedRoots.Contains(family.RootProcessId) ||
                !processes.Processes.TryGetValue(family.RootProcessId, out var root) ||
                root.SessionId != _ownSessionId ||
                root.SessionId == 0)
            {
                continue;
            }

            var hog = family.SustainedCpuPercent >= tuning.BackgroundHogCpu;
            var heavy = pressure >= PressureLevel.High && family.WorkingSetMb >= 150;
            if ((!hog && !heavy) || IsFamilyProtected(family, Array.Empty<string>()) || CheckAudio(family))
            {
                continue;
            }

            var candidate = BuildCandidate(family, TimeSpan.FromHours(1), 0.5f, true, 0.5, telemetry, tuning);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        foreach (var candidate in candidates
                     .OrderByDescending(c => c.Priority)
                     .Take(tuning.MaxActions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCandidate(candidate, processes);
        }
    }

    private Candidate? BuildCandidate(
        AppFamily family,
        TimeSpan idle,
        float inactivePage,
        bool allMinimized,
        double distance,
        TelemetrySnapshot telemetry,
        Tuning tuning)
    {
        if (idle < tuning.MinIdleThrottle && idle < tuning.MinIdleTrim)
        {
            return null;
        }

        var learned = _usage.GetSignals(family.Name);
        var features = new PolicyFeatures(
            IdlePressure: (float)Math.Clamp(idle.TotalSeconds / 300d, 0, 1),
            InactivePage: inactivePage,
            MemoryPressure: (float)Math.Clamp(telemetry.RamPercent / 100d, 0, 1),
            WorkingSetPressure: (float)Math.Clamp(family.WorkingSetMb / 4096d, 0, 1),
            SystemCpuPressure: (float)Math.Clamp(telemetry.CpuPercent / 100d, 0, 1),
            BackgroundCpuShare: (float)Math.Clamp(family.SustainedCpuPercent / 25d, 0, 1),
            ViewportDistance: (float)Math.Clamp(distance, 0, 1),
            Minimized: allMinimized ? 1 : 0,
            Foreground: 0,
            LearnedAffinity: (float)learned.Affinity,
            LearnedRegret: (float)learned.RegretRate,
            PredictedUseSoon: (float)learned.PredictedUseSoon);

        var score = _policy.Evaluate(features);
        var alreadyThrottled = IsFamilyThrottled(family);
        var canThrottle = !alreadyThrottled &&
                          score >= tuning.ThrottleThreshold &&
                          idle >= tuning.MinIdleThrottle;
        var canTrim = tuning.TrimAllowed &&
                      score >= tuning.TrimThreshold &&
                      idle >= tuning.MinIdleTrim &&
                      family.WorkingSetMb >= 200 &&
                      HasTrimCooldownElapsed(family.RootProcessId, tuning.TrimCooldown);

        if (!canThrottle && !canTrim)
        {
            return null;
        }

        // Rank by expected benefit, not just by confidence.
        var priority = score * (1 + family.WorkingSetMb / 2048d + family.SustainedCpuPercent / 20d);
        return new Candidate(family, idle, score, priority, canThrottle, canTrim, allMinimized);
    }

    private void ApplyCandidate(Candidate candidate, ProcessSnapshot processes)
    {
        var family = candidate.Family;
        var members = family.ProcessIds
            .Where(pid => pid != _ownProcessId &&
                          processes.Processes.TryGetValue(pid, out var p) &&
                          !ProtectedProcesses.Contains(p.Name))
            .ToList();
        if (members.Count == 0)
        {
            return;
        }

        var throttled = 0;
        if (candidate.CanThrottle)
        {
            // Idle class for minimized apps that still burn CPU; below-normal otherwise.
            var target = candidate.AllMinimized && family.SustainedCpuPercent >= 2
                ? NativeMethods.IDLE_PRIORITY_CLASS
                : NativeMethods.BELOW_NORMAL_PRIORITY_CLASS;
            foreach (var pid in members)
            {
                if (TryThrottle(processes.Processes[pid], family, target))
                {
                    throttled++;
                }
            }

            if (throttled > 0)
            {
                lock (_throttleGate)
                {
                    _familyCpuAtThrottle[family.RootProcessId] = family.SustainedCpuPercent;
                }
            }
        }

        double reclaimed = 0;
        var trimmed = false;
        if (candidate.CanTrim)
        {
            foreach (var pid in members)
            {
                var stat = processes.Processes[pid];
                if (stat.WorkingSetBytes < 16L * 1024 * 1024)
                {
                    continue;
                }

                var freed = TryTrimWorkingSet(pid, stat.WorkingSetBytes);
                if (freed >= 0)
                {
                    trimmed = true;
                    reclaimed += freed;
                }
            }

            if (trimmed)
            {
                lock (_throttleGate)
                {
                    _lastFamilyTrim[family.RootProcessId] = DateTime.UtcNow;
                    _reclaimedMb += reclaimed;
                }
            }
        }

        if (throttled == 0 && !trimmed)
        {
            return;
        }

        lock (_throttleGate)
        {
            _actions++;
        }

        _usage.RecordOptimization(family.Name);

        var idleMinutes = candidate.Idle >= TimeSpan.FromMinutes(59) ? "—" : $"{candidate.Idle.TotalMinutes:0.#}";
        string key;
        object[] args;
        if (throttled > 0 && trimmed)
        {
            key = "AiDetailThrottleTrim";
            args = [throttled, reclaimed, candidate.Score, idleMinutes, family.SustainedCpuPercent];
        }
        else if (trimmed)
        {
            key = "AiDetailTrim";
            args = [members.Count, reclaimed, candidate.Score, idleMinutes];
        }
        else
        {
            key = "AiDetailThrottle";
            args = [throttled, candidate.Score, idleMinutes, family.SustainedCpuPercent];
        }

        var detail = $"{throttled} throttled · {reclaimed:0} MB reclaimed · AI score {candidate.Score:P0} · idle {idleMinutes} min · CPU {family.SustainedCpuPercent:0.#}%";
        _log.Info($"AI optimized {family.Name} (root {family.RootProcessId}, {members.Count} processes): {detail}.");
        Report(new OptimizationEvent(
            DateTimeOffset.Now,
            family.Name,
            trimmed ? OptimizationEventKind.WorkingSetTrimmed : OptimizationEventKind.PriorityThrottled,
            detail,
            true,
            key,
            args));
    }

    // ------------------------------------------------------------------ manual actions (Performance tab)

    /// <summary>Optimizes one family right now, regardless of score (user request).</summary>
    public void OptimizeFamilyNow(int rootProcessId)
    {
        var processes = _processes.SampleNow();
        if (!processes.Families.TryGetValue(rootProcessId, out var family) ||
            IsFamilyProtected(family, Array.Empty<string>()) ||
            rootProcessId == _foregroundRoot ||
            _visibleActiveRoots.Contains(rootProcessId))
        {
            // Apps the user is looking at are never throttled, even on request:
            // they would be restored on the next pass and only suffer page faults.
            return;
        }

        ApplyCandidate(
            new Candidate(family, TimeSpan.FromHours(1), 1, 1, !IsFamilyThrottled(family), true, true),
            processes);
        PublishStats();
    }

    public void RestoreFamilyNow(int rootProcessId)
    {
        RestoreFamilies(new HashSet<int> { rootProcessId }, "user");
        PublishStats();
    }

    /// <summary>Adds or removes an app from the user's exclusion list.</summary>
    public bool ToggleProtection(AppFamily family)
    {
        var exclusions = _settings.Current.ProcessExclusions;
        var existing = exclusions.FirstOrDefault(e => string.Equals(e, family.Name, StringComparison.OrdinalIgnoreCase));
        bool nowProtected;
        if (existing is not null)
        {
            exclusions.Remove(existing);
            nowProtected = false;
        }
        else
        {
            exclusions.Add(family.Name);
            RestoreFamilies(new HashSet<int> { family.RootProcessId }, "protected");
            nowProtected = true;
        }

        _settings.Save(_settings.Current);
        PublishStats();
        return nowProtected;
    }

    /// <summary>Ends an application and every process it spawned.</summary>
    public bool TryEndFamily(AppFamily family, out string? error)
    {
        error = null;
        if (IsProtectedName(family.Name) || family.ProcessIds.Contains(_ownProcessId))
        {
            error = "protected";
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(family.RootProcessId);
            process.Kill(entireProcessTree: true);
            _log.Info($"User ended {family.Name} (root {family.RootProcessId}, {family.ProcessIds.Count} processes).");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.Warn($"Could not end {family.Name}: {ex.Message}");
            return false;
        }
    }

    public AppOptimizationState GetState(AppFamily family)
    {
        if (IsFamilyThrottled(family))
        {
            return AppOptimizationState.Optimized;
        }

        if (_processes.Audio.IsAudible(family.ProcessIds))
        {
            return AppOptimizationState.PlayingAudio;
        }

        if (IsProtectedName(family.Name) || IsExcluded(family.Name))
        {
            return AppOptimizationState.Protected;
        }

        if (family.RootProcessId == _foregroundRoot || _visibleActiveRoots.Contains(family.RootProcessId))
        {
            return AppOptimizationState.Active;
        }

        return AppOptimizationState.None;
    }

    public bool IsExcluded(string name) =>
        _settings.Current.ProcessExclusions.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));

    public static bool IsProtectedName(string name) => ProtectedProcesses.Contains(name);

    // ------------------------------------------------------------------ instant restore + learning

    private void OnWindowsChanged(object? sender, WindowsChangedEventArgs e)
    {
        try
        {
            var foreground = e.Windows.FirstOrDefault(w => w.IsForeground);
            if (foreground is null)
            {
                return;
            }

            var family = _processes.Snapshot.FamilyOf(foreground.ProcessId);
            var root = family?.RootProcessId ?? foreground.ProcessId;
            _usage.ObserveForeground(family?.Name ?? foreground.ProcessName);

            if (root == _foregroundRoot)
            {
                return;
            }

            _foregroundRoot = root;
            bool throttled;
            lock (_throttleGate)
            {
                throttled = _throttled.Values.Any(r => r.FamilyRootId == root);
            }

            if (throttled)
            {
                // Focused app gets its full priority back immediately, not on the next pass.
                RestoreFamilies(new HashSet<int> { root }, "focus");
                PublishStats();
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Foreground restore check failed: {ex.Message}");
        }
    }

    private void OnRegretDetected(string appName)
    {
        Report(new OptimizationEvent(
            DateTimeOffset.Now,
            appName,
            OptimizationEventKind.LearningAdjusted,
            $"You returned to {appName} soon after it was optimized; the AI will protect it more.",
            true,
            "AiLearnedRegret",
            [appName]));
    }

    private bool CheckAudio(AppFamily family)
    {
        if (!_processes.Audio.IsAudible(family.ProcessIds))
        {
            lock (_throttleGate)
            {
                _audioReported.Remove(family.RootProcessId);
            }

            return false;
        }

        if (IsFamilyThrottled(family))
        {
            RestoreFamilies(new HashSet<int> { family.RootProcessId }, "audio");
        }

        bool first;
        lock (_throttleGate)
        {
            first = _audioReported.Add(family.RootProcessId);
        }

        if (first)
        {
            Report(new OptimizationEvent(
                DateTimeOffset.Now,
                family.Name,
                OptimizationEventKind.ProtectedAudio,
                $"{family.Name} is playing audio; it will not be throttled.",
                true,
                "AiAudioProtected",
                [family.Name]));
        }

        return true;
    }

    // ------------------------------------------------------------------ throttle / trim primitives

    private bool TryThrottle(ProcessStat process, AppFamily family, uint targetPriority)
    {
        lock (_throttleGate)
        {
            if (_throttled.ContainsKey(process.ProcessId))
            {
                return false;
            }
        }

        var handle = OpenForSet(process.ProcessId, NativeMethods.PROCESS_SET_INFORMATION);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var originalPriority = NativeMethods.GetPriorityClass(handle);
            if (originalPriority == 0 ||
                originalPriority is NativeMethods.HIGH_PRIORITY_CLASS or NativeMethods.REALTIME_PRIORITY_CLASS or NativeMethods.IDLE_PRIORITY_CLASS)
            {
                return false;
            }

            var startTime = DateTime.FromFileTimeUtc(process.CreateTime);

            // Journal intent before mutating external scheduling state so crash
            // recovery can always put the original class back.
            _sessionGuard.RegisterPriority(process.ProcessId, process.Name, startTime, originalPriority, targetPriority);
            if (!NativeMethods.SetPriorityClass(handle, targetPriority))
            {
                _sessionGuard.UnregisterPriority(process.ProcessId);
                return false;
            }

            ApplyEcoQos(handle, true);
            ApplyMemoryPriority(handle, NativeMethods.MEMORY_PRIORITY_LOW);
            lock (_throttleGate)
            {
                _throttled[process.ProcessId] = new ThrottleRecord(
                    process.ProcessId,
                    process.Name,
                    process.CreateTime,
                    family.RootProcessId,
                    family.Name,
                    originalPriority,
                    targetPriority);
            }

            return true;
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not throttle {process.Name} ({process.ProcessId}): {ex.Message}");
            return false;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>Returns MB reclaimed, or -1 when the trim could not be applied.</summary>
    private static double TryTrimWorkingSet(int processId, long beforeBytes)
    {
        var handle = OpenForSet(processId, NativeMethods.PROCESS_SET_QUOTA);
        if (handle == IntPtr.Zero)
        {
            return -1;
        }

        try
        {
            if (!NativeMethods.EmptyWorkingSet(handle))
            {
                return -1;
            }

            var after = TryGetWorkingSetBytes(processId, out var afterBytes) ? afterBytes : beforeBytes;
            return Math.Max(0, (beforeBytes - after) / 1024d / 1024d);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static IntPtr OpenForSet(int processId, uint setRight)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_INFORMATION | setRight, false, (uint)processId);
        if (handle == IntPtr.Zero)
        {
            handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | setRight, false, (uint)processId);
        }

        return handle;
    }

    private bool HasTrimCooldownElapsed(int rootProcessId, TimeSpan cooldown)
    {
        lock (_throttleGate)
        {
            return !_lastFamilyTrim.TryGetValue(rootProcessId, out var last) || DateTime.UtcNow - last >= cooldown;
        }
    }

    private bool IsFamilyThrottled(AppFamily family)
    {
        lock (_throttleGate)
        {
            return _throttled.Values.Any(r => r.FamilyRootId == family.RootProcessId);
        }
    }

    private bool IsFamilyProtected(AppFamily family, IEnumerable<string> windowProcessNames) =>
        family.ProcessIds.Contains(_ownProcessId) && family.ProcessIds.Count <= 1 ||
        IsProtectedName(family.Name) ||
        IsExcluded(family.Name) ||
        windowProcessNames.Any(n => IsProtectedName(n) || IsExcluded(n));

    // ------------------------------------------------------------------ restore

    private void RestoreFamilies(HashSet<int> rootIds, string reason)
    {
        if (rootIds.Count == 0)
        {
            return;
        }

        List<ThrottleRecord> records;
        lock (_throttleGate)
        {
            records = _throttled.Values.Where(r => rootIds.Contains(r.FamilyRootId)).ToList();
        }

        if (records.Count == 0)
        {
            return;
        }

        var processes = _processes.Snapshot;
        foreach (var record in records)
        {
            RestoreRecord(record, processes);
        }

        lock (_throttleGate)
        {
            foreach (var root in rootIds)
            {
                _familyCpuAtThrottle.Remove(root);
            }
        }

        _log.Info($"Restored {records.Count} process(es) of {records[0].FamilyName} ({reason}).");
        if (reason is "focus" or "user")
        {
            Report(new OptimizationEvent(
                DateTimeOffset.Now,
                records[0].FamilyName,
                OptimizationEventKind.PriorityRestored,
                $"Full priority restored to {records.Count} process(es).",
                true,
                "AiRestoredDetail",
                [records.Count]));
        }
    }

    private void RestoreMissingProcesses(ProcessSnapshot processes)
    {
        List<ThrottleRecord> missing;
        lock (_throttleGate)
        {
            missing = _throttled.Values
                .Where(r => !processes.Processes.TryGetValue(r.ProcessId, out var p) || p.CreateTime != r.CreateTime)
                .ToList();
        }

        foreach (var record in missing)
        {
            lock (_throttleGate)
            {
                _throttled.Remove(record.ProcessId);
                if (!_throttled.Values.Any(r => r.FamilyRootId == record.FamilyRootId))
                {
                    _familyCpuAtThrottle.Remove(record.FamilyRootId);
                }
            }

            _sessionGuard.UnregisterPriority(record.ProcessId);
        }
    }

    private void RestoreRecord(ThrottleRecord record, ProcessSnapshot processes)
    {
        var alive = processes.Processes.TryGetValue(record.ProcessId, out var current) &&
                    current.CreateTime == record.CreateTime;
        if (!alive || RestorePriority(record))
        {
            lock (_throttleGate)
            {
                _throttled.Remove(record.ProcessId);
            }

            _sessionGuard.UnregisterPriority(record.ProcessId);
        }
    }

    public void RestoreAllPriorities(string? reason = null)
    {
        List<ThrottleRecord> records;
        lock (_throttleGate)
        {
            records = _throttled.Values.ToList();
            _throttled.Clear();
            _lastFamilyTrim.Clear();
            _familyCpuAtThrottle.Clear();
        }

        var processes = _processes.Snapshot;
        var restored = 0;
        foreach (var record in records)
        {
            var alive = processes.Processes.TryGetValue(record.ProcessId, out var current) &&
                        current.CreateTime == record.CreateTime;
            if (alive && RestorePriority(record))
            {
                restored++;
            }

            _sessionGuard.UnregisterPriority(record.ProcessId);
        }

        if (restored > 0)
        {
            _log.Info($"Restored priority for {restored} process(es). {reason}");
        }
    }

    private static bool RestorePriority(ThrottleRecord record)
    {
        var handle = OpenForSet(record.ProcessId, NativeMethods.PROCESS_SET_INFORMATION);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var currentPriority = NativeMethods.GetPriorityClass(handle);
            if (currentPriority == record.ThrottledTo &&
                !NativeMethods.SetPriorityClass(handle, record.OriginalPriority))
            {
                return false;
            }

            ApplyEcoQos(handle, false);
            ApplyMemoryPriority(handle, NativeMethods.MEMORY_PRIORITY_NORMAL);
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

    // ------------------------------------------------------------------ helpers

    private static PressureLevel ComputePressure(TelemetrySnapshot telemetry)
    {
        if (telemetry.RamPercent >= 90)
        {
            return PressureLevel.Critical;
        }

        if (telemetry.RamPercent >= 80 || telemetry.CpuPercent >= 85)
        {
            return PressureLevel.High;
        }

        if (telemetry.RamPercent < 60 && telemetry.CpuPercent < 40)
        {
            return PressureLevel.Relaxed;
        }

        return PressureLevel.Normal;
    }

    private static bool IsOnBattery() =>
        NativeMethods.GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;

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

        var dx = capsule.CanvasX + capsule.CanvasWidth / 2 - (active.CanvasX + active.CanvasWidth / 2);
        var dy = capsule.CanvasY + capsule.CanvasHeight / 2 - (active.CanvasY + active.CanvasHeight / 2);
        return Math.Sqrt(dx * dx + dy * dy) / 6000d;
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
                (uint)Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>());
        }
        catch
        {
            // EcoQoS is optional; the priority class remains applied.
        }
    }

    private static void ApplyMemoryPriority(IntPtr processHandle, uint priority)
    {
        try
        {
            var info = new NativeMethods.MEMORY_PRIORITY_INFORMATION { MemoryPriority = priority };
            NativeMethods.SetProcessMemoryPriority(
                processHandle,
                NativeMethods.ProcessMemoryPriority,
                ref info,
                (uint)Marshal.SizeOf<NativeMethods.MEMORY_PRIORITY_INFORMATION>());
        }
        catch
        {
            // Optional enhancement.
        }
    }

    private AiStats BuildStatsLocked() => new(
        _pressure,
        _interval,
        _throttled.Values.Select(r => r.FamilyRootId).Distinct().Count(),
        _reclaimedMb,
        _familyCpuAtThrottle.Values.Sum(),
        _actions,
        _usage.LearnedApps,
        _usage.Accuracy,
        IsOnBattery());

    private void PublishStats()
    {
        AiStats stats;
        lock (_throttleGate)
        {
            stats = BuildStatsLocked();
        }

        StatsChanged?.Invoke(stats);
    }

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
            var processes = _processes.Snapshot;
            var roots = _windows.Snapshot
                .Where(w => string.Equals(w.CapsuleId, capsuleId, StringComparison.Ordinal))
                .Select(w => processes.FamilyOf(w.ProcessId)?.RootProcessId ?? w.ProcessId)
                .ToHashSet();
            RestoreFamilies(roots, "page activated");
            PublishStats();
        });
    }

    private void Report(OptimizationEvent optimizationEvent) => ActivityReported?.Invoke(optimizationEvent);

    public async ValueTask DisposeAsync()
    {
        _isDisposed = true;
        _windows.WindowsChanged -= OnWindowsChanged;
        _usage.RegretDetected -= OnRegretDetected;

        var cts = _cts;
        var loop = _loopTask;
        _cts = null;
        _loopTask = null;

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
        _usage.Save();
        _evaluationGate.Dispose();
        cts?.Dispose();
        _log.Info("Hardware AI optimizer stopped.");
    }

    /// <summary>Thresholds derived from profile, live pressure and power source.</summary>
    private sealed record Tuning(
        double ThrottleThreshold,
        double TrimThreshold,
        TimeSpan MinIdleThrottle,
        TimeSpan MinIdleTrim,
        bool TrimAllowed,
        TimeSpan TrimCooldown,
        int MaxActions,
        TimeSpan Interval,
        double BackgroundHogCpu)
    {
        public static Tuning For(AiProfile profile, PressureLevel pressure, bool onBattery, bool manual)
        {
            var (throttle, trim, idleThrottle, idleTrim, actions) = profile switch
            {
                AiProfile.Conservative => (0.62, 0.80, 120.0, 300.0, 1),
                AiProfile.Aggressive => (0.42, 0.60, 30.0, 60.0, 5),
                _ => (0.52, 0.70, 60.0, 120.0, 3)
            };

            var pressureShift = pressure switch
            {
                PressureLevel.Critical => 0.10,
                PressureLevel.High => 0.05,
                _ => 0.0
            };
            throttle -= pressureShift;
            trim -= pressureShift;
            if (onBattery)
            {
                // EcoQoS saves battery, so be a little more eager on DC power.
                throttle -= 0.05;
            }

            if (pressure == PressureLevel.Critical)
            {
                idleThrottle /= 2;
                idleTrim /= 3;
            }
            else if (pressure == PressureLevel.High)
            {
                idleTrim /= 2;
            }

            actions += pressure switch
            {
                PressureLevel.Relaxed => -1,
                PressureLevel.High => 2,
                PressureLevel.Critical => 5,
                _ => 0
            };

            if (manual)
            {
                actions += 3;
            }

            var interval = pressure switch
            {
                PressureLevel.Relaxed => TimeSpan.FromSeconds(15),
                PressureLevel.High => TimeSpan.FromSeconds(4),
                PressureLevel.Critical => TimeSpan.FromSeconds(3),
                _ => TimeSpan.FromSeconds(8)
            };

            // Emptying working sets while RAM is plentiful only causes page faults later.
            var trimAllowed = manual || pressure >= PressureLevel.Normal || profile == AiProfile.Aggressive;

            return new Tuning(
                throttle,
                trim,
                TimeSpan.FromSeconds(idleThrottle),
                TimeSpan.FromSeconds(idleTrim),
                trimAllowed,
                pressure == PressureLevel.Critical ? TimeSpan.FromMinutes(5) : TimeSpan.FromMinutes(15),
                Math.Max(1, actions),
                interval,
                profile == AiProfile.Aggressive ? 5 : profile == AiProfile.Conservative ? 15 : 8);
        }
    }

    private sealed record Candidate(
        AppFamily Family,
        TimeSpan Idle,
        double Score,
        double Priority,
        bool CanThrottle,
        bool CanTrim,
        bool AllMinimized);

    private sealed record ThrottleRecord(
        int ProcessId,
        string ProcessName,
        long CreateTime,
        int FamilyRootId,
        string FamilyName,
        uint OriginalPriority,
        uint ThrottledTo);
}
