using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ContextFlow.Infrastructure;
using ContextFlow.Interop;
using ContextFlow.Models;

namespace ContextFlow.Services;

/// <summary>
/// Global keyboard listener for the ContextFlow shortcuts:
///   configurable scroll up / scroll down (Ctrl+E and Ctrl+F by default)
///   Ctrl+E+F to expand or collapse the floating HUD.
///
/// A chord with two non-modifier keys cannot be registered with RegisterHotKey,
/// so this class uses a low-level keyboard hook. When a scroll gesture uses
/// Ctrl+E or Ctrl+F, a short disambiguation window prevents the first key from
/// firing before the full Ctrl+E+F chord is recognized.
/// </summary>
public sealed class GlobalHotkeyHook : IDisposable
{
    private static readonly TimeSpan ChordWindow = TimeSpan.FromMilliseconds(180);

    private readonly AppLog _log;
    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.LowLevelKeyboardProc _hookProc;
    private readonly HashSet<int> _downKeys = new();
    private DispatcherTimer? _navigationTimer;
    private IntPtr _hookHandle;
    private HotkeyGesture _scrollUp = HotkeyGesture.DefaultScrollUp;
    private HotkeyGesture _scrollDown = HotkeyGesture.DefaultScrollDown;
    private bool _chordFired;
    private bool _suppressChordUntilRelease;
    private bool _suspended;
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

    /// <summary>Raised when the configured scroll-up gesture is pressed.</summary>
    public event EventHandler? PreviousRequested;

    /// <summary>Raised when the configured scroll-down gesture is pressed.</summary>
    public event EventHandler? NextRequested;

    public bool IsRunning { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Suppresses navigation while the settings tab is capturing a hotkey.</summary>
    public bool IsSuspended
    {
        get => _suspended;
        set
        {
            _suspended = value;
            CancelPending();
            _downKeys.Clear();
            _chordFired = false;
            _suppressChordUntilRelease = false;
        }
    }

    public void UpdateGestures(HotkeyGesture scrollUp, HotkeyGesture scrollDown)
    {
        _scrollUp = scrollUp.IsValid ? scrollUp : HotkeyGesture.DefaultScrollUp;
        _scrollDown = scrollDown.IsValid ? scrollDown : HotkeyGesture.DefaultScrollDown;
        CancelPending();
        _downKeys.Clear();
        _chordFired = false;
        _suppressChordUntilRelease = false;
        _log.Info($"Global scroll gestures updated: up={_scrollUp.Display}, down={_scrollDown.Display}.");
    }

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
            _log.Info("Global keyboard hook installed for scroll gestures and Ctrl+E+F.");
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
        if (nCode >= 0 && !_suspended)
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
        if (!_downKeys.Add(key))
        {
            return;
        }

        var modifiers = GetModifiers();

        // Full HUD chord. This wins over a scroll gesture, but only when the
        // pending scroll has not already fired (a slow second key must not
        // reopen the HUD).
        if (!_chordFired && !_suppressChordUntilRelease)
        {
            if (modifiers == HotkeyModifiers.Control && key == NativeMethods.VK_E &&
                _downKeys.Contains(NativeMethods.VK_F))
            {
                FireChord();
                return;
            }

            if (modifiers == HotkeyModifiers.Control && key == NativeMethods.VK_F &&
                _downKeys.Contains(NativeMethods.VK_E))
            {
                FireChord();
                return;
            }
        }

        if (_chordFired || _suppressChordUntilRelease)
        {
            return;
        }

        if (Matches(_scrollUp, key, modifiers))
        {
            if (NeedsChordDisambiguation(key, modifiers))
            {
                StartPending(-1);
            }
            else
            {
                RaisePrevious();
            }
        }
        else if (Matches(_scrollDown, key, modifiers))
        {
            if (NeedsChordDisambiguation(key, modifiers))
            {
                StartPending(1);
            }
            else
            {
                RaiseNext();
            }
        }
    }

    private void HandleKeyUp(IntPtr lParam)
    {
        var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        var key = (int)data.vkCode;
        _downKeys.Remove(key);

        if (!_downKeys.Contains(NativeMethods.VK_E) &&
            !_downKeys.Contains(NativeMethods.VK_F))
        {
            _chordFired = false;
            _suppressChordUntilRelease = false;
        }
    }

    private static bool Matches(HotkeyGesture gesture, int key, HotkeyModifiers modifiers) =>
        gesture.IsValid && gesture.VirtualKey == key && gesture.Modifiers == modifiers;

    private static bool NeedsChordDisambiguation(int key, HotkeyModifiers modifiers) =>
        modifiers == HotkeyModifiers.Control &&
        (key == NativeMethods.VK_E || key == NativeMethods.VK_F);

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

        // If the key is still held, do not let a slower second key turn the
        // gesture into the HUD chord. If it was released, leave the next
        // shortcut clean (the timer can fire just after key-up).
        if (_downKeys.Contains(NativeMethods.VK_E) || _downKeys.Contains(NativeMethods.VK_F))
        {
            _suppressChordUntilRelease = true;
        }

        if (direction < 0)
        {
            RaisePrevious();
        }
        else
        {
            RaiseNext();
        }
    }

    private void RaisePrevious()
    {
        _log.Info("Global hotkey: previous window.");
        RaiseEvent(PreviousRequested);
    }

    private void RaiseNext()
    {
        _log.Info("Global hotkey: next window.");
        RaiseEvent(NextRequested);
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

    private static HotkeyModifiers GetModifiers()
    {
        var modifiers = HotkeyModifiers.None;
        if (IsKeyDown(NativeMethods.VK_CONTROL)) modifiers |= HotkeyModifiers.Control;
        if (IsKeyDown(NativeMethods.VK_MENU)) modifiers |= HotkeyModifiers.Alt;
        if (IsKeyDown(NativeMethods.VK_SHIFT)) modifiers |= HotkeyModifiers.Shift;
        if (IsKeyDown(NativeMethods.VK_LWIN) || IsKeyDown(NativeMethods.VK_RWIN))
        {
            modifiers |= HotkeyModifiers.Win;
        }

        return modifiers;
    }

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
