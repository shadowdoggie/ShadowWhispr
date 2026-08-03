using System.Diagnostics;
using System.Text;
using ShadowWhispr.Services;

namespace ShadowWhispr.Linux.Services;

/// <summary>
/// Copies text to the clipboard and the primary selection, pastes it into the
/// currently focused window with a virtual-keyboard Shift+Insert, then puts
/// whatever the user had on both back. Shift+Insert instead of the Ctrl+V the
/// Windows app sends: terminal emulators handle Shift+Insert themselves and
/// deliver pasted text to the application inside, so TUIs that bind Ctrl+V to
/// something else (Codex and other AI CLIs treat it as image paste) cannot
/// intercept the chord, while GUI toolkits all honour Shift+Insert as a
/// normal paste. The text goes on both selections because terminals disagree
/// on which one Shift+Insert reads (alacritty pastes the primary selection,
/// VTE terminals the clipboard). Wayland offers no way to re-focus the window
/// that was focused when dictation started, so the text lands wherever focus
/// is when transcription finishes; in practice that is the field the user is
/// dictating into.
/// </summary>
public sealed class LinuxTextInsertionService : IDisposable
{
    /// <summary>
    /// How long the pasted text must stay on the clipboard before the user's
    /// own clipboard contents are restored. Target applications read the
    /// clipboard asynchronously after the paste chord.
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
        string? savedClipboard = await TryReadSelectionAsync(primary: false, cancellationToken);
        string? savedPrimary = await TryReadSelectionAsync(primary: true, cancellationToken);

        await WriteSelectionAsync(text, primary: false, cancellationToken);
        await WriteSelectionAsync(text, primary: true, cancellationToken);
        try
        {
            // Give the clipboard manager a moment to take the new contents.
            await Task.Delay(120, cancellationToken);
            await Task.Run(_keyboard.SendPasteChord, cancellationToken);
        }
        finally
        {
            _ = RestoreSelectionsLaterAsync(savedClipboard, savedPrimary, text);
        }
    }

    private async Task RestoreSelectionsLaterAsync(string? savedClipboard, string? savedPrimary, string pastedText)
    {
        try
        {
            await Task.Delay(RestoreDelay);
            await RestoreSelectionAsync(savedClipboard, pastedText, primary: false);
            await RestoreSelectionAsync(savedPrimary, pastedText, primary: true);
        }
        catch (Exception exception)
        {
            AppLog.Write("Could not restore the clipboard contents after pasting", exception);
        }
    }

    private async Task RestoreSelectionAsync(string? saved, string pastedText, bool primary)
    {
        // Another app may have taken the selection while the paste was in
        // flight; only reclaim it when it still holds our text.
        var current = await TryReadSelectionAsync(primary, CancellationToken.None);
        if (current is not null && !string.Equals(current, pastedText, StringComparison.Ordinal)) return;

        if (saved is null)
            await ClearSelectionAsync(primary);
        else
            await WriteSelectionAsync(saved, primary, CancellationToken.None);
    }

    private async Task<string?> TryReadSelectionAsync(bool primary, CancellationToken cancellationToken)
    {
        try
        {
            var (fileName, arguments) = _wayland
                ? ("wl-paste", primary ? new[] { "--primary", "--no-newline" } : new[] { "--no-newline" })
                : ("xclip", new[] { "-selection", primary ? "primary" : "clipboard", "-o" });
            var result = await RunAsync(fileName, arguments, standardInput: null, cancellationToken, captureOutput: true);
            return result.ExitCode == 0 ? result.Output : null;
        }
        catch (Exception exception)
        {
            AppLog.Write($"Could not read the existing {SelectionName(primary)} contents before pasting", exception);
            return null;
        }
    }

    private async Task WriteSelectionAsync(string text, bool primary, CancellationToken cancellationToken)
    {
        var (fileName, arguments) = _wayland
            ? ("wl-copy", primary ? new[] { "--primary" } : Array.Empty<string>())
            : ("xclip", new[] { "-selection", primary ? "primary" : "clipboard" });
        var result = await RunAsync(fileName, arguments, text, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not put the transcript on the {SelectionName(primary)} ({fileName} exited with {result.ExitCode}). " +
                (_wayland ? "Is wl-clipboard installed?" : "Is xclip installed?"));
        }
    }

    private async Task ClearSelectionAsync(bool primary)
    {
        if (_wayland)
        {
            await RunAsync("wl-copy", primary ? ["--primary", "--clear"] : ["--clear"], null, CancellationToken.None);
        }
        else
        {
            await RunAsync("xclip", ["-selection", primary ? "primary" : "clipboard"], string.Empty, CancellationToken.None);
        }
    }

    private static string SelectionName(bool primary) => primary ? "primary selection" : "clipboard";

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
