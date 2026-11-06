using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ContextFlow.Infrastructure;
using ContextFlow.Interop;

namespace ContextFlow.Services;

/// <summary>
/// Global keyboard listener for the ContextFlow shortcuts:
///   Ctrl+E      previous managed window (scroll up)
///   Ctrl+F      next managed window (scroll down)
///   Ctrl+E+F    expand or collapse the floating HUD
///
/// A chord with two non-modifier keys cannot be registered with RegisterHotKey,
/// so this class uses a low-level keyboard hook. A short disambiguation window
/// prevents Ctrl+E or Ctrl+F from firing when the user is actually pressing the
/// full Ctrl+E+F chord. The hook observes input but never suppresses it.
/// </summary>
public sealed class GlobalHotkeyHook : IDisposable
{
    private static readonly TimeSpan ChordWindow = TimeSpan.FromMilliseconds(180);

    private readonly AppLog _log;
    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.LowLevelKeyboardProc _hookProc;
    private DispatcherTimer? _navigationTimer;
    private IntPtr _hookHandle;
    private bool _eDown;
    private bool _fDown;
    private bool _chordFired;
    private bool _suppressChordUntilRelease;
    private int _pendingDirection;
    private bool _disposed;

    public GlobalHotkeyHook(AppLog log)
    {
        _log = log;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _hookProc = HookCallback;
    }

    /// <summary>Raised for the complete Ctrl+E+F chord.</summary>
    public event EventHandler? Pressed;

    /// <summary>Raised when Ctrl+E is pressed without F.</summary>
    public event EventHandler? PreviousRequested;

    /// <summary>Raised when Ctrl+F is pressed without E.</summary>
    public event EventHandler? NextRequested;

    public bool IsRunning { get; private set; }

    public string? LastError { get; private set; }

    public bool Start()
    {
        if (_disposed || IsRunning)
        {
            return IsRunning;
        }

        try
        {
            _navigationTimer = new DispatcherTimer(DispatcherPriority.Input, _dispatcher)
            {
                Interval = ChordWindow
            };
            _navigationTimer.Tick += OnNavigationTimerTick;

            var module = NativeMethods.GetModuleHandle(null);
            _hookHandle = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL,
                _hookProc,
                module,
                0);

            if (_hookHandle == IntPtr.Zero)
            {
                LastError = $"SetWindowsHookEx failed (Win32 error {Marshal.GetLastWin32Error()}).";
                _log.Warn(LastError);
                return false;
            }

            IsRunning = true;
            _log.Info("Global Ctrl+E / Ctrl+F / Ctrl+E+F low-level keyboard hook installed.");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.Error("Failed to install the global hotkey hook.", ex);
            return false;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var message = wParam.ToInt32();
                if (message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
                {
                    HandleKeyDown(lParam);
                }
                else if (message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
                {
                    HandleKeyUp(lParam);
                }
            }
            catch (Exception ex)
            {
                _log.Error("Global hotkey callback failed.", ex);
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void HandleKeyDown(IntPtr lParam)
    {
        var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        var key = (int)data.vkCode;

        if (key == NativeMethods.VK_E)
        {
            if (_eDown)
            {
                return;
            }

            _eDown = true;
            if (!IsControlDown())
            {
                return;
            }

            if (!_chordFired && !_suppressChordUntilRelease)
            {
                if (IsKeyDown(NativeMethods.VK_F))
                {
                    FireChord();
                }
                else
                {
                    StartPending(-1);
                }
            }
        }
        else if (key == NativeMethods.VK_F)
        {
            if (_fDown)
            {
                return;
            }

            _fDown = true;
            if (!IsControlDown())
            {
                return;
            }

            if (!_chordFired && !_suppressChordUntilRelease)
            {
                if (IsKeyDown(NativeMethods.VK_E))
                {
                    FireChord();
                }
                else
                {
                    StartPending(1);
                }
            }
        }
    }

    private void HandleKeyUp(IntPtr lParam)
    {
        var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        var key = (int)data.vkCode;

        if (key == NativeMethods.VK_E)
        {
            _eDown = false;
        }
        else if (key == NativeMethods.VK_F)
        {
            _fDown = false;
        }

        if (!_eDown && !_fDown)
        {
            _chordFired = false;
            _suppressChordUntilRelease = false;
        }
    }

    private void StartPending(int direction)
    {
        if (_navigationTimer is null || _pendingDirection != 0)
        {
            return;
        }

        _pendingDirection = direction;
        _navigationTimer.Stop();
        _navigationTimer.Start();
    }

    private void CancelPending()
    {
        _navigationTimer?.Stop();
        _pendingDirection = 0;
    }

    private void OnNavigationTimerTick(object? sender, EventArgs e)
    {
        _navigationTimer?.Stop();
        var direction = _pendingDirection;
        _pendingDirection = 0;

        if (direction == 0)
        {
            return;
        }

        // A lone Ctrl+E/F was detected. If the key is still held, do not allow
        // a slower second key press to turn the same gesture into the chord.
        // If the key was already released, leave the state clean for the next
        // shortcut (the timer can fire a few milliseconds after key-up).
        if (_eDown || _fDown)
        {
            _suppressChordUntilRelease = true;
        }
        if (direction < 0)
        {
            _log.Info("Global hotkey: previous window.");
            RaiseEvent(PreviousRequested);
        }
        else
        {
            _log.Info("Global hotkey: next window.");
            RaiseEvent(NextRequested);
        }
    }

    private void FireChord()
    {
        if (_chordFired)
        {
            return;
        }

        CancelPending();
        _chordFired = true;
        _suppressChordUntilRelease = true;
        _log.Info("Global hotkey: HUD toggle.");
        RaiseEvent(Pressed);
    }

    private static bool IsControlDown() => IsKeyDown(NativeMethods.VK_CONTROL);

    private static bool IsKeyDown(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private void RaiseEvent(EventHandler? handler)
    {
        if (handler is null)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            handler.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _dispatcher.BeginInvoke(new Action(() => handler.Invoke(this, EventArgs.Empty)), DispatcherPriority.Input);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _navigationTimer?.Stop();
        _navigationTimer = null;

        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        IsRunning = false;
        _log.Info("Global hotkey hook released.");
    }
}
