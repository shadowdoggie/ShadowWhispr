using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShadowWhispr.Services;

public enum HoldHotkey
{
    RightCtrl,
    RightAlt,
    CtrlSpace,
    CtrlShiftSpace,
    AltSpace,
    F8,
    F9
}

/// <summary>
/// Detects both edges of a system-wide push-to-talk hotkey without requiring
/// the ShadowWhispr window to have focus.
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
    private bool _isHeld;
    private bool _disposed;
    private HoldHotkey _hotkey;

    public GlobalHotkeyService(HoldHotkey hotkey = HoldHotkey.RightCtrl)
    {
        _hotkey = hotkey;
        _hookProc = KeyboardHookCallback;
    }

    public event EventHandler? Pressed;
    public event EventHandler? Released;

    /// <summary>
    /// The configured push-to-talk key. It may be changed while the hook is running.
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
            bool raiseReleased;
            lock (_gate)
            {
                if (_hotkey == value)
                {
                    return;
                }

                _hotkey = value;
                raiseReleased = _isHeld;
                _isHeld = false;
                _keysDown.Clear();
            }

            if (raiseReleased)
            {
                RaiseOnEventContext(Released);
            }
        }
    }

    public bool IsHeld
    {
        get
        {
            lock (_gate)
            {
                return _isHeld;
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
        bool raiseReleased;

        lock (_gate)
        {
            thread = _hookThread;
            threadId = _hookThreadId;
            if (thread is null)
            {
                return;
            }

            raiseReleased = _isHeld;
            _isHeld = false;
            _keysDown.Clear();
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

        if (raiseReleased)
        {
            RaiseOnEventContext(Released);
        }
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
        bool raisePressed = false;
        bool raiseReleased = false;
        bool suppress;

        lock (_gate)
        {
            bool wasHeld = _isHeld;
            if (isDown)
            {
                _keysDown.Add(key);
            }
            else
            {
                _keysDown.Remove(key);
            }

            _isHeld = IsConfiguredHotkeyDown(_hotkey);
            raisePressed = !wasHeld && _isHeld;
            raiseReleased = wasHeld && !_isHeld;
            suppress = SuppressHotkey && ShouldSuppressKey(_hotkey, key, wasHeld, _isHeld);
        }

        if (raisePressed)
        {
            RaiseOnEventContext(Pressed);
        }
        else if (raiseReleased)
        {
            RaiseOnEventContext(Released);
        }

        return suppress ? 1 : CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    private bool IsConfiguredHotkeyDown(HoldHotkey hotkey) => hotkey switch
    {
        HoldHotkey.RightCtrl => IsDown(VkRControl),
        HoldHotkey.RightAlt => IsDown(VkRMenu),
        HoldHotkey.CtrlSpace => IsCtrlDown() && IsDown(VkSpace),
        HoldHotkey.CtrlShiftSpace => IsCtrlDown() && IsShiftDown() && IsDown(VkSpace),
        HoldHotkey.AltSpace => IsAltDown() && IsDown(VkSpace),
        HoldHotkey.F8 => IsDown(VkF8),
        HoldHotkey.F9 => IsDown(VkF9),
        _ => false
    };

    private static bool ShouldSuppressKey(HoldHotkey hotkey, int key, bool wasHeld, bool isHeld)
    {
        return hotkey switch
        {
            HoldHotkey.RightCtrl => key == VkRControl,
            HoldHotkey.RightAlt => key == VkRMenu,
            HoldHotkey.F8 => key == VkF8,
            HoldHotkey.F9 => key == VkF9,
            // Suppressing only Space leaves normal Ctrl/Alt/Shift shortcuts usable.
            HoldHotkey.CtrlSpace or HoldHotkey.CtrlShiftSpace or HoldHotkey.AltSpace =>
                key == VkSpace && (wasHeld || isHeld),
            _ => false
        };
    }

    private bool IsDown(int key) => _keysDown.Contains(key);
    private bool IsCtrlDown() => IsDown(VkControl) || IsDown(VkLControl) || IsDown(VkRControl);
    private bool IsAltDown() => IsDown(VkMenu) || IsDown(VkLMenu) || IsDown(VkRMenu);
    private bool IsShiftDown() => IsDown(VkShift) || IsDown(VkLShift) || IsDown(VkRShift);

    private void RaiseOnEventContext(EventHandler? handler)
    {
        if (handler is null)
        {
            return;
        }

        SynchronizationContext? context = _eventContext;
        if (context is null)
        {
            handler(this, EventArgs.Empty);
            return;
        }

        context.Post(static state =>
        {
            var (sender, callback) = ((GlobalHotkeyService, EventHandler))state!;
            callback(sender, EventArgs.Empty);
        }, (this, handler));
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
