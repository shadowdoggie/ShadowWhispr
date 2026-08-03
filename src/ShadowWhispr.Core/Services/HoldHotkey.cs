using System;
using System.Collections.Generic;

namespace ShadowWhispr.Services;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
    Win = 8
}

/// <summary>
/// A user-selected hold hotkey. The activation key is a Windows virtual-key
/// code, so extended function keys such as F13-F24 work without special cases.
/// The Linux app maps evdev keycodes onto the same virtual-key values, which
/// keeps saved settings identical across both platforms.
/// </summary>
public readonly record struct HoldHotkey(int VirtualKey, HotkeyModifiers Modifiers)
{
    public static HoldHotkey RightCtrl => new(0xA3, HotkeyModifiers.None);
    public static HoldHotkey RightAlt => new(0xA5, HotkeyModifiers.None);
    public static HoldHotkey CtrlSpace => new(0x20, HotkeyModifiers.Ctrl);
    public static HoldHotkey CtrlShiftSpace => new(0x20, HotkeyModifiers.Ctrl | HotkeyModifiers.Shift);
    public static HoldHotkey AltSpace => new(0x20, HotkeyModifiers.Alt);
    public static HoldHotkey F8 => new(0x77, HotkeyModifiers.None);
    public static HoldHotkey F9 => new(0x78, HotkeyModifiers.None);
    public static HoldHotkey Default => RightCtrl;

    public static HoldHotkey FromVirtualKey(
        int virtualKey,
        bool ctrl = false,
        bool shift = false,
        bool alt = false,
        bool win = false)
    {
        if (virtualKey is <= 0 or > 0xFF)
        {
            throw new ArgumentOutOfRangeException(nameof(virtualKey));
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        if (ctrl && virtualKey is not (0x11 or 0xA2 or 0xA3)) modifiers |= HotkeyModifiers.Ctrl;
        if (shift && virtualKey is not (0x10 or 0xA0 or 0xA1)) modifiers |= HotkeyModifiers.Shift;
        if (alt && virtualKey is not (0x12 or 0xA4 or 0xA5)) modifiers |= HotkeyModifiers.Alt;
        if (win && virtualKey is not (0x5B or 0x5C)) modifiers |= HotkeyModifiers.Win;
        return new HoldHotkey(virtualKey, modifiers);
    }

    public static bool TryParse(string? value, out HoldHotkey binding)
    {
        binding = Default;
        if (string.IsNullOrWhiteSpace(value)) return false;

        string[] parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!TryParseModifier(parts[i], out HotkeyModifiers modifier)) return false;
            modifiers |= modifier;
        }

        if (!TryParseKey(parts[^1], out int virtualKey)) return false;
        binding = new HoldHotkey(virtualKey, modifiers);
        return true;
    }

    public static HoldHotkey Parse(string value) =>
        TryParse(value, out HoldHotkey binding)
            ? binding
            : throw new FormatException($"'{value}' is not a valid hotkey.");

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(GetKeyName(VirtualKey));
        return string.Join(" + ", parts);
    }

    private static bool TryParseModifier(string value, out HotkeyModifiers modifier)
    {
        modifier = value.Trim().ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => HotkeyModifiers.Ctrl,
            "SHIFT" => HotkeyModifiers.Shift,
            "ALT" => HotkeyModifiers.Alt,
            "WIN" or "WINDOWS" => HotkeyModifiers.Win,
            _ => HotkeyModifiers.None
        };
        return modifier != HotkeyModifiers.None;
    }

    private static bool TryParseKey(string value, out int virtualKey)
    {
        string key = value.Trim().ToUpperInvariant();
        virtualKey = key switch
        {
            "SPACE" => 0x20,
            "ENTER" => 0x0D,
            "TAB" => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            "BACKSPACE" => 0x08,
            "DELETE" => 0x2E,
            "INSERT" => 0x2D,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGE UP" => 0x21,
            "PAGE DOWN" => 0x22,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "CAPS LOCK" => 0x14,
            "NUM LOCK" => 0x90,
            "SCROLL LOCK" => 0x91,
            "PAUSE" => 0x13,
            "PRINT SCREEN" => 0x2C,
            "LEFT CTRL" => 0xA2,
            "RIGHT CTRL" => 0xA3,
            "LEFT SHIFT" => 0xA0,
            "RIGHT SHIFT" => 0xA1,
            "LEFT ALT" => 0xA4,
            "RIGHT ALT" => 0xA5,
            "LEFT WIN" => 0x5B,
            "RIGHT WIN" => 0x5C,
            "NUMPAD PLUS" => 0x6B,
            "NUMPAD MINUS" => 0x6D,
            "NUMPAD MULTIPLY" => 0x6A,
            "NUMPAD DIVIDE" => 0x6F,
            "NUMPAD DECIMAL" => 0x6E,
            _ => 0
        };

        if (virtualKey != 0) return true;
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            virtualKey = key[0];
            return true;
        }
        if (key.StartsWith('F') && int.TryParse(key.AsSpan(1), out int function) && function is >= 1 and <= 24)
        {
            virtualKey = 0x6F + function;
            return true;
        }
        if (key.StartsWith("NUMPAD ", StringComparison.Ordinal) &&
            int.TryParse(key.AsSpan(7), out int numpad) && numpad is >= 0 and <= 9)
        {
            virtualKey = 0x60 + numpad;
            return true;
        }
        if (key.StartsWith("VK 0X", StringComparison.Ordinal) &&
            int.TryParse(key.AsSpan(5), System.Globalization.NumberStyles.HexNumber, null, out int raw) &&
            raw is > 0 and <= 0xFF)
        {
            virtualKey = raw;
            return true;
        }
        return false;
    }

    private static string GetKeyName(int virtualKey)
    {
        if (virtualKey is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39) return ((char)virtualKey).ToString();
        if (virtualKey is >= 0x70 and <= 0x87) return $"F{virtualKey - 0x6F}";
        if (virtualKey is >= 0x60 and <= 0x69) return $"Numpad {virtualKey - 0x60}";
        return virtualKey switch
        {
            0x20 => "Space", 0x0D => "Enter", 0x09 => "Tab", 0x1B => "Escape", 0x08 => "Backspace",
            0x2E => "Delete", 0x2D => "Insert", 0x24 => "Home", 0x23 => "End", 0x21 => "Page Up",
            0x22 => "Page Down", 0x26 => "Up", 0x28 => "Down", 0x25 => "Left", 0x27 => "Right",
            0x14 => "Caps Lock", 0x90 => "Num Lock", 0x91 => "Scroll Lock", 0x13 => "Pause",
            0x2C => "Print Screen", 0xA2 => "Left Ctrl", 0xA3 => "Right Ctrl", 0xA0 => "Left Shift",
            0xA1 => "Right Shift", 0xA4 => "Left Alt", 0xA5 => "Right Alt", 0x5B => "Left Win",
            0x5C => "Right Win", 0x6B => "Numpad Plus", 0x6D => "Numpad Minus", 0x6A => "Numpad Multiply",
            0x6F => "Numpad Divide", 0x6E => "Numpad Decimal",
            _ => $"VK 0x{virtualKey:X2}"
        };
    }
}

/// <summary>Which of the configurable dictation hotkeys fired.</summary>
public enum HotkeyKind
{
    /// <summary>The main hotkey: transcribe, then apply AI cleanup if enabled.</summary>
    Primary,

    /// <summary>The optional second hotkey: transcribe and type the raw text.</summary>
    Raw
}

public sealed class HotkeyEventArgs(HotkeyKind kind) : EventArgs
{
    public HotkeyKind Kind { get; } = kind;
}
