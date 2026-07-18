using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ShadowWhispr.Services;

public readonly record struct TextInsertionTarget(nint WindowHandle, nint FocusedControlHandle)
{
    public bool IsValid => WindowHandle != 0;
}

/// <summary>
/// Copies text to the clipboard and pastes it into the Windows field that was
/// focused when the push-to-talk key was pressed, then puts whatever the user
/// had on the clipboard back.
/// </summary>
public sealed class TextInsertionService
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;

    /// <summary>
    /// How long the pasted text must stay on the clipboard before the user's own
    /// clipboard contents are restored. The target application reads the clipboard
    /// asynchronously after Ctrl+V, so restoring too early pastes the wrong text.
    /// </summary>
    private static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(400);

    public TextInsertionTarget CaptureTarget()
    {
        nint foreground = GetForegroundWindow();
        if (foreground == 0)
        {
            throw new InvalidOperationException("Windows does not currently have a foreground window.");
        }

        uint threadId = GetWindowThreadProcessId(foreground, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        nint focusedControl = GetGuiThreadInfo(threadId, ref info) ? info.Focus : 0;
        return new TextInsertionTarget(foreground, focusedControl);
    }

    public bool TryCaptureTarget(out TextInsertionTarget target)
    {
        try
        {
            target = CaptureTarget();
            return true;
        }
        catch
        {
            target = default;
            return false;
        }
    }

    /// <summary>Captures the current target immediately, then inserts into it.</summary>
    public Task InsertTextAsync(string text, CancellationToken cancellationToken = default)
        => InsertTextAsync(text, CaptureTarget(), cancellationToken);

    public Task InsertTextAsync(
        string text,
        TextInsertionTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!target.IsValid || !IsWindow(target.WindowHandle))
        {
            throw new ArgumentException("The captured input window is no longer available.", nameof(target));
        }

        if (text.Length == 0)
        {
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => InsertOnStaThread(text, target, cancellationToken, completion))
        {
            IsBackground = true,
            Name = "ShadowWhispr text insertion"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void InsertOnStaThread(
        string text,
        TextInsertionTarget target,
        CancellationToken cancellationToken,
        TaskCompletionSource<object?> completion)
    {
        IDataObject? savedClipboard = null;
        bool clipboardOverwritten = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            savedClipboard = TryCaptureClipboard();
            SetClipboardTextWithRetry(text, cancellationToken);
            clipboardOverwritten = true;
            ActivateCapturedTarget(target, cancellationToken);
            SendPasteShortcut();
            completion.TrySetResult(null);
        }
        catch (OperationCanceledException ex)
        {
            completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
        finally
        {
            if (clipboardOverwritten)
            {
                RestoreClipboard(savedClipboard, text);
            }
        }
    }

    /// <summary>
    /// Takes a detached copy of everything currently on the clipboard so it can be
    /// put back after pasting. Returns null when the clipboard is empty or locked,
    /// in which case the clipboard is simply cleared afterwards.
    /// </summary>
    private static IDataObject? TryCaptureClipboard()
    {
        try
        {
            IDataObject? current = Clipboard.GetDataObject();
            if (current is null)
            {
                return null;
            }

            var snapshot = new DataObject();
            bool captured = false;
            foreach (string format in current.GetFormats(autoConvert: false))
            {
                try
                {
                    object? data = current.GetData(format, autoConvert: false);
                    if (data is not null)
                    {
                        snapshot.SetData(format, data);
                        captured = true;
                    }
                }
                catch (Exception ex)
                {
                    // A single unreadable format (for example a virtual file stream
                    // owned by an app that has since closed) must not lose the rest.
                    AppLog.Write($"Could not copy clipboard format '{format}' before pasting.", ex);
                }
            }

            return captured ? snapshot : null;
        }
        catch (Exception ex)
        {
            AppLog.Write("Could not read the existing clipboard contents before pasting.", ex);
            return null;
        }
    }

    private static void RestoreClipboard(IDataObject? saved, string pastedText)
    {
        try
        {
            Thread.Sleep(RestoreDelay);

            // Another app may have copied something while the paste was in flight;
            // only reclaim the clipboard when it still holds our transcript.
            if (Clipboard.ContainsText(TextDataFormat.UnicodeText)
                && !string.Equals(Clipboard.GetText(TextDataFormat.UnicodeText), pastedText, StringComparison.Ordinal))
            {
                return;
            }

            if (saved is null)
            {
                Clipboard.Clear();
            }
            else
            {
                Clipboard.SetDataObject(saved, copy: true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Write("Could not restore the clipboard contents after pasting.", ex);
        }
    }

    private static void SetClipboardTextWithRetry(string text, CancellationToken cancellationToken)
    {
        const int attempts = 10;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                Clipboard.SetText(text, TextDataFormat.UnicodeText);
                return;
            }
            catch (ExternalException) when (attempt < attempts)
            {
                Thread.Sleep(35);
            }
        }
    }

    private static void ActivateCapturedTarget(TextInsertionTarget target, CancellationToken cancellationToken)
    {
        if (!IsWindow(target.WindowHandle))
        {
            throw new InvalidOperationException("The captured input window was closed before transcription finished.");
        }

        nint focusedControl = target.FocusedControlHandle;
        if (focusedControl != 0
            && (!IsWindow(focusedControl)
                || (focusedControl != target.WindowHandle && !IsChild(target.WindowHandle, focusedControl))))
        {
            throw new InvalidOperationException(
                "The field captured when dictation started is no longer available; no text was pasted.");
        }

        uint ownThread = GetCurrentThreadId();
        uint windowThread = GetWindowThreadProcessId(target.WindowHandle, out _);
        uint focusThread = focusedControl != 0
            ? GetWindowThreadProcessId(focusedControl, out _)
            : windowThread;

        nint previousForeground = GetForegroundWindow();
        uint previousForegroundThread = previousForeground != 0
            ? GetWindowThreadProcessId(previousForeground, out _)
            : 0;

        // SetFocus only operates on the caller's input queue. Temporarily join the
        // relevant GUI queues even when the captured top-level window is already
        // foreground, because another child control may have taken focus meanwhile.
        var attachedThreads = new List<uint>(3);
        AttachInputThreadIfNeeded(ownThread, previousForegroundThread, attachedThreads);
        AttachInputThreadIfNeeded(ownThread, windowThread, attachedThreads);
        AttachInputThreadIfNeeded(ownThread, focusThread, attachedThreads);

        try
        {
            if (GetForegroundWindow() != target.WindowHandle)
            {
                SetForegroundWindow(target.WindowHandle);
            }

            for (int i = 0; i < 20 && GetForegroundWindow() != target.WindowHandle; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(15);
            }

            if (GetForegroundWindow() == target.WindowHandle && focusedControl != 0)
            {
                SetFocus(focusedControl);
            }
        }
        finally
        {
            for (int i = attachedThreads.Count - 1; i >= 0; i--)
            {
                AttachThreadInput(ownThread, attachedThreads[i], false);
            }
        }

        if (GetForegroundWindow() != target.WindowHandle)
        {
            throw new InvalidOperationException(
                "Windows would not return focus to the field captured when dictation started; no text was pasted.");
        }

        if (focusedControl != 0)
        {
            for (int i = 0; i < 20 && GetFocusedWindow(focusThread) != focusedControl; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(15);
            }

            if (GetFocusedWindow(focusThread) != focusedControl)
            {
                throw new InvalidOperationException(
                    "Windows would not return focus to the field captured when dictation started; no text was pasted.");
            }
        }
    }

    private static void AttachInputThreadIfNeeded(uint ownThread, uint otherThread, List<uint> attachedThreads)
    {
        if (otherThread == 0 || otherThread == ownThread || attachedThreads.Contains(otherThread))
        {
            return;
        }

        if (AttachThreadInput(ownThread, otherThread, true))
        {
            attachedThreads.Add(otherThread);
        }
    }

    private static nint GetFocusedWindow(uint threadId)
    {
        if (threadId == 0)
        {
            return 0;
        }

        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return GetGuiThreadInfo(threadId, ref info) ? info.Focus : 0;
    }

    private static void SendPasteShortcut()
    {
        Input[] inputs =
        [
            KeyboardInput(VkControl, keyUp: false),
            KeyboardInput(VkV, keyUp: false),
            KeyboardInput(VkV, keyUp: true),
            KeyboardInput(VkControl, keyUp: true)
        ];

        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent != inputs.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not send the paste shortcut.");
        }
    }

    private static Input KeyboardInput(ushort key, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInputData
            {
                VirtualKey = key,
                Flags = keyUp ? KeyEventKeyUp : 0
            }
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size;
        public uint Flags;
        public nint Active;
        public nint Focus;
        public nint Capture;
        public nint MenuOwner;
        public nint MoveSize;
        public nint Caret;
        public int CaretLeft;
        public int CaretTop;
        public int CaretRight;
        public int CaretBottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInputData Mouse;
        [FieldOffset(0)] public KeyboardInputData Keyboard;
        [FieldOffset(0)] public HardwareInputData Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareInputData
    {
        public uint Message;
        public ushort ParamLow;
        public ushort ParamHigh;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(nint parentWindow, nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetGUIThreadInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetGuiThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, Input[] inputs, int inputSize);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
