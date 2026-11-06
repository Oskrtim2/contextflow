using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ContextFlow.Interop;

namespace ContextFlow.Services;

/// <summary>
/// Native Windows 11 composition integration: Mica/Acrylic system backdrop,
/// immersive dark mode, rounded corners, and a Windows 10 acrylic fallback.
/// The WPF window remains non-layered so DWM can compose the backdrop behind it.
/// </summary>
public static class WindowEffects
{
    public static void ApplyHudBackdrop(Window window, bool isDark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        if (PresentationSource.FromVisual(window) is HwndSource source && source.CompositionTarget is not null)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }

        // Tool window: no taskbar button, no Alt+Tab entry for the HUD shell.
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        var newExStyle = exStyle | NativeMethods.WS_EX_TOOLWINDOW;
        if (newExStyle != exStyle)
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(newExStyle));
        }

        var dark = isDark ? 1 : 0;
        if (NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
        {
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, sizeof(int));
        }

        var corner = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        var backdrop = NativeMethods.DWMSBT_TRANSIENTWINDOW;
        var backdropResult = NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

        var margins = new NativeMethods.MARGINS
        {
            cxLeftWidth = 1,
            cxRightWidth = 1,
            cyTopHeight = 1,
            cyBottomHeight = 1
        };
        NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);

        if (backdropResult != 0)
        {
            ApplyLegacyAcrylic(hwnd, isDark);
        }
    }

    public static void ApplyBubbleWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        var newExStyle = exStyle | NativeMethods.WS_EX_TOOLWINDOW;
        if (newExStyle != exStyle)
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(newExStyle));
        }
    }

    private static void ApplyLegacyAcrylic(IntPtr hwnd, bool isDark)
    {
        try
        {
            var accent = new NativeMethods.ACCENTPOLICY
            {
                AccentState = NativeMethods.ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 2,
                GradientColor = isDark ? unchecked((int)0xD90F172A) : unchecked((int)0xD9F8FAFC),
                AnimationId = 0
            };

            var size = Marshal.SizeOf<NativeMethods.ACCENTPOLICY>();
            var accentPointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, accentPointer, false);
                var data = new NativeMethods.WINDOWCOMPOSITIONATTRIBDATA
                {
                    Attribute = NativeMethods.WCA_ACCENT_POLICY,
                    Data = accentPointer,
                    SizeOfData = size
                };
                NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(accentPointer);
            }
        }
        catch
        {
            // Older systems can fall back to the translucent WPF surface brush.
        }
    }
}
