using System.Diagnostics;
using System.Text.RegularExpressions;
using ShadowWhispr.Services;

namespace ShadowWhispr.Linux.Services;

/// <summary>The outcome of listening for a replacement hotkey.</summary>
public sealed record HotkeyCaptureResult(HoldHotkey? Hotkey, bool Cleared, bool Cancelled);

/// <summary>
/// Detects both edges of the system-wide push-to-talk hotkeys by reading the
/// keyboard devices under /dev/input directly. This works identically under
/// X11 and Wayland (including GNOME, which offers no other way to watch a
/// bare modifier like Right Ctrl), and needs the user to be in the
/// <c>input</c> group.
///
/// The hold-or-tap semantics mirror the Windows GlobalHotkeyService exactly:
/// key-down reports Pressed; a release after <see cref="TapThreshold"/>
/// reports Released; a quicker release latches the recording on until the next
/// press. The one difference is that keypresses cannot be swallowed here, so
/// the hotkey also reaches the focused app — harmless for the default
/// Right Ctrl, worth knowing for letter keys.
/// </summary>
public sealed partial class LinuxHotkeyService : IDisposable
{
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkShift = 0x10;
    private const int VkLControl = 0xA2;
    private const int VkRControl = 0xA3;
    private const int VkLMenu = 0xA4;
    private const int VkRMenu = 0xA5;
    private const int VkLShift = 0xA0;
    private const int VkRShift = 0xA1;

    private readonly object _gate = new();
    private readonly HashSet<int> _keysDown = [];
    private readonly Dictionary<string, CancellationTokenSource> _readers = [];
    private CancellationTokenSource? _lifetime;
    private SynchronizationContext? _eventContext;
    private Timer? _rescanTimer;
    private HotkeyKind? _heldKind;
    private HotkeyKind? _latchedKind;
    private long _heldSince;
    private bool _disposed;
    private HoldHotkey? _hotkey;
    private HoldHotkey? _rawHotkey;
    private bool _enabled = true;
    private TaskCompletionSource<HotkeyCaptureResult>? _capture;

    public LinuxHotkeyService(HoldHotkey? hotkey = null)
    {
        _hotkey = hotkey ?? HoldHotkey.Default;
    }

    public event EventHandler<HotkeyEventArgs>? Pressed;
    public event EventHandler<HotkeyEventArgs>? Released;

    /// <summary>
    /// Raised when a press turned out to be a tap, so the recording stays on
    /// until the next press instead of ending with the key going up.
    /// </summary>
    public event EventHandler<HotkeyEventArgs>? Latched;

    public TimeSpan TapThreshold { get; set; } = TimeSpan.FromMilliseconds(500);

    public HoldHotkey? Hotkey
    {
        get { lock (_gate) return _hotkey; }
        set
        {
            ReleasedKinds released;
            lock (_gate)
            {
                if (_hotkey == value) return;
                _hotkey = value;
                released = ResetHeldState();
            }
            RaiseReleased(released);
        }
    }

