namespace ShadowWhispr.Models;

public sealed class AppSettings
{
    public string Hotkey { get; set; } = "Right Ctrl";

    /// <summary>
    /// Optional second hotkey that dictates with AI cleanup skipped, so the raw
    /// Parakeet transcript is typed as-is. Empty means "not configured".
    /// </summary>
    public string RawHotkey { get; set; } = string.Empty;

    /// <summary>
    /// Optional third hotkey that hands the transcript to a headless Claude Code
    /// session as an instruction instead of typing it. Empty means "not
    /// configured", which is also what leaves agent mode unusable.
    /// </summary>
    public string AgentHotkey { get; set; } = string.Empty;

    /// <summary>
    /// Optional key that aborts the newest running agent session. Separate from
    /// <see cref="AgentHotkey"/> because that one queues another instruction, so
    /// it cannot also mean "stop". Empty means "not configured".
    /// </summary>
    public string AgentAbortHotkey { get; set; } = string.Empty;

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

    /// <summary>
    /// Turns the agent hotkey into a live Claude Code session that can act on
    /// this PC. Off by default: it runs with permission prompts bypassed, so it
    /// is never something the app switches on by itself.
    /// </summary>
    public bool AgentModeEnabled { get; set; }

    /// <summary>
    /// The folder the headless Claude Code session starts in, which is also the
    /// only tree it can reach without being told a full path. Empty means the
    /// Windows user profile folder.
    /// </summary>
    public string AgentWorkingDirectory { get; set; } = string.Empty;

    /// <summary>
    /// The model agent mode runs. Kept apart from <see cref="ModelId"/> because
    /// carrying out a task and tidying a transcript are different jobs, and the
    /// choice for one says nothing about the other.
    /// </summary>
    public string AgentModelId { get; set; } = "claude-opus-5";

    /// <summary>
    /// The effort level agent mode runs at, kept apart from
    /// <see cref="Reasoning"/> for the same reason as the model.
    /// </summary>
    public string AgentEffort { get; set; } = "medium";

    /// <summary>
    /// Which generation of agent defaults this settings file has been through.
    /// Left at zero by every file written before the idea existed, which is how
    /// <see cref="SettingsService"/> knows to apply the current defaults once
    /// and then leave the user's choices alone.
    /// </summary>
    public int AgentDefaultsVersion { get; set; }

    /// <summary>
    /// The agent defaults generation this build ships. Bumped only when a new
    /// release should move everyone onto a different model or effort, not
    /// whenever the defaults happen to be edited.
    /// </summary>
    public const int CurrentAgentDefaultsVersion = 1;

    /// <summary>
    /// Puts this settings file on the current agent defaults, and reports
    /// whether anything changed. Applied once per generation: agent mode is new
    /// enough that moving everyone onto the model and effort worth using is
    /// worth more than preserving a choice made while it was being built, but
    /// doing it on every launch would be overriding the user rather than
    /// updating them.
    /// </summary>
    public bool ApplyCurrentAgentDefaults()
    {
        if (AgentDefaultsVersion >= CurrentAgentDefaultsVersion) return false;

        AgentDefaultsVersion = CurrentAgentDefaultsVersion;
        AgentModelId = "claude-opus-5";
        AgentEffort = "medium";
        return true;
    }

    /// <summary>
    /// Standing facts handed to every agent session on top of its own system
    /// prompt: what the machine is, which apps matter, what names mean. Every
    /// session starts blank, so anything the agent should always know has to
    /// live here rather than being explained out loud each time.
    /// </summary>
    public string AgentInstruction { get; set; } =
        """
        You are running on my own Windows PC, signed in as me, with full shell access. Desktop automation is expected and allowed: activate windows, send keystrokes, start apps, open URLs, click things. Do not turn a task down because it involves an app rather than a file — work out how to drive it and try.

        Facts about my setup:
        - (put your own standing facts here: which apps you use, what "my server" or "the site" means, your usernames, where your projects live)
        """;

    /// <summary>
    /// Runs the spoken instruction through the AI cleanup above before handing
    /// it to the agent. Off by default: it costs a second call and a couple of
    /// seconds, which is only worth it if the raw transcript trips the agent up.
    /// </summary>
    public bool AgentCleanupEnabled { get; set; }

    /// <summary>
    /// Whether a spoken instruction actually gets tidied before the agent acts
    /// on it. Agent cleanup borrows the provider, model and instruction from the
    /// AI cleanup settings, so it cannot run while those are switched off — the
    /// setting on its own is only half the answer.
    ///
    /// Kept here rather than as a check at each use so the UI and the run agree
    /// by construction: the checkbox greys out on exactly the condition that
    /// stops the cleanup happening.
    /// </summary>
    public bool WillCleanAgentInstruction => AiEnabled && AgentCleanupEnabled;

    /// <summary>
    /// Plays a quiet chime when an agent run finishes. On by default: a run can
    /// take minutes, and without it the only way to know it is done is to keep
    /// checking the window. Switched off entirely by the "no sounds" setting,
    /// same as every other cue.
    /// </summary>
    public bool AgentFinishedSoundEnabled { get; set; } = true;

    /// <summary>
    /// The working folder to actually use, with the empty default resolved.
    /// </summary>
    public string ResolveAgentWorkingDirectory() =>
        string.IsNullOrWhiteSpace(AgentWorkingDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : AgentWorkingDirectory.Trim();

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

    /// <summary>
    /// Silences the short start and stop cues that play around a dictation.
    /// Off by default, so the sounds keep working unless they are turned off.
    /// </summary>
    public bool SoundCuesMuted { get; set; }
}
