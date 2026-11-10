using System.Runtime.InteropServices;
using ContextFlow.Infrastructure;
using ContextFlow.Interop;
using ContextFlow.Models;

namespace ContextFlow.Services;

/// <summary>
/// Samples the whole process table every two seconds with a single
/// NtQuerySystemInformation call (no per-process handles), computes smoothed
/// per-process CPU, and groups processes into application families by walking
/// the parent chain up to the first shell/system boundary.
/// </summary>
public sealed class ProcessMonitor : IAsyncDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

    // Parents that mark the top of an application tree. A process whose parent
    // is one of these is the root of its own family.
    private static readonly HashSet<string> ShellBoundaries = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services",
        "svchost", "explorer", "sihost", "userinit", "taskhostw", "RuntimeBroker", "dllhost",
        "ApplicationFrameHost", "ShellExperienceHost", "StartMenuExperienceHost", "MemCompression",
        "lsass", "fontdrvhost", "dwm"
    };

    private readonly AppLog _log;
    private readonly AudioActivityMonitor _audio;
    private readonly object _gate = new();
    private readonly object _sampleGate = new();
    private readonly Dictionary<int, CpuState> _cpu = new();
    private readonly Dictionary<int, double> _familySustained = new();
    private IntPtr _buffer;
    private int _bufferSize = 2 * 1024 * 1024;
    private long _lastSampleTicks;
    private ProcessSnapshot _snapshot = ProcessSnapshot.Empty;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ProcessMonitor(AppLog log)
    {
        _log = log;
        _audio = new AudioActivityMonitor(log);
    }

    public event Action<ProcessSnapshot>? SnapshotUpdated;

    public ProcessSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public AudioActivityMonitor Audio => _audio;

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        _log.Info("Process monitor started (system process table + audio sessions).");
    }

    private async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(SampleInterval);
        do
        {
            try
            {
                var snapshot = Sample();
                lock (_gate)
                {
                    _snapshot = snapshot;
                }

                _audio.Poll();
                SnapshotUpdated?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                _log.Error("Process table sample failed.", ex);
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        while (!token.IsCancellationRequested);
    }

    /// <summary>Forces a synchronous sample. Used before a manual optimization pass.</summary>
    public ProcessSnapshot SampleNow()
    {
        var snapshot = Sample();
        lock (_gate)
        {
            _snapshot = snapshot;
        }

        return snapshot;
    }

    private ProcessSnapshot Sample()
    {
        // The native buffer is shared; the loop and SampleNow must not overlap.
        lock (_sampleGate)
        {
            return SampleCore();
        }
    }

    private ProcessSnapshot SampleCore()
    {
        var raw = ReadProcessTable();
        var nowTicks = DateTime.UtcNow.Ticks;
        var processorCount = Environment.ProcessorCount;
        var stats = new Dictionary<int, ProcessStat>(raw.Count);

        lock (_gate)
        {
            var elapsed = _lastSampleTicks == 0 ? 0 : nowTicks - _lastSampleTicks;
            _lastSampleTicks = nowTicks;

            var seen = new HashSet<int>();
            foreach (var p in raw)
            {
                seen.Add(p.ProcessId);
                var cpuTime = p.UserTime + p.KernelTime;
                double cpu = 0;
                if (_cpu.TryGetValue(p.ProcessId, out var previous) && previous.CreateTime == p.CreateTime && elapsed > 0)
                {
                    var instant = Math.Max(0, cpuTime - previous.CpuTime) * 100d / (elapsed * (double)processorCount);
                    cpu = previous.Smoothed * 0.45 + Math.Min(instant, 100) * 0.55;
                }

                _cpu[p.ProcessId] = new CpuState(p.CreateTime, cpuTime, cpu);
                stats[p.ProcessId] = new ProcessStat(
                    p.ProcessId,
                    p.ParentProcessId,
                    p.Name,
                    p.CreateTime,
                    cpu,
                    p.WorkingSet,
                    p.PrivateWorkingSet,
                    p.PrivateBytes,
                    p.Threads,
                    p.SessionId,
                    p.ProcessId);
            }

            foreach (var gone in _cpu.Keys.Where(pid => !seen.Contains(pid)).ToList())
            {
                _cpu.Remove(gone);
            }
        }

        // Resolve family roots with memoization.
        var roots = new Dictionary<int, int>(stats.Count);
        foreach (var pid in stats.Keys)
        {
            ResolveRoot(pid, stats, roots);
        }

        var families = new Dictionary<int, AppFamily>();
        var grouped = stats.Values
            .Select(s => s with { FamilyRootId = roots[s.ProcessId] })
            .ToList();
        stats = grouped.ToDictionary(s => s.ProcessId);

        lock (_gate)
        {
            var liveRoots = new HashSet<int>();
            foreach (var group in grouped.GroupBy(s => s.FamilyRootId))
            {
                if (!stats.TryGetValue(group.Key, out var root))
                {
                    continue;
                }

                liveRoots.Add(group.Key);
                var cpu = group.Sum(s => s.CpuPercent);
                var sustained = _familySustained.TryGetValue(group.Key, out var prev)
                    ? prev * 0.85 + cpu * 0.15
                    : cpu;
                _familySustained[group.Key] = sustained;

                families[group.Key] = new AppFamily(
                    group.Key,
                    root.Name,
                    group.Select(s => s.ProcessId).ToList(),
                    cpu,
                    sustained,
                    group.Sum(s => s.WorkingSetBytes),
                    group.Sum(s => s.PrivateWorkingSetBytes));
            }

            foreach (var stale in _familySustained.Keys.Where(k => !liveRoots.Contains(k)).ToList())
            {
                _familySustained.Remove(stale);
            }
        }

        return new ProcessSnapshot(stats, families, DateTime.UtcNow);
    }

    private static int ResolveRoot(int pid, Dictionary<int, ProcessStat> stats, Dictionary<int, int> roots)
    {
        if (roots.TryGetValue(pid, out var known))
        {
            return known;
        }

        var chain = new List<int>();
        var current = pid;
        var root = pid;
        for (var depth = 0; depth < 32; depth++)
        {
            if (roots.TryGetValue(current, out var cached))
            {
                root = cached;
                break;
            }

            chain.Add(current);
            root = current;
            if (!stats.TryGetValue(current, out var stat) ||
                stat.ProcessId <= 4 ||
                ShellBoundaries.Contains(stat.Name))
            {
                break;
            }

            // A parent created after the child means the parent PID was reused.
            if (!stats.TryGetValue(stat.ParentProcessId, out var parent) ||
                parent.ProcessId == stat.ProcessId ||
                parent.CreateTime > stat.CreateTime ||
                ShellBoundaries.Contains(parent.Name) ||
                parent.ProcessId <= 4)
            {
                break;
            }

            current = parent.ProcessId;
        }

        foreach (var id in chain)
        {
            roots[id] = root;
        }

        return root;
    }

    private List<RawProcess> ReadProcessTable()
    {
        var result = new List<RawProcess>(400);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (_buffer == IntPtr.Zero)
            {
                _buffer = Marshal.AllocHGlobal(_bufferSize);
            }

            var status = NativeMethods.NtQuerySystemInformation(
                NativeMethods.SystemProcessInformation,
                _buffer,
                _bufferSize,
                out var needed);

            if (status == NativeMethods.STATUS_INFO_LENGTH_MISMATCH)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
                _bufferSize = Math.Max(needed + 256 * 1024, _bufferSize * 2);
                continue;
            }

            if (status != 0)
            {
                throw new InvalidOperationException($"NtQuerySystemInformation failed: 0x{status:X8}");
            }

            var offset = 0L;
            while (true)
            {
                var entryPtr = IntPtr.Add(_buffer, (int)offset);
                var info = Marshal.PtrToStructure<NativeMethods.SYSTEM_PROCESS_INFORMATION>(entryPtr);
                var pid = (int)info.UniqueProcessId.ToInt64();
                var name = pid switch
                {
                    0 => "Idle",
                    _ when info.ImageName.Buffer == IntPtr.Zero => "System",
                    _ => Marshal.PtrToStringUni(info.ImageName.Buffer, info.ImageName.Length / 2)
                };

                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    name = name[..^4];
                }

                if (pid != 0)
                {
                    result.Add(new RawProcess(
                        pid,
                        (int)info.InheritedFromUniqueProcessId.ToInt64(),
                        name,
                        info.CreateTime,
                        info.UserTime,
                        info.KernelTime,
                        (long)info.WorkingSetSize.ToUInt64(),
                        info.WorkingSetPrivateSize,
                        (long)info.PrivatePageCount.ToUInt64(),
                        (int)info.NumberOfThreads,
                        (int)info.SessionId));
                }

                if (info.NextEntryOffset == 0)
                {
                    break;
                }

                offset += info.NextEntryOffset;
            }

            return result;
        }

        throw new InvalidOperationException("Process table kept growing while it was being read.");
    }

    public async ValueTask DisposeAsync()
    {
        var cts = _cts;
        _cts = null;
        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            if (_loop is not null)
            {
                try
                {
                    await _loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            cts.Dispose();
        }

        _loop = null;
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }

        _log.Info("Process monitor stopped.");
    }

    private readonly record struct CpuState(long CreateTime, long CpuTime, double Smoothed);

    private readonly record struct RawProcess(
        int ProcessId,
        int ParentProcessId,
        string Name,
        long CreateTime,
        long UserTime,
        long KernelTime,
        long WorkingSet,
        long PrivateWorkingSet,
        long PrivateBytes,
        int Threads,
        int SessionId);
}
