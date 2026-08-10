using System;
using System.Diagnostics;
using System.IO;
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
        "open code", "opencode", "open coat", "open opencode", "open code cli", "open opencode cli",
        "open code terminal", "open opencode terminal"
    ];

    private static readonly string[] CodexTriggers =
    [
        "open codex", "open codec", "open codex cli", "open codec cli", "open code sec",
        "open code sec cli", "codec cli", "codex cli", "open codec terminal", "open codex terminal"
    ];

    private static readonly string[] ClaudeTriggers =
    [
        "open claude", "open cloud code", "open claude code", "open cloud coat", "open clawed code",
        "cloud code", "claude code", "open claude cli", "open cloud code cli", "open claude terminal"
    ];

    private static readonly string[] GrokTriggers =
    [
        "open grok", "open grok build", "open grog build", "open frog build", "grok build",
        "grog build", "open grok cli", "open grok terminal"
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
            AppLog.Write($"Voice command executing: launching {command.MatchedName} ({command.Executable})");

            var wtPath = FindOnPath("wt.exe") ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\WindowsApps\wt.exe");

            ProcessStartInfo startInfo;
            if (File.Exists(wtPath))
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = wtPath,
                    Arguments = command.Executable,
                    UseShellExecute = true
                };
            }
            else
            {
                startInfo = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c start cmd.exe /k \"{command.Executable}\"",
                    UseShellExecute = true
                };
            }

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