    public HoldHotkey? RawHotkey
    {
        get { lock (_gate) return _rawHotkey; }
        set
        {
            ReleasedKinds released;
            lock (_gate)
            {
                if (_rawHotkey == value) return;
                _rawHotkey = value;
                released = ResetHeldState();
            }
            RaiseReleased(released);
        }
    }

    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
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
            lock (_gate) return _heldKind is not null || _latchedKind is not null;
        }
    }

    private readonly record struct ReleasedKinds(HotkeyKind? Held, HotkeyKind? Latched);

    private ReleasedKinds ResetHeldState()
    {
        var released = new ReleasedKinds(_heldKind, _latchedKind);
        _heldKind = null;
        _latchedKind = null;
        _keysDown.Clear();
        return released;
    }

    /// <summary>
    /// Opens every keyboard under /dev/input and starts reading. Throws with a
    /// plain-English explanation when none could be opened, which on a fresh
    /// machine means the user is not in the input group yet.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_lifetime is not null) return;
            _eventContext = SynchronizationContext.Current;
            _lifetime = new CancellationTokenSource();
        }

        int opened = ScanForKeyboards();
        if (opened == 0)
        {
            lock (_gate)
            {
                _lifetime?.Cancel();
                _lifetime = null;
            }
            throw new InvalidOperationException(
                "No keyboard could be opened under /dev/input. Add yourself to the " +
                "'input' group (sudo usermod -aG input $USER), then log out and back in.");
        }

        // Keyboards plugged in later are picked up on a slow rescan.
        _rescanTimer = new Timer(_ => TryRescan(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        AppLog.Write($"Global hotkey listener started on {opened} keyboard device(s)");
    }

    public void Stop()
    {
        CancellationTokenSource? lifetime;
        ReleasedKinds released;
        lock (_gate)
        {
            lifetime = _lifetime;
            _lifetime = null;
            released = ResetHeldState();
            foreach (var reader in _readers.Values) reader.Cancel();
            _readers.Clear();
        }

        _rescanTimer?.Dispose();
        _rescanTimer = null;
        lifetime?.Cancel();
        RaiseReleased(released);
    }

    /// <summary>
    /// Listens for the next key the user presses and reports it as a binding,
    /// with the same rules as the Windows capture UI: Escape cancels, Delete or
    /// Backspace clears, a modifier alone binds on its release, anything else
    /// binds on its press together with whatever modifiers are held.
    /// Matching is disabled while capture is active.
    /// </summary>
    public Task<HotkeyCaptureResult> CaptureNextAsync(CancellationToken cancellationToken = default)
    {
        TaskCompletionSource<HotkeyCaptureResult> capture;
        lock (_gate)
        {
            _capture?.TrySetResult(new HotkeyCaptureResult(null, false, true));
            capture = new TaskCompletionSource<HotkeyCaptureResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            _capture = capture;
            ResetHeldState();
        }

        cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                if (ReferenceEquals(_capture, capture)) _capture = null;
            }
            capture.TrySetResult(new HotkeyCaptureResult(null, false, true));
        });
        return capture.Task;
    }

    public void CancelCapture()
    {
        TaskCompletionSource<HotkeyCaptureResult>? capture;
        lock (_gate)
        {
            capture = _capture;
            _capture = null;
        }
        capture?.TrySetResult(new HotkeyCaptureResult(null, false, true));
    }

    private void TryRescan()
    {
        try
        {
            ScanForKeyboards();
        }
        catch (Exception exception)
        {
            AppLog.Write("Rescanning input devices failed", exception);
        }
    }

    /// <summary>
    /// Finds keyboard devices via /proc/bus/input/devices (the handlers line
    /// names both the eventN node and whether the kernel considers it a kbd)
    /// and starts a reader for each new one. Returns how many are being read.
    /// </summary>
    private int ScanForKeyboards()
    {
        List<string> devices = [];
        try
        {
            var text = File.ReadAllText("/proc/bus/input/devices");
            foreach (var block in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                if (!block.Contains("kbd", StringComparison.Ordinal)) continue;

                // EV bitmask bit 1 = EV_KEY; a "keyboard" without it (or with a
                // near-empty KEY map, like a power button) is not worth reading.
                var ev = EvLineRegex().Match(block);
                if (!ev.Success ||
                    !ulong.TryParse(ev.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var evBits) ||
                    (evBits & 0x2) == 0)
                    continue;
                var key = KeyLineRegex().Match(block);
                if (!key.Success || key.Groups[1].Value.Replace(" ", "").TrimStart('0').Length < 8) continue;

                var handler = EventHandlerRegex().Match(block);
                if (handler.Success) devices.Add($"/dev/input/{handler.Groups[1].Value}");
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("Could not enumerate input devices", exception);
            return 0;
        }

        int active;
        lock (_gate)
        {
            if (_lifetime is null) return 0;
            foreach (var device in devices)
            {
                if (_readers.ContainsKey(device)) continue;
                var readerCancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                if (TryOpenDevice(device, out var stream))
                {
                    _readers[device] = readerCancel;
                    _ = Task.Run(() => ReadDeviceAsync(device, stream!, readerCancel.Token));
                }
                else
                {
                    readerCancel.Dispose();
                }
            }
            active = _readers.Count;
        }
        return active;
    }

    private static bool TryOpenDevice(string path, out FileStream? stream)
    {
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096,
                FileOptions.Asynchronous);
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Write($"Could not open {path} for hotkey listening: {exception.Message}");
            stream = null;
            return false;
        }
    }

    private async Task ReadDeviceAsync(string path, FileStream stream, CancellationToken cancellationToken)
    {
        // struct input_event on 64-bit: 16-byte timeval + type + code + value.
        var buffer = new byte[24];
        try
        {
            using (stream)
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int read = 0;
                    while (read < buffer.Length)
                    {
                        int chunk = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken);
                        if (chunk == 0) return; // device unplugged
                        read += chunk;
                    }

                    var type = BitConverter.ToUInt16(buffer, 16);
                    var code = BitConverter.ToUInt16(buffer, 18);
                    var value = BitConverter.ToInt32(buffer, 20);
                    if (type != 1) continue; // EV_KEY only

                    int vk = EvdevKeys.ToVirtualKey(code);
                    if (vk == 0) continue;

                    // value: 0 = up, 1 = down, 2 = auto-repeat (treated as a
                    // repeated down, same as the Windows hook sees it).
                    HandleKey(vk, isDown: value is 1 or 2, isUp: value == 0);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            AppLog.Write($"Hotkey reader for {path} stopped", exception);
        }
        finally
        {
            lock (_gate)
            {
                if (_readers.Remove(path, out var readerCancel)) readerCancel.Dispose();
            }
        }
    }

    /// <summary>
    /// Decides what one key going down or up means and raises the matching
    /// events. Semantics match GlobalHotkeyService.HandleKey on Windows, minus
    /// suppression (impossible without grabbing the whole keyboard).
    /// </summary>
    internal void HandleKey(int key, bool isDown, bool isUp)
    {
        HotkeyKind? pressedKind = null;
        HotkeyKind? releasedKind = null;
        HotkeyKind? latchedKind = null;
        TaskCompletionSource<HotkeyCaptureResult>? captureDone = null;
        HotkeyCaptureResult? captureResult = null;

        lock (_gate)
        {
            var wasHeld = _heldKind;
            bool isRepeat = isDown && !_keysDown.Add(key);
            if (isUp) _keysDown.Remove(key);

            if (_capture is not null)
            {
                var result = EvaluateCapture(key, isDown, isRepeat);
                if (result is not null)
                {
                    captureDone = _capture;
                    _capture = null;
                    captureResult = result;
                    ResetHeldState();
                }
            }
            else
            {
                HotkeyKind? match = _enabled ? MatchHeldHotkey() : null;

                if (_latchedKind is HotkeyKind latched)
                {
                    // A tap recording is running: it ignores the keys going up
                    // and down, and ends on the next fresh press of either binding.
                    _heldKind = null;
                    if (isDown && !isRepeat && match is not null)
                    {
                        _latchedKind = null;
                        releasedKind = latched;
                    }
                }
                else
                {
                    _heldKind = match;

                    if (wasHeld != _heldKind)
                    {
                        if (wasHeld is HotkeyKind ending)
                        {
                            if (_heldKind is null && Stopwatch.GetElapsedTime(_heldSince) < TapThreshold)
                            {
                                _latchedKind = ending;
                                latchedKind = ending;
                            }
                            else
                            {
                                releasedKind = ending;
                            }
                        }

                        if (_heldKind is HotkeyKind starting)
                        {
                            pressedKind = starting;
                            _heldSince = Stopwatch.GetTimestamp();
                        }
                    }
                }
            }
        }

        if (captureDone is not null && captureResult is not null)
        {
            RaiseOnEventContext(() => captureDone.TrySetResult(captureResult));
        }
        if (releasedKind is not null) RaiseEvent(Released, releasedKind.Value);
        if (latchedKind is not null) RaiseEvent(Latched, latchedKind.Value);
        if (pressedKind is not null) RaiseEvent(Pressed, pressedKind.Value);
    }

    /// <summary>Capture-mode interpretation of one key event. Callers hold the gate.</summary>
    private HotkeyCaptureResult? EvaluateCapture(int key, bool isDown, bool isRepeat)
    {
        bool isModifier = key is VkLControl or VkRControl or VkLShift or VkRShift
            or VkLMenu or VkRMenu or 0x5B or 0x5C or VkControl or VkShift or VkMenu;

        if (isDown && !isRepeat)
        {
            if (key == 0x1B) return new HotkeyCaptureResult(null, false, true);
            if (key is 0x2E or 0x08) return new HotkeyCaptureResult(null, true, false);
            if (!isModifier)
            {
                return new HotkeyCaptureResult(HoldHotkey.FromVirtualKey(
                    key,
                    ctrl: IsCtrlDown(),
                    shift: IsShiftDown(),
                    alt: IsAltDown(),
                    win: IsWinDown()), false, false);
            }
        }
        else if (!isDown && isModifier && key is not (VkControl or VkShift or VkMenu))
        {
            // A modifier pressed and released on its own becomes the binding,
            // which is how "Right Ctrl" alone is chosen.
            return new HotkeyCaptureResult(HoldHotkey.FromVirtualKey(key), false, false);
        }

        return null;
    }

    private HotkeyKind? MatchHeldHotkey()
    {
        var raw = _rawHotkey;
        var primary = _hotkey;
        bool primaryDown = primary is not null && IsConfiguredHotkeyDown(primary.Value);
        bool rawDown = raw is not null && raw.Value != primary && IsConfiguredHotkeyDown(raw.Value);

        if (primaryDown && rawDown)
        {
            return ModifierCount(raw!.Value.Modifiers) > ModifierCount(primary!.Value.Modifiers)
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
        if (released.Held is not null) RaiseEvent(Released, released.Held.Value);
        if (released.Latched is not null) RaiseEvent(Released, released.Latched.Value);
    }

    private void RaiseEvent(EventHandler<HotkeyEventArgs>? handler, HotkeyKind kind)
    {
        if (handler is null) return;
        RaiseOnEventContext(() => handler(this, new HotkeyEventArgs(kind)));
    }

    private void RaiseOnEventContext(Action action)
    {
        var context = _eventContext;
        if (context is null)
        {
            action();
            return;
        }
        context.Post(static state => ((Action)state!)(), action);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    [GeneratedRegex(@"^B:\s*EV=([0-9a-fA-F]+)", RegexOptions.Multiline)]
    private static partial Regex EvLineRegex();

    [GeneratedRegex(@"^B:\s*KEY=([0-9a-f ]+)", RegexOptions.Multiline)]
    private static partial Regex KeyLineRegex();

    [GeneratedRegex(@"^H:\s*Handlers=.*?\b(event\d+)", RegexOptions.Multiline)]
    private static partial Regex EventHandlerRegex();
}
