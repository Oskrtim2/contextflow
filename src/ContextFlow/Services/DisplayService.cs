using ContextFlow.Interop;
using ContextFlow.Models;

namespace ContextFlow.Services;

/// <summary>
/// Enumerates physical monitors via EnumDisplayMonitors and translates between
/// monitor handles, stable device names, and physical work areas.
/// </summary>
public sealed class DisplayService
{
    private readonly object _gate = new();
    private List<DisplayInfo> _displays = new();
    private Dictionary<IntPtr, DisplayInfo> _byHandle = new();

    public IReadOnlyList<DisplayInfo> Current
    {
        get
        {
            lock (_gate)
            {
                return _displays;
            }
        }
    }

    public IReadOnlyList<DisplayInfo> Refresh()
    {
        var raw = new List<(IntPtr Handle, NativeMethods.MONITORINFOEX Info)>();

        NativeMethods.MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
        {
            var info = NativeMethods.MONITORINFOEX.Create();
            if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
            {
                raw.Add((hMonitor, info));
            }

            return true;
        };

        if (!NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero) && raw.Count == 0)
        {
            var handle = NativeMethods.MonitorFromWindow(IntPtr.Zero, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var info = NativeMethods.MONITORINFOEX.Create();
            if (handle != IntPtr.Zero && NativeMethods.GetMonitorInfo(handle, ref info))
            {
                raw.Add((handle, info));
            }
        }

        raw.Sort((a, b) =>
        {
            var primaryCompare = (b.Info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY)
                .CompareTo(a.Info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY);
            if (primaryCompare != 0)
            {
                return primaryCompare;
            }

            var x = a.Info.rcMonitor.Left.CompareTo(b.Info.rcMonitor.Left);
            return x != 0 ? x : a.Info.rcMonitor.Top.CompareTo(b.Info.rcMonitor.Top);
        });

        var displays = new List<DisplayInfo>(raw.Count);
        var byHandle = new Dictionary<IntPtr, DisplayInfo>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var (handle, info) = raw[i];
            uint dpi = 96;
            try
            {
                if (NativeMethods.GetDpiForMonitor(handle, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
                {
                    dpi = dpiX;
                }
            }
            catch (DllNotFoundException)
            {
                // Older Windows builds: retain the 96 DPI fallback.
            }

            var bounds = new ScreenRect(
                info.rcMonitor.Left,
                info.rcMonitor.Top,
                info.rcMonitor.Width,
                info.rcMonitor.Height);
            var work = new ScreenRect(
                info.rcWork.Left,
                info.rcWork.Top,
                info.rcWork.Width,
                info.rcWork.Height);

            var display = new DisplayInfo(
                info.szDevice,
                $"Display {i + 1}",
                (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                bounds,
                work,
                i,
                dpi);

            displays.Add(display);
            byHandle[handle] = display;
        }

        lock (_gate)
        {
            _displays = displays;
            _byHandle = byHandle;
        }

        return displays;
    }

    public DisplayInfo Primary
    {
        get
        {
            lock (_gate)
            {
                return _displays.FirstOrDefault(d => d.IsPrimary)
                       ?? _displays.FirstOrDefault()
                       ?? new DisplayInfo(
                           string.Empty,
                           "Display 1",
                           true,
                           new ScreenRect(0, 0, 1920, 1080),
                           new ScreenRect(0, 0, 1920, 1040),
                           0,
                           96);
            }
        }
    }

    public DisplayInfo? FindByDevice(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return null;
        }

        lock (_gate)
        {
            return _displays.FirstOrDefault(d =>
                string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
        }
    }

    public DisplayInfo GetByDeviceOrPrimary(string? deviceName) => FindByDevice(deviceName) ?? Primary;

    public DisplayInfo GetForWindow(IntPtr hwnd)
    {
        var handle = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        lock (_gate)
        {
            if (_byHandle.TryGetValue(handle, out var display))
            {
                return display;
            }
        }

        Refresh();
        lock (_gate)
        {
            return _byHandle.TryGetValue(handle, out var display) ? display : Primary;
        }
    }

    public DisplayInfo GetForRect(ScreenRect rect)
    {
        var nativeRect = new NativeMethods.RECT
        {
            Left = rect.Left,
            Top = rect.Top,
            Right = rect.Right,
            Bottom = rect.Bottom
        };
        var handle = NativeMethods.MonitorFromRect(ref nativeRect, NativeMethods.MONITOR_DEFAULTTONEAREST);
        lock (_gate)
        {
            if (_byHandle.TryGetValue(handle, out var display))
            {
                return display;
            }
        }

        return Primary;
    }

    public string GetDeviceNameForWindow(IntPtr hwnd) => GetForWindow(hwnd).DeviceName;
}
