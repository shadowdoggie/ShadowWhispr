namespace ShadowWhispr.Models;

public sealed class AppSettings
{
    public string Hotkey { get; set; } = "Right Ctrl";

    /// <summary>
    /// Optional second hold hotkey that dictates with AI cleanup skipped, so the
    /// raw Parakeet transcript is typed as-is. Empty means "not configured".
    /// </summary>
    public string RawHotkey { get; set; } = string.Empty;

    public bool AiEnabled { get; set; }
    public string Provider { get; set; } = "Claude";
    public string ModelId { get; set; } = "claude-sonnet-5";
    public string Reasoning { get; set; } = "high";
    public string CustomInstruction { get; set; } =
        "You are a prompt improver/rebuilder, for an extreme adhd vibecoder guy who knows alot about software but nothing about coding. The user you improve/rebuild this prompt for is very impulsive so often doesn't really know what he wants. Don't ever make the prompt into something that requires manual input from the user. Don't ever say anything like this or similar: \"Complete this task entirely autonomously without requiring further input.\", because this causes the vibecoding tool to not be able to ask questions if it wants to, and sometimes questions are a good thing.";

    /// <summary>
    /// When true (the default), ShadowWhispr checks GitHub for a newer release on
    /// startup and installs it automatically when the app is closed, so the user
    /// never has to download and run an installer by hand.
    /// </summary>
    public bool AutoUpdateEnabled { get; set; } = true;

    /// <summary>
    /// When true, closing the main window hides ShadowWhispr to the system tray
    /// instead of quitting, so the hold hotkey keeps working. Quitting for real
    /// is always available from the tray menu.
    /// </summary>
    public bool KeepRunningInTray { get; set; } = true;

    /// <summary>
    /// Starts ShadowWhispr automatically (hidden in the tray) when Windows starts.
    /// Off by default — this is opt-in, and the checkbox in the app is the only
    /// thing that writes the Windows "Run" registry entry.
    /// </summary>
    public bool StartWithWindows { get; set; }
}
