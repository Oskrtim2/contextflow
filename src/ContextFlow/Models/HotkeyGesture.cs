using System.Windows.Input;

namespace ContextFlow.Models;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Win = 8
}

/// <summary>
/// A configurable global shortcut. The string form is persisted in settings,
/// for example "Ctrl+E" or "Alt+Left".
/// </summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, int VirtualKey)
{
    public static HotkeyGesture DefaultScrollUp { get; } =
        new(HotkeyModifiers.Control, 0x45);

    public static HotkeyGesture DefaultScrollDown { get; } =
        new(HotkeyModifiers.Control, 0x46);

    public bool IsValid => Modifiers != HotkeyModifiers.None && VirtualKey > 0;

    public string Display
    {
        get
        {
            if (!IsValid)
            {
                return "—";
            }

            var parts = new List<string>(5);
            if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
            var key = KeyInterop.KeyFromVirtualKey(VirtualKey);
            parts.Add(key == Key.None ? $"VK{VirtualKey:X2}" : key.ToString());
            return string.Join("+", parts);
        }
    }

    public static HotkeyGesture From(Key key, ModifierKeys modifiers, bool windows = false)
    {
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return new HotkeyGesture(HotkeyModifiers.None, 0);
        }

        var flags = HotkeyModifiers.None;
        if ((modifiers & ModifierKeys.Control) != 0) flags |= HotkeyModifiers.Control;
        if ((modifiers & ModifierKeys.Alt) != 0) flags |= HotkeyModifiers.Alt;
        if ((modifiers & ModifierKeys.Shift) != 0) flags |= HotkeyModifiers.Shift;
        if (windows) flags |= HotkeyModifiers.Win;

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        return new HotkeyGesture(flags, virtualKey);
    }

    public static HotkeyGesture Parse(string? value, HotkeyGesture fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return fallback;
        }

        var modifiers = HotkeyModifiers.None;
        var keyToken = string.Empty;
        foreach (var part in parts)
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Control;
            }
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Alt;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Shift;
            }
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                     part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= HotkeyModifiers.Win;
            }
            else
            {
                keyToken = part;
            }
        }

        if (modifiers == HotkeyModifiers.None ||
            !Enum.TryParse<Key>(keyToken, true, out var key) ||
            key == Key.None)
        {
            return fallback;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        return virtualKey > 0
            ? new HotkeyGesture(modifiers, virtualKey)
            : fallback;
    }
}
