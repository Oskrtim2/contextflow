using System.Text;
using ContextFlow.Infrastructure;
using ContextFlow.Interop;
using ContextFlow.Models;

namespace ContextFlow.Services;

public sealed class WindowsChangedEventArgs : EventArgs
{
    public WindowsChangedEventArgs(IReadOnlyList<ManagedWindow> windows) => Windows = windows;

    public IReadOnlyList<ManagedWindow> Windows { get; }
}

public readonly record struct CapsuleMoveResult(int Moved, int Failed);

/// <summary>
/// Low-level Win32 window service. Discovers top-level application windows,
/// tracks physical geometry, owns capsule assignments, routes windows between
/// physical displays, and applies opt-in DWM cloaking for inactive capsules.
/// </summary>
public sealed class WindowManager : IAsyncDisposable
{
    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "DV2ControlHost",
        "TaskListThumbnailWnd",
        "Windows.UI.Core.CoreWindow",
        "ApplicationManager_DesktopShellWindow",
        "MultitaskingViewFrame",
        "ForegroundStaging",
        "XamlExplorerHostIslandWindow",
        "tooltips_class32",
        "#32768"
    };

    private readonly object _gate = new();
    private readonly object _scanGate = new();
    private readonly DisplayService _displays;
    private readonly IconService _icons;
    private readonly SessionGuard _sessionGuard;
    private readonly AppLog _log;
    private readonly int _ownProcessId = Environment.ProcessId;

    private readonly Dictionary<IntPtr, TrackedWindow> _tracked = new();
    private readonly Dictionary<IntPtr, string> _assignments = new();
    private readonly Dictionary<string, string> _processAffinity = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<IntPtr> _appCloaked = new();
    private readonly HashSet<IntPtr> _appMinimized = new();
    private readonly HashSet<string> _capsuleIds = new(StringComparer.Ordinal);
    private bool _dwmCloakDenied;

    private CancellationTokenSource? _cts;
    private Task? _trackingTask;
    private string _activeCapsuleId = string.Empty;
    private bool _isolationEnabled;
    private bool _autoMaximize;

    // Windows ContextFlow maximized itself, so they can be put back on exit.
    private readonly HashSet<IntPtr> _autoMaximized = new();

    public WindowManager(DisplayService displays, IconService icons, SessionGuard sessionGuard, AppLog log)
    {
        _displays = displays;
        _icons = icons;
        _sessionGuard = sessionGuard;
        _log = log;
    }

    public event EventHandler<WindowsChangedEventArgs>? WindowsChanged;

    public string ActiveCapsuleId
    {
        get
        {
            lock (_gate)
            {
                return _activeCapsuleId;
            }
        }
    }

    public IReadOnlyList<ManagedWindow> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return BuildSnapshotLocked();
            }
        }
    }

    public void ConfigureCapsules(IEnumerable<CapsuleDefinition> capsules, string activeCapsuleId)
    {
        IReadOnlyList<ManagedWindow>? changed = null;
        lock (_gate)
        {
            _capsuleIds.Clear();
            foreach (var capsule in capsules)
            {
                _capsuleIds.Add(capsule.Id);
            }

            _activeCapsuleId = _capsuleIds.Contains(activeCapsuleId)
                ? activeCapsuleId
                : _capsuleIds.FirstOrDefault() ?? string.Empty;

            ReconcileAssignmentsLocked();
            ApplyIsolationLocked();
            changed = BuildSnapshotLocked();
        }

        RaiseWindowsChanged(changed);
    }

    public void SetActiveCapsule(string capsuleId)
    {
        IReadOnlyList<ManagedWindow>? changed = null;
        lock (_gate)
        {
            if (!_capsuleIds.Contains(capsuleId) || string.Equals(_activeCapsuleId, capsuleId, StringComparison.Ordinal))
            {
                return;
            }

            _activeCapsuleId = capsuleId;
            ApplyIsolationLocked();
            changed = BuildSnapshotLocked();
        }

        RaiseWindowsChanged(changed);
    }

    public void SetCapsuleIsolation(bool enabled)
    {
        IReadOnlyList<ManagedWindow>? changed = null;
        lock (_gate)
        {
            _isolationEnabled = enabled;
            ApplyIsolationLocked();
            changed = BuildSnapshotLocked();
        }

        RaiseWindowsChanged(changed);
    }

    /// <summary>
    /// Full-screen workspace mode: every resizable app window is maximized when
    /// it appears and whenever it gains focus, so scrolling through windows feels
    /// like moving across full-screen workspaces. A window the user restores by
    /// hand is left alone until focus leaves it and comes back.
    /// </summary>
    public bool AutoMaximize
    {
        get
        {
            lock (_gate)
            {
                return _autoMaximize;
            }
        }
    }

    public void SetAutoMaximize(bool enabled)
    {
        List<IntPtr> toMaximize;
        lock (_gate)
        {
            if (_autoMaximize == enabled)
            {
                return;
            }

            _autoMaximize = enabled;
            toMaximize = enabled
                ? _tracked.Values
                    .Where(t => !t.Window.IsMinimized && !t.Window.IsCloaked)
                    .Select(t => t.Window.Handle)
                    .ToList()
                : new List<IntPtr>();
        }

        if (enabled)
        {
            foreach (var hwnd in toMaximize)
            {
                TryAutoMaximize(hwnd);
            }

            _log.Info($"Full-screen workspace mode enabled for {toMaximize.Count} window(s).");
        }
        else
        {
            RestoreAutoMaximizedWindows();
        }
    }

    private void TryAutoMaximize(IntPtr hwnd)
    {
        if (!NativeMethods.IsWindow(hwnd) || NativeMethods.IsIconic(hwnd) || NativeMethods.IsZoomed(hwnd))
        {
            return;
        }

        // Dialogs and fixed-size tool windows cannot (and should not) be maximized.
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        if ((style & NativeMethods.WS_MAXIMIZEBOX) == 0 || (style & NativeMethods.WS_THICKFRAME) == 0)
        {
            return;
        }

        if (NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_MAXIMIZE))
        {
            lock (_gate)
            {
                _autoMaximized.Add(hwnd);
            }
        }
    }

    /// <summary>Restores windows that ContextFlow maximized and that are still maximized.</summary>
    public void RestoreAutoMaximizedWindows()
    {
        List<IntPtr> handles;
        lock (_gate)
        {
            handles = _autoMaximized.ToList();
            _autoMaximized.Clear();
        }

        var restored = 0;
        foreach (var hwnd in handles)
        {
            if (NativeMethods.IsWindow(hwnd) && NativeMethods.IsZoomed(hwnd) &&
                NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE))
            {
                restored++;
            }
        }

        if (restored > 0)
        {
            _log.Info($"Restored {restored} window(s) maximized by full-screen workspace mode.");
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
            _trackingTask = Task.Run(() => TrackLoopAsync(_cts.Token));
        }

        _log.Info("Window tracking started.");
    }

    private async Task TrackLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000));
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                CaptureNow();
            }
            catch (Exception ex)
            {
                _log.Error("Window tracking pass failed.", ex);
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

    /// <summary>Performs one synchronous discovery pass and emits a snapshot.</summary>
    public void CaptureNow()
    {
        lock (_scanGate)
        {
            IReadOnlyList<ManagedWindow> snapshot;
            var maximizeQueue = new List<IntPtr>();

            lock (_gate)
            {
                var foregroundRoot = GetRootWindow(NativeMethods.GetForegroundWindow());
                var currentHandles = new List<IntPtr>();
                var seen = new HashSet<IntPtr>();
                var zOrder = 0;

                NativeMethods.EnumWindows((hwnd, _) =>
                {
                    if (!seen.Add(hwnd) || !IsManageableWindow(hwnd, foregroundRoot))
                    {
                        return true;
                    }

                    currentHandles.Add(hwnd);
                    return true;
                }, IntPtr.Zero);

                foreach (var hwnd in currentHandles)
                {
                    var foreground = hwnd == foregroundRoot;
                    var previous = _tracked.TryGetValue(hwnd, out var tracked) ? tracked : null;

                    NativeMethods.GetWindowThreadProcessId(hwnd, out var processIdRaw);
                    var processId = unchecked((int)processIdRaw);
                    var executablePath = previous?.Window.ExecutablePath ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(executablePath))
                    {
                        TryGetProcessPath(processId, out executablePath);
                    }

                    var processName = previous?.Window.ProcessName;
                    if (string.IsNullOrWhiteSpace(processName))
                    {
                        processName = string.IsNullOrWhiteSpace(executablePath)
                            ? $"process-{processId}"
                            : Path.GetFileNameWithoutExtension(executablePath);
                    }

                    var title = GetWindowTitle(hwnd);
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        continue;
                    }

                    var bounds = GetWindowBounds(hwnd, previous?.Window.Bounds);
                    if (bounds.IsEmpty)
                    {
                        continue;
                    }

                    var cloaked = AreWeCloaking(hwnd) || IsDwmCloaked(hwnd);
                    var capsuleId = ResolveCapsuleLocked(hwnd, executablePath, previous?.Window.CapsuleId);
                    var icon = previous?.Window.Icon ?? _icons.GetWindowIcon(hwnd, executablePath);
                    var firstSeen = previous?.Window.FirstSeenUtc ?? DateTime.UtcNow;
                    var lastForeground = foreground
                        ? DateTime.UtcNow
                        : previous?.Window.LastForegroundUtc ?? DateTime.MinValue;

                    var window = new ManagedWindow(
                        hwnd,
                        processId,
                        processName ?? "unknown",
                        executablePath,
                        title,
                        bounds,
                        NativeMethods.IsIconic(hwnd),
                        foreground,
                        cloaked,
                        capsuleId,
                        _displays.GetDeviceNameForWindow(hwnd),
                        icon,
                        firstSeen,
                        lastForeground);

                    _assignments[hwnd] = capsuleId;
                    if (!string.IsNullOrWhiteSpace(executablePath))
                    {
                        _processAffinity[executablePath] = capsuleId;
                    }

                    _tracked[hwnd] = new TrackedWindow
                    {
                        ZOrder = zOrder++,
                        Window = window
                    };

                    // Full-screen mode: new windows, and windows that just gained focus.
                    var gainedFocus = foreground && previous is not null && !previous.Window.IsForeground;
                    if (_autoMaximize && !window.IsMinimized && !cloaked && (previous is null || gainedFocus))
                    {
                        maximizeQueue.Add(hwnd);
                    }
                }

                var stale = _tracked.Keys.Where(h => !currentHandles.Contains(h)).ToList();
                foreach (var hwnd in stale)
                {
                    _tracked.Remove(hwnd);
                    _assignments.Remove(hwnd);
                    _autoMaximized.Remove(hwnd);
                    if (_appCloaked.Remove(hwnd) || _appMinimized.Remove(hwnd))
                    {
                        _sessionGuard.UnregisterIsolatedWindow(hwnd);
                    }
                }

                ReconcileAssignmentsLocked();
                if (_isolationEnabled)
                {
                    ApplyIsolationLocked();
                }

                snapshot = BuildSnapshotLocked();
            }

            foreach (var hwnd in maximizeQueue)
            {
                TryAutoMaximize(hwnd);
            }

            WindowsChanged?.Invoke(this, new WindowsChangedEventArgs(snapshot));
        }
    }

    private bool IsManageableWindow(IntPtr hwnd, IntPtr foregroundRoot)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd) || !NativeMethods.IsWindowVisible(hwnd))
        {
            return false;
        }

        if (NativeMethods.GetWindowTextLength(hwnd) <= 0)
        {
            return false;
        }

        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        if ((style & NativeMethods.WS_CHILD) != 0)
        {
            return false;
        }

        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0 && (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
        {
            return false;
        }

        var owner = NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER);
        if (owner != IntPtr.Zero && owner != hwnd && (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0)
        {
            return false;
        }

        var className = GetWindowClass(hwnd);
        if (ExcludedClasses.Contains(className))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processIdRaw);
        if (unchecked((int)processIdRaw) == _ownProcessId)
        {
            return false;
        }

        // Windows on other virtual desktops are cloaked by the shell. Never
        // surface them in ContextFlow or interfere with their DWM state.
        if (IsDwmCloaked(hwnd) && !AreWeCloaking(hwnd))
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        return rect.Width > 0 && rect.Height > 0 && (hwnd == foregroundRoot || NativeMethods.IsWindowVisible(hwnd));
    }

    private string ResolveCapsuleLocked(IntPtr hwnd, string executablePath, string? previousCapsuleId)
    {
        string? candidate = null;
        if (_assignments.TryGetValue(hwnd, out var assigned))
        {
            candidate = assigned;
        }
        else if (!string.IsNullOrWhiteSpace(executablePath) &&
                 _processAffinity.TryGetValue(executablePath, out var processAssigned))
        {
            candidate = processAssigned;
        }

        candidate ??= previousCapsuleId;

        if (!string.IsNullOrWhiteSpace(candidate) && _capsuleIds.Contains(candidate))
        {
            return candidate;
        }

        if (!string.IsNullOrWhiteSpace(_activeCapsuleId))
        {
            return _activeCapsuleId;
        }

        return _capsuleIds.FirstOrDefault() ?? string.Empty;
    }

    private void ReconcileAssignmentsLocked()
    {
        if (_capsuleIds.Count == 0)
        {
            return;
        }

        var fallback = !string.IsNullOrWhiteSpace(_activeCapsuleId) &&
                       _capsuleIds.Contains(_activeCapsuleId)
            ? _activeCapsuleId
            : _capsuleIds.First();

        foreach (var hwnd in _assignments.Keys.ToList())
        {
            if (!_capsuleIds.Contains(_assignments[hwnd]))
            {
                _assignments[hwnd] = fallback;
            }
        }

        foreach (var key in _processAffinity.Keys.ToList())
        {
            if (!_capsuleIds.Contains(_processAffinity[key]))
            {
                _processAffinity[key] = fallback;
            }
        }

        foreach (var tracked in _tracked.Values)
        {
            if (!_capsuleIds.Contains(tracked.Window.CapsuleId))
            {
                tracked.Window = tracked.Window with { CapsuleId = fallback };
            }
        }
    }

    private IReadOnlyList<ManagedWindow> BuildSnapshotLocked() =>
        _tracked.Values
            .OrderBy(t => t.ZOrder)
            .Select(t => t.Window)
            .ToList();

    private void ApplyIsolationLocked()
    {
        var foreground = GetRootWindow(NativeMethods.GetForegroundWindow());
        foreach (var tracked in _tracked.Values)
        {
            var window = tracked.Window;
            var isIsolatedByUs = _appCloaked.Contains(window.Handle) || _appMinimized.Contains(window.Handle);
            var shouldBeIsolated = _isolationEnabled &&
                                   !string.IsNullOrWhiteSpace(_activeCapsuleId) &&
                                   !string.Equals(window.CapsuleId, _activeCapsuleId, StringComparison.Ordinal) &&
                                   window.Handle != foreground;

            if (!shouldBeIsolated)
            {
                if (_appCloaked.Contains(window.Handle) && SetDwmCloak(window.Handle, false) == 0)
                {
                    _appCloaked.Remove(window.Handle);
                    _sessionGuard.UnregisterIsolatedWindow(window.Handle);
                    tracked.Window = window with { IsCloaked = false };
                }

                if (_appMinimized.Remove(window.Handle))
                {
                    if (NativeMethods.IsIconic(window.Handle))
                    {
                        NativeMethods.ShowWindowAsync(window.Handle, NativeMethods.SW_RESTORE);
                    }

                    _sessionGuard.UnregisterIsolatedWindow(window.Handle);
                }

                continue;
            }

            if (isIsolatedByUs || window.IsMinimized)
            {
                // Windows minimized by the user, or already isolated once, are
                // left alone. If a user restores an isolated window manually, we
                // honor that intent until the next capsule transition.
                continue;
            }

            if (!_dwmCloakDenied)
            {
                _sessionGuard.RegisterIsolatedWindow(window.Handle, window.ProcessId, WindowIsolationMode.DwmCloak);
                var cloakResult = SetDwmCloak(window.Handle, true);
                if (cloakResult == 0)
                {
                    _appCloaked.Add(window.Handle);
                    tracked.Window = window with { IsCloaked = true };
                    continue;
                }

                _sessionGuard.UnregisterIsolatedWindow(window.Handle);
                // DWM refuses cross-process cloaking for some windows/builds.
                // Fall back to a reversible minimize, which also removes the
                // window from the interactive composition surface.
                _dwmCloakDenied = true;
                _log.Info($"DWM cross-process cloaking is unavailable (0x{cloakResult:X8}); using minimize isolation fallback.");
            }

            _sessionGuard.RegisterIsolatedWindow(window.Handle, window.ProcessId, WindowIsolationMode.Minimize);
            if (NativeMethods.ShowWindowAsync(window.Handle, NativeMethods.SW_MINIMIZE))
            {
                _appMinimized.Add(window.Handle);
                tracked.Window = window with { IsMinimized = true };
            }
            else
            {
                _sessionGuard.UnregisterIsolatedWindow(window.Handle);
            }
        }
    }

    /// <summary>Restores every window isolated by ContextFlow and clears tracking.</summary>
    public void RestoreAllIsolatedWindows()
    {
        lock (_gate)
        {
            foreach (var hwnd in _appCloaked.ToList())
            {
                if (SetDwmCloak(hwnd, false) == 0)
                {
                    _appCloaked.Remove(hwnd);
                    _sessionGuard.UnregisterIsolatedWindow(hwnd);
                }
            }

            foreach (var hwnd in _appMinimized.ToList())
            {
                if (NativeMethods.IsIconic(hwnd))
                {
                    NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
                }

                _appMinimized.Remove(hwnd);
                _sessionGuard.UnregisterIsolatedWindow(hwnd);
            }

            _isolationEnabled = false;
        }
    }

    /// <summary>
    /// Assigns a window to another capsule and, optionally, routes it to that
    /// capsule's physical monitor while preserving its relative screen position.
    /// </summary>
    public void AssignWindowToCapsule(IntPtr hwnd, string capsuleId, DisplayInfo? targetDisplay)
    {
        var shouldMove = false;
        IReadOnlyList<ManagedWindow> snapshot;
        lock (_gate)
        {
            if (!_capsuleIds.Contains(capsuleId) || !_tracked.TryGetValue(hwnd, out var tracked))
            {
                return;
            }

            _assignments[hwnd] = capsuleId;
            if (!string.IsNullOrWhiteSpace(tracked.Window.ExecutablePath))
            {
                _processAffinity[tracked.Window.ExecutablePath] = capsuleId;
            }

            tracked.Window = tracked.Window with { CapsuleId = capsuleId };
            shouldMove = targetDisplay is not null;

            if (_isolationEnabled)
            {
                ApplyIsolationLocked();
            }

            snapshot = BuildSnapshotLocked();
        }

        if (shouldMove && targetDisplay is not null)
        {
            MoveWindowToDisplay(hwnd, targetDisplay);
        }

        RaiseWindowsChanged(snapshot);
    }

    /// <summary>
    /// Tiles every window assigned to a page (capsule) inside the target
    /// monitor work area. This is the split-screen simulation: the page owns a
    /// single physical screen and divides it into Auto/Columns/Rows/Grid slots.
    /// </summary>
    public CapsuleMoveResult ArrangeCapsuleWindows(
        string capsuleId,
        DisplayInfo targetDisplay,
        PageLayout layout,
        bool focusFirst)
    {
        List<IntPtr> handles;
        lock (_gate)
        {
            handles = _tracked.Values
                .Where(t => string.Equals(t.Window.CapsuleId, capsuleId, StringComparison.Ordinal))
                .OrderBy(t => t.Window.FirstSeenUtc)
                .ThenBy(t => t.Window.Handle.ToInt64())
                .Select(t => t.Window.Handle)
                .ToList();
        }

        if (handles.Count == 0)
        {
            return new CapsuleMoveResult(0, 0);
        }

        if (AutoMaximize)
        {
            return StackMaximizedOnDisplay(handles, targetDisplay, focusFirst, capsuleId);
        }

        var slots = BuildPageSlots(handles.Count, layout, targetDisplay.WorkArea);
        var moved = 0;
        var failed = 0;

        for (var i = 0; i < handles.Count; i++)
        {
            var handle = handles[i];

            // A maximized or minimized window cannot be positioned reliably
            // until it is restored to its normal placement. SetWindowPlacement
            // is synchronous; ShowWindowAsync can race with SetWindowPos and
            // leave the window maximized on top of the tiled layout.
            if (NativeMethods.IsZoomed(handle) || NativeMethods.IsIconic(handle))
            {
                var slot = slots[i];
                var placement = new NativeMethods.WINDOWPLACEMENT
                {
                    length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>(),
                    showCmd = NativeMethods.SW_SHOWNORMAL,
                    rcNormalPosition = new NativeMethods.RECT
                    {
                        Left = slot.X,
                        Top = slot.Y,
                        Right = slot.Right,
                        Bottom = slot.Bottom
                    }
                };

                if (!NativeMethods.SetWindowPlacement(handle, ref placement))
                {
                    NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
                }
            }

            if (SetWindowBounds(handle, slots[i]))
            {
                moved++;
            }
            else
            {
                failed++;
            }
        }

        if (handles.Count == 1 && layout == PageLayout.Auto)
        {
            NativeMethods.ShowWindowAsync(handles[0], NativeMethods.SW_MAXIMIZE);
        }

        if (focusFirst && moved > 0)
        {
            FocusWindow(handles[0]);
        }

        _log.Info(
            $"Page '{capsuleId}' arranged {moved} window(s) on {targetDisplay.Label} " +
            $"using {layout} layout; {failed} failed.");

        return new CapsuleMoveResult(moved, failed);
    }

    /// <summary>
    /// Full-screen mode replacement for split tiling: every window of the page is
    /// moved to the target display and maximized there, like stacked workspaces.
    /// </summary>
    private CapsuleMoveResult StackMaximizedOnDisplay(
        IReadOnlyList<IntPtr> handles,
        DisplayInfo targetDisplay,
        bool focusFirst,
        string capsuleId)
    {
        var moved = 0;
        var failed = 0;
        var work = targetDisplay.WorkArea;
        foreach (var handle in handles)
        {
            var placement = new NativeMethods.WINDOWPLACEMENT
            {
                length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>()
            };
            NativeMethods.GetWindowPlacement(handle, ref placement);

            var normal = placement.rcNormalPosition;
            var width = Math.Min(Math.Max(normal.Right - normal.Left, 640), work.Width);
            var height = Math.Min(Math.Max(normal.Bottom - normal.Top, 480), work.Height);
            var left = work.X + (work.Width - width) / 2;
            var top = work.Y + (work.Height - height) / 2;

            placement.showCmd = NativeMethods.SW_SHOWNORMAL;
            placement.rcNormalPosition = new NativeMethods.RECT
            {
                Left = left,
                Top = top,
                Right = left + width,
                Bottom = top + height
            };

            // Restore onto the target monitor first; maximizing then fills that monitor.
            if (NativeMethods.SetWindowPlacement(handle, ref placement))
            {
                moved++;
                TryAutoMaximize(handle);
            }
            else
            {
                failed++;
            }
        }

        if (focusFirst && moved > 0)
        {
            FocusWindow(handles[0]);
        }

        _log.Info($"Page '{capsuleId}' stacked {moved} maximized window(s) on {targetDisplay.Label}; {failed} failed.");
        return new CapsuleMoveResult(moved, failed);
    }

    private static IReadOnlyList<ScreenRect> BuildPageSlots(
        int count,
        PageLayout layout,
        ScreenRect work)
    {
        const int gap = 8;
        var slots = new List<ScreenRect>(count);
        if (count <= 0)
        {
            return slots;
        }

        if (layout == PageLayout.Auto && count == 1)
        {
            slots.Add(work);
            return slots;
        }

        if (layout == PageLayout.Auto && count == 2)
        {
            var half = (work.Width - gap) / 2;
            slots.Add(new ScreenRect(work.X, work.Y, half, work.Height));
            slots.Add(new ScreenRect(work.X + half + gap, work.Y, work.Width - half - gap, work.Height));
            return slots;
        }

        if (layout == PageLayout.Auto && count == 3)
        {
            // Classic focus layout: one large pane on the left and two stacked
            // panes on the right.
            var left = (work.Width - gap) / 2;
            var right = work.Width - left - gap;
            var top = (work.Height - gap) / 2;
            var rightX = work.X + left + gap;
            slots.Add(new ScreenRect(work.X, work.Y, left, work.Height));
            slots.Add(new ScreenRect(rightX, work.Y, right, top));
            slots.Add(new ScreenRect(rightX, work.Y + top + gap, right, work.Height - top - gap));
            return slots;
        }

        int columns;
        int rows;
        if (layout == PageLayout.Columns)
        {
            columns = count;
            rows = 1;
        }
        else if (layout == PageLayout.Rows)
        {
            columns = 1;
            rows = count;
        }
        else
        {
            columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(count)));
            rows = Math.Max(1, (int)Math.Ceiling(count / (double)columns));
        }

        var cellWidth = Math.Max(1, (work.Width - gap * (columns - 1)) / columns);
        var cellHeight = Math.Max(1, (work.Height - gap * (rows - 1)) / rows);

        for (var i = 0; i < count; i++)
        {
            var row = i / columns;
            var column = i % columns;
            slots.Add(new ScreenRect(
                work.X + column * (cellWidth + gap),
                work.Y + row * (cellHeight + gap),
                cellWidth,
                cellHeight));
        }

        return slots;
    }

    /// <summary>
    /// Routes every window assigned to a capsule to the given display. A single
    /// DeferWindowPos batch is used where possible, preserving relative layout.
    /// </summary>
    public CapsuleMoveResult MoveCapsuleWindowsToDisplay(string capsuleId, DisplayInfo targetDisplay)
    {
        var targets = new List<(IntPtr Handle, ScreenRect Target)>();
        lock (_gate)
        {
            foreach (var tracked in _tracked.Values)
            {
                if (!string.Equals(tracked.Window.CapsuleId, capsuleId, StringComparison.Ordinal))
                {
                    continue;
                }

                var sourceDisplay = _displays.GetForWindow(tracked.Window.Handle);
                if (string.Equals(sourceDisplay.DeviceName, targetDisplay.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var target = CalculateTargetBounds(tracked.Window.Bounds, sourceDisplay.WorkArea, targetDisplay.WorkArea);
                targets.Add((tracked.Window.Handle, target));
            }
        }

        if (targets.Count == 0)
        {
            return new CapsuleMoveResult(0, 0);
        }

        var moved = 0;
        var failed = 0;

        var batch = targets.Where(t => !IsMaximized(t.Handle)).ToList();
        var maximized = targets.Where(t => IsMaximized(t.Handle)).ToList();

        if (batch.Count > 0)
        {
            var hdwp = NativeMethods.BeginDeferWindowPos(batch.Count);
            var batchOk = hdwp != IntPtr.Zero;
            if (batchOk)
            {
                foreach (var (handle, target) in batch)
                {
                    var result = NativeMethods.DeferWindowPos(
                        hdwp,
                        handle,
                        IntPtr.Zero,
                        target.X,
                        target.Y,
                        target.Width,
                        target.Height,
                        NativeMethods.SWP_NOZORDER |
                        NativeMethods.SWP_NOACTIVATE |
                        NativeMethods.SWP_NOOWNERZORDER);
                    if (!result)
                    {
                        batchOk = false;
                        break;
                    }
                }

                if (batchOk && NativeMethods.EndDeferWindowPos(hdwp))
                {
                    moved += batch.Count;
                }
                else
                {
                    if (hdwp != IntPtr.Zero)
                    {
                        NativeMethods.EndDeferWindowPos(hdwp);
                    }

                    foreach (var (handle, target) in batch)
                    {
                        if (SetWindowBounds(handle, target))
                        {
                            moved++;
                        }
                        else
                        {
                            failed++;
                        }
                    }
                }
            }
        }

        foreach (var (handle, target) in maximized)
        {
            NativeMethods.ShowWindowAsync(handle, NativeMethods.SW_RESTORE);
            if (SetWindowBounds(handle, target))
            {
                NativeMethods.ShowWindowAsync(handle, NativeMethods.SW_MAXIMIZE);
                moved++;
            }
            else
            {
                failed++;
            }
        }

        if (failed > 0)
        {
            _log.Warn($"Capsule routing moved {moved} window(s); {failed} failed.");
        }

        return new CapsuleMoveResult(moved, failed);
    }

    public bool MoveWindowToDisplay(IntPtr hwnd, DisplayInfo targetDisplay)
    {
        lock (_gate)
        {
            if (!_tracked.TryGetValue(hwnd, out var tracked))
            {
                return false;
            }

            var sourceDisplay = _displays.GetForWindow(hwnd);
            if (string.Equals(sourceDisplay.DeviceName, targetDisplay.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var target = CalculateTargetBounds(tracked.Window.Bounds, sourceDisplay.WorkArea, targetDisplay.WorkArea);
            var wasMaximized = IsMaximized(hwnd);
            if (wasMaximized)
            {
                NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
            }

            var success = SetWindowBounds(hwnd, target);
            if (success && wasMaximized)
            {
                NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_MAXIMIZE);
            }

            return success;
        }
    }

    /// <summary>
    /// Focuses a window and makes it fill its monitor. Used by the infinite
    /// window scroll so each selected window behaves like a Pop!_OS workspace.
    /// </summary>
    public bool FocusAndMaximizeWindow(IntPtr hwnd)
    {
        bool canMaximize;
        lock (_gate)
        {
            if (!_tracked.ContainsKey(hwnd) || !NativeMethods.IsWindow(hwnd))
            {
                return false;
            }

            var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
            canMaximize = (style & NativeMethods.WS_MAXIMIZEBOX) != 0;
        }

        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
        }

        if (canMaximize && !NativeMethods.IsZoomed(hwnd))
        {
            NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_MAXIMIZE);
        }

        return FocusWindow(hwnd);
    }

    public bool FocusWindow(IntPtr hwnd)
    {
        lock (_gate)
        {
            if (!_tracked.ContainsKey(hwnd) || !NativeMethods.IsWindow(hwnd))
            {
                return false;
            }

            if (_appCloaked.Remove(hwnd))
            {
                SetDwmCloak(hwnd, false);
                _sessionGuard.UnregisterIsolatedWindow(hwnd);
            }

            if (_appMinimized.Remove(hwnd))
            {
                if (NativeMethods.IsIconic(hwnd))
                {
                    NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
                }

                _sessionGuard.UnregisterIsolatedWindow(hwnd);
            }
        }

        var currentThread = NativeMethods.GetCurrentThreadId();
        var targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero
            ? 0
            : NativeMethods.GetWindowThreadProcessId(foreground, out _);

        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindowAsync(hwnd, NativeMethods.SW_RESTORE);
        }

        var attachedTarget = false;
        var attachedForeground = false;
        try
        {
            if (targetThread != 0 && targetThread != currentThread)
            {
                attachedTarget = NativeMethods.AttachThreadInput(currentThread, targetThread, true);
            }

            if (foregroundThread != 0 && foregroundThread != currentThread && foregroundThread != targetThread)
            {
                attachedForeground = NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
            }

            NativeMethods.BringWindowToTop(hwnd);
            return NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attachedForeground)
            {
                NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }

            if (attachedTarget)
            {
                NativeMethods.AttachThreadInput(currentThread, targetThread, false);
            }
        }
    }

    private static ScreenRect CalculateTargetBounds(ScreenRect source, ScreenRect sourceWork, ScreenRect targetWork)
    {
        if (sourceWork.Width <= 0 || sourceWork.Height <= 0)
        {
            return new ScreenRect(targetWork.X + 48, targetWork.Y + 48, Math.Min(1280, targetWork.Width - 96), Math.Min(800, targetWork.Height - 96));
        }

        var relativeX = (source.X - sourceWork.X) / (double)sourceWork.Width;
        var relativeY = (source.Y - sourceWork.Y) / (double)sourceWork.Height;

        var width = Math.Clamp((int)(source.Width * (targetWork.Width / (double)sourceWork.Width)), 320, targetWork.Width);
        var height = Math.Clamp((int)(source.Height * (targetWork.Height / (double)sourceWork.Height)), 220, targetWork.Height);

        var x = targetWork.X + (int)Math.Round(relativeX * targetWork.Width);
        var y = targetWork.Y + (int)Math.Round(relativeY * targetWork.Height);

        x = Math.Clamp(x, targetWork.X, targetWork.Right - width);
        y = Math.Clamp(y, targetWork.Y, targetWork.Bottom - height);

        return new ScreenRect(x, y, width, height);
    }

    private static bool SetWindowBounds(IntPtr hwnd, ScreenRect bounds) =>
        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER);

    private static bool IsMaximized(IntPtr hwnd) => NativeMethods.IsZoomed(hwnd);

    private static ScreenRect GetWindowBounds(IntPtr hwnd, ScreenRect? previousBounds = null)
    {
        // Minimized windows often report their normal position only through
        // WINDOWPLACEMENT; GetWindowRect/DWM can return the off-screen icon
        // coordinate (-32000). Prefer the last valid snapshot, then placement.
        if (NativeMethods.IsIconic(hwnd))
        {
            if (previousBounds.HasValue && !previousBounds.Value.IsEmpty)
            {
                return previousBounds.Value;
            }

            var placement = new NativeMethods.WINDOWPLACEMENT
            {
                length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>()
            };
            if (NativeMethods.GetWindowPlacement(hwnd, ref placement))
            {
                var normal = placement.rcNormalPosition;
                if (normal.Width > 0 && normal.Height > 0)
                {
                    return new ScreenRect(normal.Left, normal.Top, normal.Width, normal.Height);
                }
            }
        }

        var result = NativeMethods.DwmGetWindowAttribute(
            hwnd,
            NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
            out NativeMethods.RECT rect,
            System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>());

        if (result != 0 && !NativeMethods.GetWindowRect(hwnd, out rect))
        {
            return default;
        }

        return new ScreenRect(rect.Left, rect.Top, rect.Width, rect.Height);
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var length = Math.Min(NativeMethods.GetWindowTextLength(hwnd) + 1, 1024);
        var builder = new StringBuilder(length);
        NativeMethods.GetWindowText(hwnd, builder, length);
        return builder.ToString().Trim();
    }

    private static string GetWindowClass(IntPtr hwnd)
    {
        var builder = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private static IntPtr GetRootWindow(IntPtr hwnd) =>
        hwnd == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);

    private static bool TryGetProcessPath(int processId, out string path)
    {
        path = string.Empty;
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var builder = new StringBuilder(2048);
            var size = (uint)builder.Capacity;
            if (!NativeMethods.QueryFullProcessImageName(handle, 0, builder, ref size))
            {
                return false;
            }

            path = builder.ToString();
            return !string.IsNullOrWhiteSpace(path);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private bool AreWeCloaking(IntPtr hwnd)
    {
        lock (_gate)
        {
            return _appCloaked.Contains(hwnd);
        }
    }

    private static bool IsDwmCloaked(IntPtr hwnd)
    {
        try
        {
            return NativeMethods.DwmGetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_CLOAKED,
                out int cloaked,
                sizeof(int)) == 0 && cloaked != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    private static int SetDwmCloak(IntPtr hwnd, bool cloak)
    {
        try
        {
            var value = cloak ? 1 : 0;
            return NativeMethods.DwmSetWindowAttribute(
                hwnd,
                NativeMethods.DWMWA_CLOAK,
                ref value,
                sizeof(int));
        }
        catch (DllNotFoundException)
        {
            return unchecked((int)0x8007007E);
        }
    }

    private void RaiseWindowsChanged(IReadOnlyList<ManagedWindow>? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        WindowsChanged?.Invoke(this, new WindowsChangedEventArgs(snapshot));
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cts;
        Task? tracking;
        lock (_gate)
        {
            cts = _cts;
            tracking = _trackingTask;
            _cts = null;
            _trackingTask = null;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            try
            {
                if (tracking is not null)
                {
                    await tracking.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        RestoreAllIsolatedWindows();
        RestoreAutoMaximizedWindows();
        cts?.Dispose();
        _log.Info("Window tracking stopped and isolated window state restored.");
    }

    private sealed class TrackedWindow
    {
        public int ZOrder { get; init; }
        public ManagedWindow Window { get; set; } = null!;
    }
}
