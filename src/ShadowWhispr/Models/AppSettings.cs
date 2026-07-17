namespace ShadowWhispr.Models;

public sealed class AppSettings
{
    public string Hotkey { get; set; } = "Right Ctrl";
    public bool AiEnabled { get; set; }
    public string Provider { get; set; } = "Claude";
    public string ModelId { get; set; } = "claude-sonnet-5";
    public string Reasoning { get; set; } = "high";
    public string CustomInstruction { get; set; } =
        "Fix punctuation and obvious speech-to-text mistakes while preserving my meaning and tone.";
}
