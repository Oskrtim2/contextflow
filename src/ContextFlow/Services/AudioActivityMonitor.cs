using System.Runtime.InteropServices;
using ContextFlow.Infrastructure;

namespace ContextFlow.Services;

/// <summary>
/// Tracks which processes are producing sound, through the Windows Core Audio
/// session API on every active playback device. The optimizer never throttles
/// an app family that played audio in the last <see cref="AudibleGrace"/>:
/// slowing down a music player or a call causes audible glitches.
/// </summary>
public sealed class AudioActivityMonitor
{
    public static readonly TimeSpan AudibleGrace = TimeSpan.FromSeconds(30);

    private const int AudioSessionStateActive = 1;
    private const uint DeviceStateActive = 0x1;
    private const int ClsctxAll = 0x17;
    private const float AudiblePeak = 0.0005f;

    private static readonly Guid AudioSessionManager2Iid = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    private readonly AppLog _log;
    private readonly object _gate = new();
    private readonly Dictionary<int, DateTime> _lastAudible = new();
    private bool _failed;

    public AudioActivityMonitor(AppLog log)
    {
        _log = log;
    }

    /// <summary>True when any of the given processes produced sound recently.</summary>
    public bool IsAudible(IEnumerable<int> processIds)
    {
        var cutoff = DateTime.UtcNow - AudibleGrace;
        lock (_gate)
        {
            foreach (var pid in processIds)
            {
                if (_lastAudible.TryGetValue(pid, out var last) && last >= cutoff)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void Poll()
    {
        if (_failed)
        {
            return;
        }

        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? devices = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceStateActive, out devices));
            Marshal.ThrowExceptionForHR(devices.GetCount(out var count));

            var now = DateTime.UtcNow;
            var audible = new List<int>();
            for (uint i = 0; i < count; i++)
            {
                if (devices.Item(i, out var device) != 0 || device is null)
                {
                    continue;
                }

                try
                {
                    CollectAudibleSessions(device, audible);
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }

            lock (_gate)
            {
                foreach (var pid in audible)
                {
                    _lastAudible[pid] = now;
                }

                var expiry = now - AudibleGrace - AudibleGrace;
                foreach (var stale in _lastAudible.Where(p => p.Value < expiry).Select(p => p.Key).ToList())
                {
                    _lastAudible.Remove(stale);
                }
            }
        }
        catch (Exception ex)
        {
            // No audio service (e.g. some VMs). Audio protection simply stays off.
            _failed = true;
            _log.Warn($"Audio session monitoring unavailable: {ex.Message}");
        }
        finally
        {
            if (devices is not null)
            {
                Marshal.ReleaseComObject(devices);
            }

            if (enumerator is not null)
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
    }

    private static void CollectAudibleSessions(IMMDevice device, List<int> audible)
    {
        var iid = AudioSessionManager2Iid;
        if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var managerObject) != 0 || managerObject is null)
        {
            return;
        }

        var manager = (IAudioSessionManager2)managerObject;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            if (manager.GetSessionEnumerator(out sessions) != 0 || sessions is null)
            {
                return;
            }

            sessions.GetCount(out var sessionCount);
            for (var s = 0; s < sessionCount; s++)
            {
                if (sessions.GetSession(s, out var control) != 0 || control is null)
                {
                    continue;
                }

                try
                {
                    if (control.GetState(out var state) != 0 || state != AudioSessionStateActive)
                    {
                        continue;
                    }

                    var control2 = (IAudioSessionControl2)control;
                    if (control2.GetProcessId(out var pid) != 0 || pid == 0)
                    {
                        continue;
                    }

                    // An open-but-silent stream (paused video) is not audible.
                    var peak = 1f;
                    if (control is IAudioMeterInformation meter && meter.GetPeakValue(out var value) == 0)
                    {
                        peak = value;
                    }

                    if (peak >= AudiblePeak)
                    {
                        audible.Add((int)pid);
                    }
                }
                catch (InvalidCastException)
                {
                    // Session without IAudioSessionControl2.
                }
                finally
                {
                    Marshal.ReleaseComObject(control);
                }
            }
        }
        finally
        {
            if (sessions is not null)
            {
                Marshal.ReleaseComObject(sessions);
            }

            Marshal.ReleaseComObject(manager);
        }
    }

    // ---------------------------------------------------------------- COM interop

    private enum EDataFlow
    {
        Render = 0,
        Capture = 1,
        All = 2
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid iid,
            int clsCtx,
            IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? instance);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        // IAudioSessionManager
        [PreserveSig]
        int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr sessionControl);

        [PreserveSig]
        int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);

        // IAudioSessionManager2
        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int sessionCount);

        [PreserveSig]
        int GetSession(int sessionIndex, out IAudioSessionControl session);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // IAudioSessionControl slots (order matters for the vtable).
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName(IntPtr value, IntPtr eventContext);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath(IntPtr value, IntPtr eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingParam);
        [PreserveSig] int SetGroupingParam(ref Guid groupingParam, IntPtr eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);

        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint processId);
    }

    [ComImport]
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig]
        int GetPeakValue(out float peak);
    }
}
