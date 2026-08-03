using ShadowWhispr.Services;

namespace ShadowWhispr.Linux.Services;

/// <summary>
/// Opt-in "start at login" support through the XDG autostart directory — a
/// single .desktop file in the user's own config, nothing system-wide. It is
/// off by default and only ever changed by the checkbox in the app.
/// </summary>
public static class LinuxAutostartService
{
    /// <summary>
    /// Passed to the auto-started copy so it comes up hidden in the tray rather
    /// than throwing a window in the user's face at every login.
    /// </summary>
    public const string TrayArgument = "--tray";

    private static string DesktopFilePath
    {
        get
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrWhiteSpace(configHome))
                configHome = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(configHome, "autostart", "shadowwhispr.desktop");
        }
    }

    /// <summary>True when the autostart entry exists.</summary>
    public static bool IsEnabled()
    {
        try
        {
            return File.Exists(DesktopFilePath);
        }
        catch (Exception exception)
        {
            AppLog.Write("Could not read the start-at-login setting", exception);
            return false;
        }
    }

    /// <summary>
    /// Applies the requested state. Returns false when the change failed, so
    /// the UI can fall back to reflecting reality instead of a lie.
    /// </summary>
    public static bool Apply(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var executable = Environment.ProcessPath;
                if (string.IsNullOrEmpty(executable))
                {
                    AppLog.Write("Start at login failed: the executable path is unknown");
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(DesktopFilePath)!);
                File.WriteAllText(DesktopFilePath,
                    $"""
                     [Desktop Entry]
                     Type=Application
                     Name=ShadowWhispr
                     Comment=Hold a key. Speak. It types for you.
                     Exec="{executable}" {TrayArgument}
                     X-GNOME-Autostart-enabled=true
                     """ + "\n");
                AppLog.Write($"Start at login enabled -> \"{executable}\" {TrayArgument}");
            }
            else
            {
                File.Delete(DesktopFilePath);
                AppLog.Write("Start at login disabled");
            }

            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (Exception exception)
        {
            AppLog.Write($"Could not change the start-at-login setting (requested: {enabled})", exception);
            return false;
        }
    }
}
