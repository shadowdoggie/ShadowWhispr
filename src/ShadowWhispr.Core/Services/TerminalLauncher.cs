using System.Diagnostics;

namespace ShadowWhispr.Services;

/// <summary>
/// Finds an installed terminal emulator and builds the start info to run a
/// command inside it. Linux only — Windows consoles open themselves. Each
/// terminal has its own way of being handed a command to run.
/// </summary>
public static class TerminalLauncher
{
    private static readonly (string Name, string[] Prefix)[] Terminals =
    [
        // --standalone / --wait keep the process attached to the window;
        // without them these client/server terminals return immediately and
        // callers would think the command finished the moment it opened.
        ("ptyxis", ["--standalone", "--"]),  // GNOME's default since 46
        ("kgx", ["--"]),                     // GNOME Console
        ("gnome-terminal", ["--wait", "--"]),
        ("konsole", ["-e"]),
        ("foot", []),
        ("alacritty", ["-e"]),
        ("kitty", []),
        ("wezterm", ["start", "--"]),
        ("xterm", ["-e"]),
    ];

    /// <summary>
    /// Builds the start info that runs <paramref name="executable"/> inside the
    /// first terminal emulator found on PATH, or null when none exists.
    /// </summary>
    public static ProcessStartInfo? TryCreate(
        string executable,
        IReadOnlyCollection<string> arguments,
        string? workingDirectory = null)
    {
        foreach (var (name, prefix) in Terminals)
        {
            var terminalPath = FindOnPath(name);
            if (terminalPath is null) continue;

            var startInfo = new ProcessStartInfo
            {
                FileName = terminalPath,
                UseShellExecute = false
            };
            if (workingDirectory is not null) startInfo.WorkingDirectory = workingDirectory;
            foreach (var part in prefix) startInfo.ArgumentList.Add(part);
            startInfo.ArgumentList.Add(executable);
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

            AppLog.Write($"Using terminal emulator {name} to run {executable}");
            return startInfo;
        }

        return null;
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
