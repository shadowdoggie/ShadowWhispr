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

    public static string LogPath { get; } = Path.Combine(AppContext.BaseDirectory, "app-log.txt");

    static AppLog()
    {
        try
        {
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > MaxBytes)
            {
                var old = Path.Combine(AppContext.BaseDirectory, "app-log.old.txt");
                File.Copy(LogPath, old, overwrite: true);
                File.Delete(LogPath);
            }
        }
        catch { }
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
