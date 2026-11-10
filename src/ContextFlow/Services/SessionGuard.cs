using System.Diagnostics;
using System.Text.Json;
using ContextFlow.Infrastructure;
using ContextFlow.Interop;
using ContextFlow.Models;

namespace ContextFlow.Services;

/// <summary>
/// Crash-consistency journal for externally visible optimizer state.
///
/// Priority changes and DWM cloaking are journaled before/while they are in
/// effect. If ContextFlow is terminated without running its shutdown path, the
/// next launch validates each record and restores it before normal tracking
/// begins. This protects user windows from remaining hidden or throttled.
/// </summary>
public sealed class SessionGuard
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly AppPaths _paths;
    private readonly AppLog _log;
    private readonly SessionGuardState _state;

    public SessionGuard(AppPaths paths, AppLog log)
    {
        _paths = paths;
        _log = log;
        _state = Load();
    }

    public void RecoverOrphanedState()
    {
        int priorities;
        int windows;

        lock (_gate)
        {
            var priorityRecords = _state.Priorities.ToList();
            var isolatedRecords = _state.CloakedWindows.ToList();
            priorities = RecoverPriorities(priorityRecords);
            windows = RecoverCloakedWindows(isolatedRecords);
            Persist();
        }

        if (priorities > 0 || windows > 0)
        {
            _log.Info($"Crash recovery restored {priorities} process priority record(s) and {windows} isolated window(s).");
        }
    }

    public void RegisterPriority(int processId, string processName, DateTime? startTimeUtc, uint originalPriority, uint throttledTo)
    {
        lock (_gate)
        {
            _state.Priorities.RemoveAll(p => p.ProcessId == processId);
            _state.Priorities.Add(new PriorityGuardRecord
            {
                ProcessId = processId,
                ProcessName = processName,
                StartTimeUtcTicks = startTimeUtc?.ToUniversalTime().Ticks ?? 0,
                OriginalPriority = originalPriority,
                ThrottledTo = throttledTo
            });
            Persist();
        }
    }

    public void UnregisterPriority(int processId)
    {
        lock (_gate)
        {
            if (_state.Priorities.RemoveAll(p => p.ProcessId == processId) > 0)
            {
                Persist();
            }
        }
    }

    public void RegisterIsolatedWindow(IntPtr hwnd, int processId, WindowIsolationMode mode)
    {
        lock (_gate)
        {
            _state.CloakedWindows.RemoveAll(w => new IntPtr(w.Hwnd) == hwnd);
            _state.CloakedWindows.Add(new CloakedWindowGuardRecord
            {
                Hwnd = hwnd.ToInt64(),
                ProcessId = processId,
                Mode = mode == WindowIsolationMode.DwmCloak ? "cloak" : "minimize"
            });
            Persist();
        }
    }

    public void UnregisterIsolatedWindow(IntPtr hwnd)
    {
        lock (_gate)
        {
            if (_state.CloakedWindows.RemoveAll(w => new IntPtr(w.Hwnd) == hwnd) > 0)
            {
                Persist();
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _state.Priorities.Clear();
            _state.CloakedWindows.Clear();
            DeleteJournal();
        }
    }

    private int RecoverPriorities(IReadOnlyList<PriorityGuardRecord> records)
    {
        var restored = 0;
        var remaining = new List<PriorityGuardRecord>();

        foreach (var entry in records)
        {
            var recover = true;
            IntPtr handle = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_SET_INFORMATION,
                false,
                (uint)entry.ProcessId);
            if (handle == IntPtr.Zero)
            {
                handle = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION | NativeMethods.PROCESS_SET_INFORMATION,
                    false,
                    (uint)entry.ProcessId);
            }

            if (handle == IntPtr.Zero)
            {
                if (IsSameProcess(entry))
                {
                    remaining.Add(entry);
                }

                continue;
            }

            try
            {
                if (!IsSameProcess(entry))
                {
                    continue;
                }

                var current = NativeMethods.GetPriorityClass(handle);
                if (current == entry.ThrottledTo)
                {
                    if (NativeMethods.SetPriorityClass(handle, entry.OriginalPriority))
                    {
                        restored++;
                    }
                    else
                    {
                        recover = false;
                    }
                }
                else if (current != entry.OriginalPriority)
                {
                    // The user (or another tool) changed priority while the app
                    // was down. Respect that state instead of overwriting it.
                    recover = true;
                }

                var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
                {
                    Version = NativeMethods.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = 0
                };
                NativeMethods.SetProcessInformation(
                    handle,
                    NativeMethods.ProcessPowerThrottling,
                    ref state,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.PROCESS_POWER_THROTTLING_STATE>());

                // AI v2 also lowers memory priority while an app is throttled.
                var memory = new NativeMethods.MEMORY_PRIORITY_INFORMATION
                {
                    MemoryPriority = NativeMethods.MEMORY_PRIORITY_NORMAL
                };
                NativeMethods.SetProcessMemoryPriority(
                    handle,
                    NativeMethods.ProcessMemoryPriority,
                    ref memory,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORY_PRIORITY_INFORMATION>());
            }
            catch (Exception ex)
            {
                recover = false;
                _log.Warn($"Could not recover process priority for PID {entry.ProcessId}: {ex.Message}");
            }
            finally
            {
                NativeMethods.CloseHandle(handle);
            }

            if (!recover)
            {
                remaining.Add(entry);
            }
        }

        _state.Priorities = remaining;
        return restored;
    }

    private int RecoverCloakedWindows(IReadOnlyList<CloakedWindowGuardRecord> records)
    {
        var restored = 0;
        var remaining = new List<CloakedWindowGuardRecord>();

        foreach (var entry in records)
        {
            try
            {
                var hwnd = new IntPtr(entry.Hwnd);
                if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
                {
                    continue;
                }

                NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
                if (unchecked((int)processId) != entry.ProcessId)
                {
                    continue;
                }

                if (string.Equals(entry.Mode, "minimize", StringComparison.OrdinalIgnoreCase))
                {
                    if (NativeMethods.IsIconic(hwnd))
                    {
                        NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
                        restored++;
                    }

                    continue;
                }

                var value = 0;
                if (NativeMethods.DwmSetWindowAttribute(
                        hwnd,
                        NativeMethods.DWMWA_CLOAK,
                        ref value,
                        sizeof(int)) == 0)
                {
                    restored++;
                }
                else
                {
                    remaining.Add(entry);
                }
            }
            catch (Exception ex)
            {
                remaining.Add(entry);
                _log.Warn($"Could not recover isolated window 0x{entry.Hwnd:X}: {ex.Message}");
            }
        }

        _state.CloakedWindows = remaining;
        return restored;
    }

    private static bool IsSameProcess(PriorityGuardRecord entry)
    {
        try
        {
            using var process = Process.GetProcessById(entry.ProcessId);
            if (!string.Equals(process.ProcessName, entry.ProcessName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (entry.StartTimeUtcTicks > 0)
            {
                var recorded = new DateTime(entry.StartTimeUtcTicks, DateTimeKind.Utc);
                if (Math.Abs((process.StartTime.ToUniversalTime() - recorded).TotalSeconds) > 1)
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

    private SessionGuardState Load()
    {
        try
        {
            if (!File.Exists(_paths.SessionGuardFile))
            {
                return new SessionGuardState();
            }

            var json = File.ReadAllText(_paths.SessionGuardFile);
            return JsonSerializer.Deserialize<SessionGuardState>(json, JsonOptions) ?? new SessionGuardState();
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not read session guard journal: {ex.Message}");
            return new SessionGuardState();
        }
    }

    private void Persist()
    {
        try
        {
            if (_state.Priorities.Count == 0 && _state.CloakedWindows.Count == 0)
            {
                DeleteJournal();
                return;
            }

            _paths.EnsureCreated();
            var temp = _paths.SessionGuardFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_state, JsonOptions));
            File.Move(temp, _paths.SessionGuardFile, overwrite: true);
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not persist session guard journal: {ex.Message}");
        }
    }

    private void DeleteJournal()
    {
        try
        {
            if (File.Exists(_paths.SessionGuardFile))
            {
                File.Delete(_paths.SessionGuardFile);
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Could not remove session guard journal: {ex.Message}");
        }
    }

    private sealed class SessionGuardState
    {
        public List<PriorityGuardRecord> Priorities { get; set; } = new();
        public List<CloakedWindowGuardRecord> CloakedWindows { get; set; } = new();
    }

    private sealed class PriorityGuardRecord
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public long StartTimeUtcTicks { get; set; }
        public uint OriginalPriority { get; set; }
        public uint ThrottledTo { get; set; }
    }

    private sealed class CloakedWindowGuardRecord
    {
        public long Hwnd { get; set; }
        public int ProcessId { get; set; }
        public string Mode { get; set; } = "cloak";
    }
}

public enum WindowIsolationMode
{
    DwmCloak,
    Minimize
}
