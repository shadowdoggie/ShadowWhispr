using System.IO;

namespace ShadowWhispr.Services;

/// <summary>
/// Append-only application log next to the executable (app-log.txt), so
/// failures on a user's machine can be diagnosed from the file instead of
/// asking the user to reproduce and paste console output. Logging must never
/// break the app: every write failure is swallowed.
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static string LogPath { get; } = Path.Combine(ResolveLogDirectory(), "app-log.txt");

    static AppLog()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > MaxBytes)
            {
                var old = Path.Combine(Path.GetDirectoryName(LogPath)!, "app-log.old.txt");
                File.Copy(LogPath, old, overwrite: true);
                File.Delete(LogPath);
            }
        }
        catch { }
    }

    /// <summary>
    /// Next to the executable when that folder is writable (the Windows install
    /// always is). A Linux install under /usr is not, so the log falls back to
    /// the user's state directory (~/.local/state/ShadowWhispr).
    /// </summary>
    private static string ResolveLogDirectory()
    {
        try
        {
            var probe = Path.Combine(AppContext.BaseDirectory, $".write-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return AppContext.BaseDirectory;
        }
        catch
        {
            var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            if (string.IsNullOrWhiteSpace(stateHome))
                stateHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
            var directory = Path.Combine(stateHome, "ShadowWhispr");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    /// <summary>Logs a message plus the exception's full text and stack trace.</summary>
    public static void Write(string message, Exception exception) =>
        Write($"{message}{Environment.NewLine}{exception}");
}
