using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ShadowWhispr.Services;

public sealed record VoiceCommandResult(string MatchedName, string Executable);

public static class VoiceCommandService
{
    private static readonly string[] AgyTriggers =
    [
        "open agy", "open 83", "open a g y", "open a gy", "open anti gravity", "open antigravity",
        "open anti-gravity", "open agy cli", "open anti gravity cli", "open antigravity cli",
        "open anti-gravity cli", "open anti gravity terminal", "open antigravity terminal",
        "open agy terminal", "agy", "anti gravity"
    ];

    private static readonly string[] OpenCodeTriggers =
    [
        "open code", "opencode", "open coat", "open opencode", "open open code", "open open coat",
        "open code cli", "open opencode cli", "open open code cli", "open code terminal",
        "open opencode terminal", "open open code terminal"
    ];

    private static readonly string[] CodexTriggers =
    [
        "open codex", "open codec", "open codecs", "open codex cli", "open codec cli", "open code sec",
        "open code sec cli", "codec cli", "codex cli", "open codec terminal", "open codex terminal"
    ];

    private static readonly string[] ClaudeTriggers =
    [
        "open claude", "open cloud code", "open claude code", "open cloud coat", "open clawed code",
        "cloud code", "claude code", "open claude cli", "open cloud code cli", "open claude terminal"
    ];

    private static readonly string[] GrokTriggers =
    [
        "open grok", "open groc", "open grock", "open groke", "open grog", "open grogue", "open croc",
        "open clock", "open quok", "open grok build", "open groc build", "open grock build", "open grog build",
        "open frog build", "grok build", "groc build", "grock build", "grog build", "open grok cli",
        "open grok terminal"
    ];

    public static bool TryMatchCommand(string? transcript, out VoiceCommandResult? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(transcript)) return false;

        string text = Normalize(transcript);

        if (MatchesAny(text, AgyTriggers))
        {
            result = new VoiceCommandResult("AGY", "agy");
            return true;
        }

        if (MatchesAny(text, OpenCodeTriggers))
        {
            result = new VoiceCommandResult("OpenCode", "opencode");
            return true;
        }

        if (MatchesAny(text, CodexTriggers))
        {
            result = new VoiceCommandResult("Codex CLI", "codex");
            return true;
        }

        if (MatchesAny(text, ClaudeTriggers))
        {
            result = new VoiceCommandResult("Claude Code", "claude");
            return true;
        }

        if (MatchesAny(text, GrokTriggers))
        {
            result = new VoiceCommandResult("Grok Build", "grok");
            return true;
        }

        return false;
    }

    private static string Normalize(string input)
    {
        var cleaned = input.Trim().TrimEnd('.', '!', '?', ',', ';', ':', '"', '\'').ToLowerInvariant();
        return Regex.Replace(cleaned, @"\s+", " ");
    }

    private static bool MatchesAny(string text, string[] triggers)
    {
        foreach (var trigger in triggers)
        {
            if (string.Equals(text, trigger, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static bool ExecuteCommand(VoiceCommandResult command)
    {
        if (!OperatingSystem.IsWindows())
        {
            AppLog.Write($"Voice command execution skipped: non-Windows OS ({command.MatchedName})");
            return false;
        }

        try
        {
            // Resolve to a full path with the same registry-aware search the AI
            // provider layer uses, so a CLI installed after ShadowWhispr started
            // (or outside this process's PATH snapshot) is still found. Falls
            // back to the bare name if resolution fails, keeping the shell's own
            // lookup in play rather than inventing a new failure.
            var executable = AiProviderService.FindOnPath(command.Executable) ?? command.Executable;
            AppLog.Write($"Voice command executing: launching {command.MatchedName} ({executable}) in PowerShell");

            // Run the CLI inside PowerShell and keep the window open. The
            // executable path travels base64-encoded through -EncodedCommand, so
            // no amount of spaces, quotes or special characters in it can break
            // the command line. If Windows has a default terminal app configured
            // (e.g. Windows Terminal), the PowerShell window opens inside it.
            var powershell = FindOnPath("powershell.exe") ?? Path.Combine(
                Environment.SystemDirectory,
                @"WindowsPowerShell\v1.0\powershell.exe");
            var encodedCommand = Convert.ToBase64String(
                Encoding.Unicode.GetBytes($"& '{executable}'"));
            var startInfo = new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = $"-NoExit -EncodedCommand {encodedCommand}",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable)
            };

            var proc = Process.Start(startInfo);
            AppLog.Write($"Voice command process started for {command.MatchedName} (PID: {proc?.Id ?? 0})");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Write($"Voice command process launch failed for {command.MatchedName}", ex);
            return false;
        }
    }

    private static string? FindOnPath(string command)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), command);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
