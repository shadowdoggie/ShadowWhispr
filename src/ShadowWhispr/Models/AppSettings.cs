namespace ShadowWhispr.Models;

public sealed class AppSettings
{
    public string Hotkey { get; set; } = "Right Ctrl";
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
}
