using ContextFlow.Infrastructure;
using ContextFlow.Interop;
using ContextFlow.Models;

namespace ContextFlow.Services;

public sealed record WindowNavigationResult(
    IntPtr Handle,
    string Title,
    string ProcessName,
    int Index,
    int Count,
    bool Focused,
    bool Maximized);

/// <summary>
/// Provides a stable, circular order of managed windows. The order follows the
/// capsule canvas (top to bottom, then left to right), so Ctrl+E / Ctrl+F behave
/// like scrolling through an infinite vertical desktop. Windows that are not
/// already maximized are maximized when they are selected.
/// </summary>
public sealed class WindowNavigator : IDisposable
{
    private readonly WindowManager _windowManager;
    private readonly SettingsService _settings;
    private readonly AppLog _log;
    private readonly object _gate = new();

    private IntPtr _currentHandle;
    private int _currentIndex = -1;
    private DateTime _lastNavigationUtc = DateTime.MinValue;
    private bool _disposed;

    public WindowNavigator(WindowManager windowManager, SettingsService settings, AppLog log)
    {
        _windowManager = windowManager;
        _settings = settings;
        _log = log;
        _windowManager.WindowsChanged += OnWindowsChanged;
    }

    public event EventHandler<WindowNavigationResult>? Navigated;

    public WindowNavigationResult? Current { get; private set; }

    public WindowNavigationResult? Navigate(int direction)
    {
        if (direction == 0)
        {
            return Current;
        }

        var windows = BuildOrder();
        if (windows.Count == 0)
        {
            _log.Info("Window scroll ignored: no manageable windows.");
            return null;
        }

        var currentIndex = FindCurrentIndex(windows);
        var nextIndex = ((currentIndex + direction) % windows.Count + windows.Count) % windows.Count;
        var target = windows[nextIndex];
        var focused = _windowManager.FocusAndMaximizeWindow(target.Handle);
        var maximized = NativeMethods.IsZoomed(target.Handle);
        var result = new WindowNavigationResult(
            target.Handle,
            DisplayTitle(target),
            target.ProcessName,
            nextIndex,
            windows.Count,
            focused,
            maximized);

        lock (_gate)
        {
            _currentHandle = target.Handle;
            _currentIndex = nextIndex;
            _lastNavigationUtc = DateTime.UtcNow;
            Current = result;
        }

        _log.Info(
            $"Window scroll {(direction < 0 ? "up" : "down")}: " +
            $"{result.ProcessName} - {result.Title} " +
            $"({result.Index + 1}/{result.Count}) focused={focused} maximized={maximized}.");
        Navigated?.Invoke(this, result);
        return result;
    }

    /// <summary>Focuses a specific window without changing its size.</summary>
    public WindowNavigationResult? FocusWindow(IntPtr handle, bool maximize = false)
    {
        var windows = BuildOrder();
        var index = IndexOf(windows, handle);
        if (index < 0)
        {
            return null;
        }

        var target = windows[index];
        var focused = maximize
            ? _windowManager.FocusAndMaximizeWindow(handle)
            : _windowManager.FocusWindow(handle);
        var result = new WindowNavigationResult(
            target.Handle,
            DisplayTitle(target),
            target.ProcessName,
            index,
            windows.Count,
            focused,
            NativeMethods.IsZoomed(target.Handle));

        lock (_gate)
        {
            _currentHandle = target.Handle;
            _currentIndex = index;
            _lastNavigationUtc = DateTime.UtcNow;
            Current = result;
        }

        Navigated?.Invoke(this, result);
        return result;
    }

    /// <summary>Resolves the current window from the foreground/stable order.</summary>
    public WindowNavigationResult? ResolveCurrent()
    {
        var windows = BuildOrder();
        if (windows.Count == 0)
        {
            lock (_gate)
            {
                Current = null;
                _currentHandle = IntPtr.Zero;
                _currentIndex = -1;
            }

            return null;
        }

        var index = FindCurrentIndex(windows);
        var target = windows[index];
        var result = new WindowNavigationResult(
            target.Handle,
            DisplayTitle(target),
            target.ProcessName,
            index,
            windows.Count,
            target.IsForeground,
            NativeMethods.IsZoomed(target.Handle));

        lock (_gate)
        {
            if (_currentHandle == IntPtr.Zero || _currentIndex < 0)
            {
                _currentHandle = target.Handle;
            }

            _currentIndex = index;
            Current = result;
        }

        return result;
    }

    private IReadOnlyList<ManagedWindow> BuildOrder()
    {
        var capsules = _settings.Current.Capsules;
        var capsuleOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < capsules.Count; i++)
        {
            capsuleOrder[capsules[i].Id] = i;
        }

        return _windowManager.Snapshot
            .Where(w => !string.IsNullOrWhiteSpace(w.Title))
            .OrderBy(w => capsuleOrder.TryGetValue(w.CapsuleId, out var index) ? index : int.MaxValue)
            .ThenBy(w => w.FirstSeenUtc)
            .ThenBy(w => w.Handle.ToInt64())
            .ToList();
    }

    private int FindCurrentIndex(IReadOnlyList<ManagedWindow> windows)
    {
        // While the user is scrolling, the window manager snapshot can lag by
        // up to one second. Trust our own last selection during that window so
        // rapid Ctrl+F presses advance instead of selecting the same target.
        lock (_gate)
        {
            if (_currentHandle != IntPtr.Zero &&
                DateTime.UtcNow - _lastNavigationUtc < TimeSpan.FromSeconds(1.5))
            {
                var remembered = IndexOf(windows, _currentHandle);
                if (remembered >= 0)
                {
                    return remembered;
                }
            }
        }

        // Outside a rapid scroll sequence, the foreground window is
        // authoritative whenever ContextFlow itself is not in front.
        var foreground = windows.FirstOrDefault(w => w.IsForeground);
        if (foreground is not null)
        {
            return IndexOf(windows, foreground.Handle);
        }

        lock (_gate)
        {
            if (_currentHandle != IntPtr.Zero)
            {
                var remembered = IndexOf(windows, _currentHandle);
                if (remembered >= 0)
                {
                    return remembered;
                }
            }

            if (_currentIndex >= 0 && _currentIndex < windows.Count)
            {
                return _currentIndex;
            }
        }

        return 0;
    }

    private void OnWindowsChanged(object? sender, WindowsChangedEventArgs e)
    {
        var foreground = e.Windows.FirstOrDefault(w => w.IsForeground);
        if (foreground is null)
        {
            return;
        }

        lock (_gate)
        {
            // Ignore snapshots captured while a scroll focus operation is
            // still settling; they may still describe the previous window.
            if (DateTime.UtcNow - _lastNavigationUtc < TimeSpan.FromSeconds(1.5))
            {
                return;
            }

            _currentHandle = foreground.Handle;
            _currentIndex = -1;
        }
    }

    private static int IndexOf(IReadOnlyList<ManagedWindow> windows, IntPtr handle)
    {
        for (var i = 0; i < windows.Count; i++)
        {
            if (windows[i].Handle == handle)
            {
                return i;
            }
        }

        return -1;
    }

    private static string DisplayTitle(ManagedWindow window) =>
        string.IsNullOrWhiteSpace(window.Title) ? "(untitled window)" : window.Title;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _windowManager.WindowsChanged -= OnWindowsChanged;
    }
}
