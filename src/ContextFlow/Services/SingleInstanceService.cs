using ContextFlow.Interop;

namespace ContextFlow.Services;

/// <summary>
/// Single-instance guard using a per-user mutex. A second launch broadcasts a
/// private window message so the existing instance surfaces its HUD instead of
/// starting a duplicate background process.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private const string MutexName = @"Local\ContextFlow.Instance.7B39A1F6-2C6A-4D37-93E1-08F0E2A77103";
    private const string ShowMessageName = "ContextFlow.ShowHUD.7B39A1F6-2C6A-4D37-93E1-08F0E2A77103";
    public const string BubbleWindowTitle = "ContextFlow.Bubble.7B39A1F6";
    private static readonly uint ShowMessage = NativeMethods.RegisterWindowMessage(ShowMessageName);

    private readonly Mutex _mutex;
    private bool _ownsMutex;
    private bool _disposed;

    public SingleInstanceService()
    {
        _mutex = new Mutex(false, MutexName);
        try
        {
            _ownsMutex = _mutex.WaitOne(0, false);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }
    }

    public bool IsPrimaryInstance => _ownsMutex;

    public static uint ShowHudMessage => ShowMessage;

    public static void SignalExistingInstance()
    {
        var bubble = NativeMethods.FindWindow(null, BubbleWindowTitle);
        if (bubble != IntPtr.Zero)
        {
            NativeMethods.PostMessage(bubble, ShowMessage, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        // Rare fallback during another instance's startup before the bubble HWND
        // exists. HWND_BROADCAST reaches all top-level windows including tools.
        var hwndBroadcast = new IntPtr(0xFFFF);
        NativeMethods.PostMessage(hwndBroadcast, ShowMessage, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned on this thread anymore; nothing to release.
            }

            _ownsMutex = false;
        }

        _mutex.Dispose();
    }
}
