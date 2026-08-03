using System.Diagnostics;
using System.Text;
using ShadowWhispr.Services;

namespace ShadowWhispr.Linux.Services;

/// <summary>
/// Copies text to the clipboard, pastes it into the currently focused window
/// with a virtual-keyboard Ctrl+V, then puts whatever the user had on the
/// clipboard back — the same approach the Windows app takes. Wayland offers no
/// way to re-focus the window that was focused when dictation started, so the
/// text lands wherever focus is when transcription finishes; in practice that
/// is the field the user is dictating into.
/// </summary>
public sealed class LinuxTextInsertionService : IDisposable
{
    /// <summary>
    /// How long the pasted text must stay on the clipboard before the user's
    /// own clipboard contents are restored. Target applications read the
    /// clipboard asynchronously after Ctrl+V.
    /// </summary>
    private static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(500);

    private readonly UinputKeyboard _keyboard = new();
    private readonly bool _wayland =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    public async Task InsertTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) return;

        // Only plain text survives the round-trip; images or rich content on
        // the clipboard are lost to the paste, which the log at least records.
        string? savedClipboard = await TryReadClipboardAsync(cancellationToken);

        await WriteClipboardAsync(text, cancellationToken);
        try
        {
            // Give the clipboard manager a moment to take the new contents.
            await Task.Delay(120, cancellationToken);
            await Task.Run(_keyboard.SendPasteChord, cancellationToken);
        }
        finally
        {
            _ = RestoreClipboardLaterAsync(savedClipboard, text);
        }
    }

    private async Task RestoreClipboardLaterAsync(string? saved, string pastedText)
    {
        try
        {
            await Task.Delay(RestoreDelay);

            // Another app may have copied something while the paste was in
            // flight; only reclaim the clipboard when it still holds our text.
            var current = await TryReadClipboardAsync(CancellationToken.None);
            if (current is not null && !string.Equals(current, pastedText, StringComparison.Ordinal)) return;

            if (saved is null)
                await ClearClipboardAsync();
            else
                await WriteClipboardAsync(saved, CancellationToken.None);
        }
        catch (Exception exception)
        {
            AppLog.Write("Could not restore the clipboard contents after pasting", exception);
        }
    }

    private async Task<string?> TryReadClipboardAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (fileName, arguments) = _wayland
                ? ("wl-paste", new[] { "--no-newline" })
                : ("xclip", new[] { "-selection", "clipboard", "-o" });
            var result = await RunAsync(fileName, arguments, standardInput: null, cancellationToken, captureOutput: true);
            return result.ExitCode == 0 ? result.Output : null;
        }
        catch (Exception exception)
        {
            AppLog.Write("Could not read the existing clipboard contents before pasting", exception);
            return null;
        }
    }

    private async Task WriteClipboardAsync(string text, CancellationToken cancellationToken)
    {
        var (fileName, arguments) = _wayland
            ? ("wl-copy", Array.Empty<string>())
            : ("xclip", new[] { "-selection", "clipboard" });
        var result = await RunAsync(fileName, arguments, text, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not put the transcript on the clipboard ({fileName} exited with {result.ExitCode}). " +
                (_wayland ? "Is wl-clipboard installed?" : "Is xclip installed?"));
        }
    }

    private async Task ClearClipboardAsync()
    {
        if (_wayland)
        {
            await RunAsync("wl-copy", ["--clear"], null, CancellationToken.None);
        }
        else
        {
            await RunAsync("xclip", ["-selection", "clipboard"], string.Empty, CancellationToken.None);
        }
    }

    /// <summary>
    /// Output is only captured when asked for: wl-copy forks a child that keeps
    /// owning the clipboard, and that child would hold an inherited stdout pipe
    /// open indefinitely, deadlocking a ReadToEnd.
    /// </summary>
    private static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        CancellationToken cancellationToken,
        bool captureOutput = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = captureOutput
        };
        if (captureOutput) startInfo.StandardOutputEncoding = Encoding.UTF8;
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        if (standardInput is not null)
            startInfo.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");

        var outputTask = captureOutput
            ? process.StandardOutput.ReadToEndAsync(cancellationToken)
            : Task.FromResult(string.Empty);
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await outputTask);
    }

    public void Dispose() => _keyboard.Dispose();
}
