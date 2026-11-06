using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ContextFlow.Interop;

namespace ContextFlow.Services;

/// <summary>
/// Resolves window/process icons through Win32 and caches frozen ImageSources.
/// All returned images are frozen and safe to use from the UI thread.
/// </summary>
public sealed class IconService
{
    private readonly ConcurrentDictionary<string, ImageSource> _shellIconCache =
        new(StringComparer.OrdinalIgnoreCase);

    public ImageSource? GetWindowIcon(IntPtr hwnd, string? executablePath)
    {
        var hIcon = TryGetWindowIcon(hwnd);
        if (hIcon != IntPtr.Zero)
        {
            var image = ConvertIcon(hIcon, destroyOriginal: true);
            if (image is not null)
            {
                return image;
            }
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        if (_shellIconCache.TryGetValue(executablePath, out var cached))
        {
            return cached;
        }

        var shellIcon = GetShellIcon(executablePath);
        if (shellIcon is not null)
        {
            _shellIconCache[executablePath] = shellIcon;
        }

        return shellIcon;
    }

    public ImageSource? GetIconForExecutable(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        if (_shellIconCache.TryGetValue(executablePath, out var cached))
        {
            return cached;
        }

        var icon = GetShellIcon(executablePath);
        if (icon is not null)
        {
            _shellIconCache[executablePath] = icon;
        }

        return icon;
    }

    private static IntPtr TryGetWindowIcon(IntPtr hwnd)
    {
        foreach (var iconType in new[] { NativeMethods.ICON_BIG, NativeMethods.ICON_SMALL2, NativeMethods.ICON_SMALL })
        {
            if (NativeMethods.SendMessageTimeout(
                    hwnd,
                    NativeMethods.WM_GETICON,
                    new UIntPtr((uint)iconType),
                    IntPtr.Zero,
                    NativeMethods.SMTO_ABORTIFHUNG,
                    120,
                    out var result) != IntPtr.Zero &&
                result != IntPtr.Zero)
            {
                var copy = NativeMethods.CopyIcon(result);
                if (copy != IntPtr.Zero)
                {
                    return copy;
                }
            }
        }

        var classIcon = NativeMethods.GetClassLongPtr(hwnd, NativeMethods.GCLP_HICON);
        if (classIcon == IntPtr.Zero)
        {
            classIcon = NativeMethods.GetClassLongPtr(hwnd, NativeMethods.GCLP_HICONSM);
        }

        if (classIcon != IntPtr.Zero)
        {
            var copy = NativeMethods.CopyIcon(classIcon);
            if (copy != IntPtr.Zero)
            {
                return copy;
            }
        }

        return IntPtr.Zero;
    }

    private static ImageSource? GetShellIcon(string executablePath)
    {
        var info = new NativeMethods.SHFILEINFO();
        var result = NativeMethods.SHGetFileInfo(
            executablePath,
            0,
            ref info,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
            NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);

        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        return ConvertIcon(info.hIcon, destroyOriginal: true);
    }

    private static ImageSource? ConvertIcon(IntPtr hIcon, bool destroyOriginal)
    {
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (destroyOriginal && hIcon != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(hIcon);
            }
        }
    }
}
