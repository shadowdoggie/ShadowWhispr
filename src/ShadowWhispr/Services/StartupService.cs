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
    /// Applies the requested state. Returns false when Windows refused the
    /// change, so the UI can fall back to reflecting reality instead of a lie.
    /// </summary>
    public static bool Apply(bool enabled)
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

                key.SetValue(ValueName, $"\"{executable}\" {TrayArgument}", RegistryValueKind.String);
                AppLog.Write($"Start with Windows enabled -> \"{executable}\" {TrayArgument}");
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
