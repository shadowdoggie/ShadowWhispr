using System.Runtime.InteropServices;
using ShadowWhispr.Services;

namespace ShadowWhispr.Linux.Services;

/// <summary>
/// A minimal virtual keyboard created through /dev/uinput, used only to send
/// the Shift+Insert paste chord. Injecting at the kernel level works on every
/// compositor — GNOME Wayland included, which accepts no other outside input.
/// Shift+Insert rather than Ctrl+V because terminal emulators handle
/// Shift+Insert themselves: the application in the terminal receives pasted
/// text instead of a keypress, so TUIs that bind Ctrl+V to something else
/// (Codex and other AI CLIs use it for image paste) cannot intercept it.
/// GUI toolkits (GTK, Qt, browsers, Electron) all treat Shift+Insert as an
/// ordinary paste. Writing to /dev/uinput needs the udev rule the installer
/// ships (60-shadowwhispr-uinput.rules) plus membership of the input group.
/// </summary>
public sealed class UinputKeyboard : IDisposable
{
    private const ushort EvSyn = 0x00;
    private const ushort EvKey = 0x01;
    private const ushort KeyLeftShift = 42;
    private const ushort KeyInsert = 110;

    private const uint UiSetEvBit = 0x40045564;
    private const uint UiSetKeyBit = 0x40045565;
    private const uint UiDevSetup = 0x405C5503;
    private const uint UiDevCreate = 0x5501;
    private const uint UiDevDestroy = 0x5502;

    private readonly object _gate = new();
    private int _fd = -1;
    private bool _disposed;

    /// <summary>
    /// Creates the virtual device on first use and keeps it open; creating one
    /// costs the compositor a device-added round-trip, so reusing it keeps the
    /// paste instant after the first dictation.
    /// </summary>
    private void EnsureDevice()
    {
        if (_fd >= 0) return;

        int fd = open("/dev/uinput", O_WRONLY | O_NONBLOCK);
        if (fd < 0)
        {
            throw new InvalidOperationException(
                "Could not open /dev/uinput to paste text. Install ShadowWhispr's udev rule " +
                "(60-shadowwhispr-uinput.rules), make sure you are in the 'input' group, " +
                "then log out and back in.");
        }

        try
        {
            Check(ioctl(fd, UiSetEvBit, EvKey), "UI_SET_EVBIT");
            Check(ioctl(fd, UiSetKeyBit, KeyLeftShift), "UI_SET_KEYBIT shift");
            Check(ioctl(fd, UiSetKeyBit, KeyInsert), "UI_SET_KEYBIT insert");

            var setup = new UinputSetup
            {
                BusType = 0x03, // BUS_USB
                Vendor = 0x1d6b,
                Product = 0x0104,
                Version = 1,
                Name = "ShadowWhispr paste keyboard"
            };
            Check(ioctl(fd, UiDevSetup, ref setup), "UI_DEV_SETUP");
            Check(ioctl(fd, UiDevCreate, 0), "UI_DEV_CREATE");

            // The compositor needs a moment to notice the new keyboard before
            // it will route events from it to the focused window.
            Thread.Sleep(250);
            _fd = fd;
        }
        catch
        {
            close(fd);
            throw;
        }
    }

    /// <summary>Presses and releases Shift+Insert, with small gaps so no app misses an edge.</summary>
    public void SendPasteChord()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            EnsureDevice();
            WriteKey(KeyLeftShift, down: true);
            Sync();
            Thread.Sleep(15);
            WriteKey(KeyInsert, down: true);
            Sync();
            Thread.Sleep(15);
            WriteKey(KeyInsert, down: false);
            Sync();
            Thread.Sleep(15);
            WriteKey(KeyLeftShift, down: false);
            Sync();
        }
    }

    private void WriteKey(ushort code, bool down) => WriteEvent(EvKey, code, down ? 1 : 0);
    private void Sync() => WriteEvent(EvSyn, 0, 0);

    private void WriteEvent(ushort type, ushort code, int value)
    {
        var inputEvent = new InputEvent { Type = type, Code = code, Value = value };
        long written = write(_fd, ref inputEvent, (nuint)Marshal.SizeOf<InputEvent>());
        if (written != Marshal.SizeOf<InputEvent>())
        {
            throw new InvalidOperationException(
                $"Writing to the virtual keyboard failed (errno {Marshal.GetLastPInvokeError()}).");
        }
    }

    private static void Check(int result, string operation)
    {
        if (result < 0)
        {
            throw new InvalidOperationException(
                $"Setting up the virtual keyboard failed at {operation} (errno {Marshal.GetLastPInvokeError()}).");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_fd >= 0)
            {
                try
                {
                    ioctl(_fd, UiDevDestroy, 0);
                }
                catch (Exception exception)
                {
                    AppLog.Write("Destroying the virtual keyboard failed", exception);
                }
                close(_fd);
                _fd = -1;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct InputEvent
    {
        public long TimeSeconds;
        public long TimeMicroseconds;
        public ushort Type;
        public ushort Code;
        public int Value;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct UinputSetup
    {
        public ushort BusType;
        public ushort Vendor;
        public ushort Product;
        public ushort Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string Name;
        public uint FfEffectsMax;
    }

    private const int O_WRONLY = 0x1;
    private const int O_NONBLOCK = 0x800;

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, int value);

    [DllImport("libc", SetLastError = true)]
    private static extern int ioctl(int fd, uint request, ref UinputSetup setup);

    [DllImport("libc", SetLastError = true)]
    private static extern long write(int fd, ref InputEvent inputEvent, nuint count);
}
