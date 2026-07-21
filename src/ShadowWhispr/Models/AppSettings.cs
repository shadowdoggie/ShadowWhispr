namespace ShadowWhispr.Models;

public sealed class AppSettings
{
    public string Hotkey { get; set; } = "Right Ctrl";

    /// <summary>
    /// Optional second hold hotkey that dictates with AI cleanup skipped, so the
    /// raw Parakeet transcript is typed as-is. Empty means "not configured".
    /// </summary>
    public string RawHotkey { get; set; } = string.Empty;

    /// <summary>
    /// Optional tap hotkey: press once to start recording, press again to stop,
    /// with no key held in between. The result is treated exactly like the main
    /// hotkey (AI cleanup applies when enabled). Empty means "not configured".
    /// </summary>
    public string ToggleHotkey { get; set; } = string.Empty;

    /// <summary>
    /// Optional raw tap hotkey: taps like <see cref="ToggleHotkey"/>, but the
    /// transcript skips AI cleanup like <see cref="RawHotkey"/>. Empty means
    /// "not configured".
    /// </summary>
    public string ToggleRawHotkey { get; set; } = string.Empty;

    /// <summary>
    /// The name of the microphone to record from, exactly as Windows lists it.
    /// Empty means "follow the Windows default microphone". Stored by name so
    /// the choice survives device numbers reshuffling between sessions.
    /// </summary>
    public string Microphone { get; set; } = string.Empty;

    public bool AiEnabled { get; set; }
    public string Provider { get; set; } = "Claude";
    public string ModelId { get; set; } = "claude-sonnet-5";

    /// <summary>
    /// The reasoning effort for the provider in use. Kept for the settings files
    /// written before per-provider memory existed, and still the value the
    /// current provider falls back to when it has nothing remembered yet.
    /// </summary>
    public string Reasoning { get; set; } = "high";

    /// <summary>
    /// Reasoning effort per provider, so switching away from a provider and back
    /// restores the effort that was chosen for it rather than whatever the other
    /// provider happened to be using.
    /// </summary>
    public Dictionary<string, string> ReasoningByProvider { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Selected model per provider, remembered for the same reason as the
    /// effort: coming back to a provider should restore the setup that was left
    /// there, not reset to whichever model happens to be listed first.
    /// </summary>
    public Dictionary<string, string> ModelByProvider { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the remembered model for a provider, falling back to the shared
    /// value so an existing settings file keeps the user's current choice.
    /// </summary>
    public string? GetModelFor(string provider) =>
        LookUp(ModelByProvider, provider)
        ?? (string.Equals(provider, Provider, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(ModelId)
                ? ModelId
                : null);

    /// <summary>Records the model chosen for a provider. Blank values are ignored.</summary>
    public void SetModelFor(string provider, string? modelId) =>
        Store(ModelByProvider, provider, modelId);

    /// <summary>
    /// Returns the remembered effort for a provider, falling back to the shared
    /// value so an existing settings file keeps the user's current choice.
    /// </summary>
    public string? GetReasoningFor(string provider) =>
        LookUp(ReasoningByProvider, provider)
        ?? (string.Equals(provider, Provider, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(Reasoning)
                ? Reasoning
                : null);

    /// <summary>
    /// Records the effort chosen for a provider. Blank values are ignored: the
    /// reasoning list is momentarily empty while a provider's models are being
    /// discovered, and that must not erase what the user picked.
    /// </summary>
    public void SetReasoningFor(string provider, string? reasoning) =>
        Store(ReasoningByProvider, provider, reasoning);

    private static string? LookUp(Dictionary<string, string> values, string provider)
    {
        if (string.IsNullOrWhiteSpace(provider)) return null;
        foreach (var pair in values)
        {
            if (string.Equals(pair.Key, provider, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(pair.Value))
                return pair.Value;
        }
        return null;
    }

    private static void Store(Dictionary<string, string> values, string provider, string? value)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(value)) return;

        // Deserialized dictionaries lose the case-insensitive comparer, so drop
        // any key that differs only by case before writing the canonical one.
        foreach (var key in values.Keys
                     .Where(existing => string.Equals(existing, provider, StringComparison.OrdinalIgnoreCase) &&
                                        !string.Equals(existing, provider, StringComparison.Ordinal))
                     .ToList())
        {
            values.Remove(key);
        }

        values[provider] = value;
    }
    /// <summary>
    /// Codex's "fast" speed tier (its priority service tier): about 1.5x faster
    /// responses, at the price of using up the Codex allowance quicker. Off by
    /// default, so nobody spends extra allowance without choosing to. Only Codex
    /// offers this, so it is a single flag rather than a per-provider map.
    /// </summary>
    public bool CodexFastMode { get; set; }

    public string CustomInstruction { get; set; } =
        """
        You are a prompt cleaner for an extreme ADHD vibecoder who knows a lot about software but nothing about coding. He is impulsive and often doesn't fully know what he wants yet.

        Rules, in order of importance:
        1. NEVER change what the user means. Fix grammar, filler words, and speech-to-text mistakes — nothing more.
        2. NEVER add details, technical terms, or claims the user didn't say. If he says something vague like "the injection part", keep exactly "the injection part" — do not guess what it means or swap in a more specific term. A vague prompt in his own words is correct; a specific prompt he didn't say is wrong.
        3. NEVER remove details the user provides, example: "on my friend's pc it doesn't work".
        4. Never make the prompt into something that requires manual input from the user. Never add anything like "Complete this task entirely autonomously without requiring further input" — that blocks the coding tool from asking questions, and sometimes questions are a good thing.
        5. If a sentence is unclear even after cleanup, keep it as-is rather than rewriting it into your best guess.
        """;

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
