using Microsoft.Win32;

namespace ShadowWhispr.Services;

/// <summary>
/// Opt-in "start with Windows" support. This writes a single per-user value
/// under HKCU\...\Run — no admin rights, no scheduled task, and nothing outside
/// the current user's own profile. It is off by default and only ever changed
/// by the checkbox in the app, so the user stays in control of it.
/// </summary>
public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ShadowWhispr";

    /// <summary>
    /// Passed to the auto-started copy so it comes up hidden in the tray rather
    /// than throwing a window in the user's face at every login.
    /// </summary>
    public const string TrayArgument = "--tray";

    /// <summary>True when the Run entry exists and points at this executable.</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string existing && existing.Length > 0;
        }
        catch (Exception exception)
        {
            AppLog.Write("Could not read the start-with-Windows setting", exception);
            return false;
        }
    }

    /// <summary>
    /// True when the existing Run entry starts ShadowWhispr hidden in the tray.
    /// Entries written before this option existed always carried the tray flag,
    /// so an older entry reports true and the checkbox matches what happens.
    /// </summary>
    public static bool StartsMinimized()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key?.GetValue(ValueName) is not string existing || existing.Length == 0) return true;
            return existing.Contains(TrayArgument, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception)
        {
            AppLog.Write("Could not read the start-minimized setting", exception);
            return true;
        }
    }

    /// <summary>
    /// Applies the requested state. Returns false when Windows refused the
    /// change, so the UI can fall back to reflecting reality instead of a lie.
    /// </summary>
    /// <param name="startMinimized">
    /// When true the auto-started copy is launched with <see cref="TrayArgument"/>
    /// so it comes up hidden in the tray; when false it opens its window normally.
    /// </param>
    public static bool Apply(bool enabled, bool startMinimized = true)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                AppLog.Write("Start with Windows failed: the Run registry key could not be opened");
                return false;
            }

            if (enabled)
            {
                var executable = Environment.ProcessPath;
                if (string.IsNullOrEmpty(executable))
                {
                    AppLog.Write("Start with Windows failed: the executable path is unknown");
                    return false;
                }

                var command = startMinimized
                    ? $"\"{executable}\" {TrayArgument}"
                    : $"\"{executable}\"";
                key.SetValue(ValueName, command, RegistryValueKind.String);
                AppLog.Write($"Start with Windows enabled -> {command}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                AppLog.Write("Start with Windows disabled");
            }

            return true;
        }
        catch (Exception exception)
        {
            AppLog.Write($"Could not change the start-with-Windows setting (requested: {enabled})", exception);
            return false;
        }
    }
}
