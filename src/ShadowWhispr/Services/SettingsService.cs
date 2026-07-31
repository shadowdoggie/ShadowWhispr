using System.IO;
using System.Text.Json;
using ShadowWhispr.Models;

namespace ShadowWhispr.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public SettingsService()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ShadowWhispr");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "settings.json");
    }

    public AppSettings Load()
    {
        AppSettings settings;
        try
        {
            settings = File.Exists(_path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception exception)
        {
            AppLog.Write($"Settings file could not be read ({_path}); using defaults", exception);
            settings = new AppSettings();
        }

        // A fresh install already has the current defaults; an existing one is
        // moved onto them once, here, rather than being left on whatever agent
        // mode happened to be set to while it was being built.
        if (settings.ApplyCurrentAgentDefaults())
        {
            AppLog.Write(
                $"Applied agent defaults v{AppSettings.CurrentAgentDefaultsVersion}: " +
                $"{settings.AgentModelId} at {settings.AgentEffort} effort");
            TrySave(settings);
        }

        return settings;
    }

    /// <summary>
    /// Writes settings without letting a failure stop the app from starting:
    /// the migration above is already applied in memory, so the worst a failed
    /// write costs is applying it again next launch.
    /// </summary>
    private void TrySave(AppSettings settings)
    {
        try
        {
            Save(settings);
        }
        catch (Exception exception)
        {
            AppLog.Write("Could not save settings after applying the agent defaults", exception);
        }
    }

    public void Save(AppSettings settings)
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, _path, true);
    }
}
