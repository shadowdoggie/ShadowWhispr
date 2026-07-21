using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

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
    Raw,

    /// <summary>
    /// The optional tap hotkey: one press starts recording, the next press stops
    /// it. Treated like <see cref="Primary"/> once the recording ends.
    /// </summary>
    Toggle,

    /// <summary>
    /// The optional raw tap hotkey: starts and stops like <see cref="Toggle"/>,
    /// but the transcript skips AI cleanup like <see cref="Raw"/>.
    /// </summary>
    ToggleRaw
}

public sealed class HotkeyEventArgs(HotkeyKind kind) : EventArgs
{
    public HotkeyKind Kind { get; } = kind;
}

/// <summary>
/// Detects both edges of the system-wide push-to-talk hotkeys without requiring
/// the ShadowWhispr window to have focus. All bindings share one hook so that a
/// chord and its plain key can never both fire for one keypress. The two hold
/// bindings report key-down as Pressed and key-up as Released; the optional tap
/// binding reports its first press as Pressed and the next press as Released.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmQuit = 0x0012;
    private const uint LlkhfInjected = 0x00000010;

    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkShift = 0x10;
    private const int VkSpace = 0x20;
    private const int VkF8 = 0x77;
    private const int VkF9 = 0x78;
    private const int VkLControl = 0xA2;
    private const int VkRControl = 0xA3;
    private const int VkLMenu = 0xA4;
    private const int VkRMenu = 0xA5;
    private const int VkLShift = 0xA0;
    private const int VkRShift = 0xA1;

    private readonly object _gate = new();
    private readonly HashSet<int> _keysDown = [];
    private readonly LowLevelKeyboardProc _hookProc;
    private Thread? _hookThread;
    private TaskCompletionSource<object?>? _started;
    private SynchronizationContext? _eventContext;
    private nint _hookHandle;
    private uint _hookThreadId;
    private HotkeyKind? _heldKind;
    private HotkeyKind? _activeToggle;
    private int _suppressedActivationKey;
    private bool _disposed;
    private HoldHotkey _hotkey;
    private HoldHotkey? _rawHotkey;
    private HoldHotkey? _toggleHotkey;
    private HoldHotkey? _toggleRawHotkey;
    private bool _enabled = true;

    public GlobalHotkeyService(HoldHotkey? hotkey = null)
    {
        _hotkey = hotkey ?? HoldHotkey.Default;
        _hookProc = KeyboardHookCallback;
    }

    public event EventHandler<HotkeyEventArgs>? Pressed;
    public event EventHandler<HotkeyEventArgs>? Released;

    /// <summary>
    /// The main push-to-talk key. It may be changed while the hook is running.
    /// </summary>
    public HoldHotkey Hotkey
    {
        get
        {
            lock (_gate)
            {
                return _hotkey;
            }
        }
        set
        {
            ReleasedKinds released;
            lock (_gate)
            {
                if (_hotkey == value)
                {
                    return;
                }

                _hotkey = value;
                released = ResetHeldState();
            }

            RaiseReleased(released);
        }
    }

    /// <summary>
    /// The optional second push-to-talk key that skips AI cleanup. Null disables
    /// it. A binding identical to <see cref="Hotkey"/> is ignored, because one
    /// keypress must never mean two different things.
    /// </summary>
    public HoldHotkey? RawHotkey
    {
        get
        {
            lock (_gate)
            {
                return _rawHotkey;
            }
        }
        set
        {
            ReleasedKinds released;
            lock (_gate)
            {
                if (_rawHotkey == value)
                {
                    return;
                }

                _rawHotkey = value;
                released = ResetHeldState();
            }

            RaiseReleased(released);
        }
    }

    /// <summary>
    /// The optional tap hotkey: one press starts recording, the next press
    /// stops it. Null disables it. A binding identical to any other configured
    /// hotkey is ignored, because one keypress must never mean two things.
    /// </summary>
    public HoldHotkey? ToggleHotkey
    {
        get
        {
            lock (_gate)
            {
                return _toggleHotkey;
            }
        }
        set
        {
            ReleasedKinds released;
            lock (_gate)
            {
                if (_toggleHotkey == value)
                {
                    return;
                }

                _toggleHotkey = value;
                released = ResetHeldState();
            }

            RaiseReleased(released);
        }
    }

    /// <summary>
    /// The optional raw tap hotkey: taps like <see cref="ToggleHotkey"/>, but
    /// the transcript skips AI cleanup. Null disables it.
    /// </summary>
    public HoldHotkey? ToggleRawHotkey
    {
        get
        {
            lock (_gate)
            {
                return _toggleRawHotkey;
            }
        }
        set
        {
            ReleasedKinds released;
            lock (_gate)
            {
                if (_toggleRawHotkey == value)
                {
                    return;
                }

                _toggleRawHotkey = value;
                released = ResetHeldState();
            }

            RaiseReleased(released);
        }
    }

    /// <summary>The dictations a call to <see cref="ResetHeldState"/> cut short.</summary>
    private readonly record struct ReleasedKinds(HotkeyKind? Held, HotkeyKind? ActiveToggle);

    /// <summary>
    /// Clears any in-progress hold and an active tap recording. Callers must
    /// hold <see cref="_gate"/>, and must raise the returned kinds (if any)
    /// outside the lock.
    /// </summary>
    private ReleasedKinds ResetHeldState()
    {
        var released = new ReleasedKinds(_heldKind, _activeToggle);
        _heldKind = null;
        _activeToggle = null;
        _suppressedActivationKey = 0;
        _keysDown.Clear();
        return released;
    }

    /// <summary>
    /// Temporarily disables matching and suppression, for example while the
    /// settings UI is listening for a replacement hotkey.
    /// </summary>
    public bool Enabled
    {
        get
        {
            lock (_gate) return _enabled;
        }
        set
        {
            ReleasedKinds released;
            lock (_gate)
            {
                if (_enabled == value) return;
                _enabled = value;
                released = ResetHeldState();
            }

            RaiseReleased(released);
        }
    }

    public bool IsHeld
    {
        get
        {
            lock (_gate)
            {
                return _heldKind is not null;
            }
        }
    }

    /// <summary>
    /// Prevents the activation key (or Space for a chord) from reaching the
    /// foreground application while dictation is active.
    /// </summary>
    public bool SuppressHotkey { get; set; } = true;

    public void Start()
    {
        ThrowIfDisposed();

        Task startedTask;
        lock (_gate)
        {
            if (_hookThread is not null)
            {
                return;
            }

            _eventContext = SynchronizationContext.Current;
            _started = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            startedTask = _started.Task;
            _hookThread = new Thread(HookThreadMain)
            {
                IsBackground = true,
                Name = "ShadowWhispr global hotkey"
            };
            _hookThread.Start();
        }

        // Hook installation is fast, and surfacing failure here is much easier
        // for callers to handle than an exception lost on a background thread.
        startedTask.GetAwaiter().GetResult();
    }

    public void Stop()
    {
        Thread? thread;
        uint threadId;
        ReleasedKinds released;

        lock (_gate)
        {
            thread = _hookThread;
            threadId = _hookThreadId;
            if (thread is null)
            {
                return;
            }

            released = ResetHeldState();
        }

        if (threadId != 0)
        {
            PostThreadMessage(threadId, WmQuit, 0, 0);
        }

        if (thread != Thread.CurrentThread)
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }

        lock (_gate)
        {
            _hookThread = null;
            _hookThreadId = 0;
            _started = null;
        }

        RaiseReleased(released);
    }

    private void HookThreadMain()
    {
        try
        {
            _hookThreadId = GetCurrentThreadId();
            nint module = GetModuleHandle(null);
            _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookProc, module, 0);
            if (_hookHandle == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the global keyboard hook.");
            }

            _started?.TrySetResult(null);

            while (GetMessage(out Message message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            // If startup already completed, this exception would otherwise be
            // lost with the background thread — the log is its only trace.
            AppLog.Write("Global hotkey hook thread failed", ex);
            _started?.TrySetException(ex);
        }
        finally
        {
            if (_hookHandle != 0)
            {
                UnhookWindowsHookEx(_hookHandle);
                _hookHandle = 0;
            }
        }
    }

    private nint KeyboardHookCallback(int code, nuint wParam, nint lParam)
    {
        if (code < 0)
        {
            return CallNextHookEx(_hookHandle, code, wParam, lParam);
        }

        int message = unchecked((int)wParam);
        bool isDown = message is WmKeyDown or WmSysKeyDown;
        bool isUp = message is WmKeyUp or WmSysKeyUp;
        if (!isDown && !isUp)
        {
            return CallNextHookEx(_hookHandle, code, wParam, lParam);
        }

        KeyboardHookData data = Marshal.PtrToStructure<KeyboardHookData>(lParam);
        if ((data.Flags & LlkhfInjected) != 0)
        {
            return CallNextHookEx(_hookHandle, code, wParam, lParam);
        }

        int key = unchecked((int)data.VirtualKeyCode);
        HotkeyKind? pressedKind;
        HotkeyKind? releasedKind;
        bool suppress;

        lock (_gate)
        {
            var wasHeld = _heldKind;
            bool isRepeat = isDown && !_keysDown.Add(key);
            if (isUp)
            {
                _keysDown.Remove(key);
            }

            _heldKind = _enabled ? MatchHeldHotkey() : null;

            // A tap hotkey flips on a fresh key-down, never on auto-repeat,
            // and stays out of the way while a hold dictation is in progress.
            bool toggledOn = false;
            HotkeyKind? toggledOff = null;
            bool swallowedToggleKey = false;
            if (_enabled && isDown && !isRepeat && wasHeld is null &&
                MatchToggleKeyDown(key) is HotkeyKind tapKind)
            {
                var binding = tapKind == HotkeyKind.ToggleRaw
                    ? _toggleRawHotkey!.Value
                    : _toggleHotkey!.Value;

                // A keypress that satisfies both a tap binding and a hold
                // binding goes to whichever names more modifiers, matching how
                // the two hold bindings resolve their own overlaps.
                bool holdClaims = _heldKind is HotkeyKind held &&
                    ModifierCount(BindingFor(held).Modifiers) >= ModifierCount(binding.Modifiers);
                if (!holdClaims)
                {
                    if (_activeToggle is null)
                    {
                        _heldKind = null;
                        _activeToggle = tapKind;
                        toggledOn = true;
                    }
                    else if (_activeToggle == tapKind)
                    {
                        _heldKind = null;
                        _activeToggle = null;
                        toggledOff = tapKind;
                    }
                    else
                    {
                        // The other tap recording is running: this key does
                        // nothing, but it must not leak into the target app.
                        _heldKind = null;
                        swallowedToggleKey = true;
                    }
                }
            }

            // A switch straight from one binding to the other (possible when the
            // two share a key) has to close the first hold before opening the
            // second, or the app would see two presses and never a release.
            releasedKind = wasHeld != _heldKind ? wasHeld : null;
            pressedKind = wasHeld != _heldKind ? _heldKind : null;
            if (toggledOn)
            {
                // wasHeld and _heldKind are both null here, so the tap event
                // never competes with a hold event for the same keypress.
                pressedKind = _activeToggle;
            }
            else if (toggledOff is not null)
            {
                releasedKind = toggledOff;
            }

            suppress = false;
            if (_enabled && SuppressHotkey)
            {
                if (isDown && (toggledOn || toggledOff is not null || swallowedToggleKey))
                {
                    _suppressedActivationKey = key;
                    suppress = true;
                }
                else if (isDown && _heldKind is not null && key == ActivationKeyFor(_heldKind.Value))
                {
                    _suppressedActivationKey = key;
                    suppress = true;
                }
                else if (isDown && wasHeld is not null && key == ActivationKeyFor(wasHeld.Value))
                {
                    // Auto-repeat while the key is already held down.
                    _suppressedActivationKey = key;
                    suppress = true;
                }
                else if (isDown && isRepeat && key != 0 && key == _suppressedActivationKey)
                {
                    // Auto-repeat of a suppressed tap key still physically down.
                    suppress = true;
                }
                else if (isUp && _suppressedActivationKey == key && key != 0)
                {
                    _suppressedActivationKey = 0;
                    suppress = true;
                }
            }
        }

        if (releasedKind is not null)
        {
            RaiseOnEventContext(Released, releasedKind.Value);
        }
        if (pressedKind is not null)
        {
            RaiseOnEventContext(Pressed, pressedKind.Value);
        }

        return suppress ? 1 : CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    private int ActivationKeyFor(HotkeyKind kind) =>
        kind == HotkeyKind.Raw ? _rawHotkey?.VirtualKey ?? 0 : _hotkey.VirtualKey;

    /// <summary>The hold binding behind a held kind. Callers must hold <see cref="_gate"/>.</summary>
    private HoldHotkey BindingFor(HotkeyKind kind) =>
        kind == HotkeyKind.Raw ? _rawHotkey.GetValueOrDefault() : _hotkey;

    /// <summary>
    /// Decides which tap binding this key-down satisfies, if any. Mirrors
    /// <see cref="MatchHeldHotkey"/>: a binding duplicating a hold hotkey is
    /// ignored, and when both tap bindings match one keypress the one naming
    /// more modifiers wins. Callers must hold <see cref="_gate"/>.
    /// </summary>
    private HotkeyKind? MatchToggleKeyDown(int key)
    {
        var tap = _toggleHotkey;
        var tapRaw = _toggleRawHotkey;
        bool tapMatch = tap is HoldHotkey a &&
            a != _hotkey && a != _rawHotkey &&
            key == a.VirtualKey && IsConfiguredHotkeyDown(a);
        bool tapRawMatch = tapRaw is HoldHotkey b &&
            b != _hotkey && b != _rawHotkey && b != tap &&
            key == b.VirtualKey && IsConfiguredHotkeyDown(b);

        if (tapMatch && tapRawMatch)
        {
            return ModifierCount(tapRaw!.Value.Modifiers) > ModifierCount(tap!.Value.Modifiers)
                ? HotkeyKind.ToggleRaw
                : HotkeyKind.Toggle;
        }
        if (tapMatch) return HotkeyKind.Toggle;
        if (tapRawMatch) return HotkeyKind.ToggleRaw;
        return null;
    }

    /// <summary>
    /// Decides which binding the currently pressed keys satisfy. The binding
    /// with more modifiers wins, so configuring "X" and "Ctrl + X" resolves to
    /// the chord rather than firing whichever happens to be checked first.
    /// Callers must hold <see cref="_gate"/>.
    /// </summary>
    private HotkeyKind? MatchHeldHotkey()
    {
        var raw = _rawHotkey;
        bool primaryDown = IsConfiguredHotkeyDown(_hotkey);
        bool rawDown = raw is not null && raw.Value != _hotkey && IsConfiguredHotkeyDown(raw.Value);

        if (primaryDown && rawDown)
        {
            return ModifierCount(raw!.Value.Modifiers) > ModifierCount(_hotkey.Modifiers)
                ? HotkeyKind.Raw
                : HotkeyKind.Primary;
        }
        if (primaryDown) return HotkeyKind.Primary;
        if (rawDown) return HotkeyKind.Raw;
        return null;
    }

    private static int ModifierCount(HotkeyModifiers modifiers) =>
        System.Numerics.BitOperations.PopCount((uint)modifiers);

    private bool IsConfiguredHotkeyDown(HoldHotkey hotkey) =>
        IsDown(hotkey.VirtualKey) &&
        (!hotkey.Modifiers.HasFlag(HotkeyModifiers.Ctrl) || IsCtrlDown()) &&
        (!hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift) || IsShiftDown()) &&
        (!hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt) || IsAltDown()) &&
        (!hotkey.Modifiers.HasFlag(HotkeyModifiers.Win) || IsWinDown());

    private bool IsDown(int key) => _keysDown.Contains(key);
    private bool IsCtrlDown() => IsDown(VkControl) || IsDown(VkLControl) || IsDown(VkRControl);
    private bool IsAltDown() => IsDown(VkMenu) || IsDown(VkLMenu) || IsDown(VkRMenu);
    private bool IsShiftDown() => IsDown(VkShift) || IsDown(VkLShift) || IsDown(VkRShift);
    private bool IsWinDown() => IsDown(0x5B) || IsDown(0x5C);

    private void RaiseReleased(ReleasedKinds released)
    {
        if (released.Held is not null)
        {
            RaiseOnEventContext(Released, released.Held.Value);
        }
        if (released.ActiveToggle is not null)
        {
            RaiseOnEventContext(Released, released.ActiveToggle.Value);
        }
    }

    private void RaiseOnEventContext(EventHandler<HotkeyEventArgs>? handler, HotkeyKind kind)
    {
        if (handler is null)
        {
            return;
        }

        var args = new HotkeyEventArgs(kind);
        SynchronizationContext? context = _eventContext;
        if (context is null)
        {
            handler(this, args);
            return;
        }

        context.Post(static state =>
        {
            var (sender, callback, eventArgs) = ((GlobalHotkeyService, EventHandler<HotkeyEventArgs>, HotkeyEventArgs))state!;
            callback(sender, eventArgs);
        }, (this, handler, args));
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Stop();
        _disposed = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct KeyboardHookData
    {
        public readonly uint VirtualKeyCode;
        public readonly uint ScanCode;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
        public uint Private;
    }

    private delegate nint LowLevelKeyboardProc(int code, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hookId, LowLevelKeyboardProc callback, nint module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, nint window, uint minMessage, uint maxMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Message message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
