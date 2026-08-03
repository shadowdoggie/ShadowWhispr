namespace ShadowWhispr.Models;

public sealed record AiModelOption(
    string Provider,
    string Id,
    string DisplayName,
    IReadOnlyList<string> ReasoningLevels,
    string? DefaultReasoningLevel = null,
    bool SupportsFastMode = false)
{
    public override string ToString() => DisplayName;
}
