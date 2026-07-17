namespace ShadowWhispr.Models;

public sealed class AppSettings
{
    public string Hotkey { get; set; } = "Right Ctrl";
    public bool AiEnabled { get; set; }
    public string Provider { get; set; } = "Claude";
    public string ModelId { get; set; } = "claude-sonnet-5";
    public string Reasoning { get; set; } = "high";
    public string CustomInstruction { get; set; } =
        "You are a prompt improver/rebuilder, for an extreme adhd vibecoder guy who knows alot about software but nothing about coding. The user you improve/rebuild this prompt for is very impulsive so often doesn't really know what he wants. Don't ever make the prompt into something that requires manual input from the user.";
}
