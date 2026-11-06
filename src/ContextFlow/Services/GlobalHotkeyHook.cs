using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ContextFlow.Infrastructure;
using ContextFlow.Interop;

namespace ContextFlow.Services;

/// <summary>
/// Global Ctrl+E+F chord listener.
///
/// RegisterHotKey cannot represent a chord with two non-modifier keys (E and F
/// pressed together), so ContextFlow uses a low-level keyboard hook instead.
/// The hook observes keys globally but never swallows input: every event is
/// forwarded with CallNextHookEx.
/// </summary>
public sealed class GlobalHotkeyHook : IDisposable
{
    private readonly AppLog _log;
    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.LowLevelKeyboardProc _hookProc;
    private IntPtr _hookHandle;
    private bool _eDown;
    private bool _fDown;
    private bool _fired;
    private bool _disposed;

    public GlobalHotkeyHook(AppLog log)
    {
        _log = log;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _hookProc = HookCallback;
    }

    public event EventHandler? Pressed;

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
            _log.Info("Global Ctrl+E+F low-level keyboard hook installed.");
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
                    var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                    var key = (int)data.vkCode;

                    if (key == NativeMethods.VK_E)
                    {
                        _eDown = true;
                    }
                    else if (key == NativeMethods.VK_F)
                    {
                        _fDown = true;
                    }

                    if (!_fired && _eDown && _fDown && IsControlDown())
                    {
                        _fired = true;
                        RaisePressed();
                    }
                }
                else if (message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
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

                    if (!_eDown || !_fDown || !IsControlDown())
                    {
                        _fired = false;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.Error("Global hotkey callback failed.", ex);
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private static bool IsControlDown() =>
        (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;

    private void RaisePressed()
    {
        if (_dispatcher.CheckAccess())
        {
            Pressed?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _dispatcher.BeginInvoke(new Action(() => Pressed?.Invoke(this, EventArgs.Empty)), DispatcherPriority.Input);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        IsRunning = false;
        _log.Info("Global hotkey hook released.");
    }
}
