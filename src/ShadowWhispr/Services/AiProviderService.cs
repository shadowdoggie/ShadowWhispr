using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShadowWhispr.Models;

namespace ShadowWhispr.Services;

/// <summary>Whether a provider CLI is signed in, as far as it can be asked.</summary>
public enum ProviderLoginStatus
{
    /// <summary>The CLI cannot be asked, is missing, or gave an answer we do not understand.</summary>
    Unknown,
    LoggedIn,
    LoggedOut
}

public sealed partial class AiProviderService
{
    public const string Claude = "Claude";
    public const string Codex = "Codex";
    public const string Gemini = "Gemini";

    /// <summary>
    /// The two HTTP API providers. Unlike the three above they are not CLIs:
    /// they are OpenAI-compatible chat-completions endpoints authenticated with
    /// an API key the user pastes into settings.
    /// </summary>
    public const string DeepSeek = "DeepSeek";
    public const string OpenRouter = "OpenRouter";

    public static IReadOnlyList<string> Providers { get; } = [Claude, Codex, Gemini, DeepSeek, OpenRouter];

    private static readonly IReadOnlyList<AiModelOption> ClaudeModels =
    [
        new(Claude, "claude-opus-5", "Claude Opus 5", ["low", "medium", "high", "xhigh", "max"], "high"),
        new(Claude, "claude-fable-5", "Claude Fable 5", ["low", "medium", "high", "xhigh", "max"], "high"),
        new(Claude, "claude-opus-4-8", "Claude Opus 4.8", ["low", "medium", "high", "xhigh", "max"], "high"),
        new(Claude, "claude-sonnet-5", "Claude Sonnet 5", ["low", "medium", "high", "xhigh", "max"], "high"),
        new(Claude, "claude-haiku-4-5", "Claude Haiku 4.5", [], null)
    ];

    private static readonly IReadOnlyList<AiModelOption> DefaultGeminiModels =
    [
        new(Gemini, "gemini-3.6-flash", "Gemini 3.6 Flash", ["low", "medium", "high"], "high"),
        new(Gemini, "gemini-3.5-flash", "Gemini 3.5 Flash", ["low", "medium", "high"], "high"),
        new(Gemini, "gemini-3.1-pro", "Gemini 3.1 Pro", ["low", "high"], "high")
    ];

    /// <summary>
    /// The HTTP providers' model lists are fixed rather than discovered: each
    /// API offers one model worth using for cleanup, and listing models would
    /// need a network call (and a valid key) just to show a one-entry combo.
    /// "off" means no thinking at all, which is what a transcript tidy usually
    /// wants — it is the cheapest and fastest setting, not a degraded one.
    /// </summary>
    private static readonly IReadOnlyList<AiModelOption> DeepSeekModels =
    [
        new(DeepSeek, "deepseek-v4-flash", "DeepSeek V4 Flash", ["off", "low", "high", "max"], "off")
    ];

    private static readonly IReadOnlyList<AiModelOption> OpenRouterModels =
    [
        new(OpenRouter, "deepseek/deepseek-v4-flash-0731", "DeepSeek V4 Flash 0731", ["off", "low", "medium", "high"], "off")
    ];

    /// <summary>The speed tier Codex's model cache lists for fast-capable models.</summary>
    private const string FastSpeedTier = "fast";

    /// <summary>The value Codex's <c>service_tier</c> setting takes for fast mode.</summary>
    private const string PriorityServiceTier = "priority";

    private static readonly string[] ReasoningOrder =
        ["off", "minimal", "low", "medium", "high", "xhigh", "max", "ultra", "on"];

    /// <summary>
    /// The default agent model, which a fresh install gets before anyone has
    /// thought about the choice.
    /// </summary>
    public const string DefaultAgentModelId = "claude-opus-5";

    /// <summary>
    /// The default effort for the default model. Each model carries its own
    /// default alongside the levels it offers, because they do not offer the
    /// same ones — Opus on low is worth having where Sonnet on low is not.
    ///
    /// Deliberately unrelated to the reasoning level chosen for dictation
    /// cleanup: that setting is about how carefully a transcript is tidied,
    /// which says nothing about how hard a desktop task should be thought
    /// through.
    /// </summary>
    public const string DefaultAgentEffort = "medium";

    /// <summary>
    /// The effort levels agent mode offers, hardest last. Declared before
    /// <see cref="AgentModels"/> on purpose: static initialisers run in
    /// declaration order, so the other way round hands the models a null list.
    /// </summary>
    public static readonly string[] AgentEffortLevels = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// What Sonnet offers, which is everything but "low": at that level it is
    /// not good enough at carrying out a spoken task to be worth offering, and a
    /// setting that only ever disappoints is worse than no setting.
    /// </summary>
    private static readonly string[] SonnetAgentEffortLevels = ["medium", "high", "xhigh", "max"];

    /// <summary>
    /// The models agent mode may run. Kept to the three that are worth handing a
    /// spoken task to, rather than the full Claude line-up: the rest are either
    /// no better at tool use or not worth the wait for a one-sentence job.
    /// </summary>
    public static IReadOnlyList<AiModelOption> AgentModels { get; } =
    [
        new(Claude, DefaultAgentModelId, "Claude Opus 5", AgentEffortLevels, DefaultAgentEffort),
        new(Claude, "claude-fable-5", "Claude Fable 5", AgentEffortLevels, DefaultAgentEffort),
        new(Claude, "claude-sonnet-5", "Claude Sonnet 5", SonnetAgentEffortLevels, "medium"),
        // The one non-Claude agent, run through `codex exec` rather than the
        // Claude CLI. Its effort levels and its Fast tier are taken from what
        // Codex itself reports for the model.
        new(Codex, "gpt-5.6-luna", "GPT-5.6-Luna", AgentEffortLevels, DefaultAgentEffort, SupportsFastMode: true)
    ];

    /// <summary>
    /// Which CLI runs a given agent model. Agent mode was Claude-only to begin
    /// with, so everything that branches on this exists to keep the Codex path
    /// from inheriting Claude's flags.
    /// </summary>
    public static string GetAgentProvider(string? modelId) => GetAgentModel(modelId).Provider;

    /// <summary>Whether the chosen agent model offers Codex's faster service tier.</summary>
    public static bool AgentModelSupportsFastMode(string? modelId) => GetAgentModel(modelId).SupportsFastMode;

    /// <summary>
    /// Falls back to the default when a stored model is one this build no longer
    /// offers, so an old settings file can never leave agent mode unrunnable.
    /// </summary>
    public static string NormalizeAgentModelId(string? modelId) =>
        AgentModels.Any(model => string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase))
            ? modelId!
            : DefaultAgentModelId;

    private static AiModelOption GetAgentModel(string? modelId) =>
        AgentModels.First(model => model.Id == NormalizeAgentModelId(modelId));

    /// <summary>The effort levels the given model offers.</summary>
    public static IReadOnlyList<string> GetAgentEffortLevels(string? modelId) =>
        GetAgentModel(modelId).ReasoningLevels;

    /// <summary>
    /// Falls back to the chosen model's own default when the stored effort is
    /// one it does not offer — which is what a settings file written before
    /// Sonnet dropped "low" contains, and what switching from Opus on low to
    /// Sonnet asks for. The fallback is per model rather than shared, because a
    /// shared one would be a level one of the two does not have.
    /// </summary>
    public static string NormalizeAgentEffort(string? modelId, string? effort)
    {
        var model = GetAgentModel(modelId);
        return model.ReasoningLevels.Contains(effort, StringComparer.OrdinalIgnoreCase)
            ? effort!.ToLowerInvariant()
            : model.DefaultReasoningLevel ?? DefaultAgentEffort;
    }

    private readonly TimeSpan _commandTimeout;

    /// <summary>
    /// Agent runs get their own, much longer limit: cleaning a transcript is one
    /// model call, while carrying out a spoken task can mean many tool calls.
    /// </summary>
    private readonly TimeSpan _agentTimeout;

    private readonly string _isolatedWorkDirectory;

    /// <summary>
    /// Hands the service an API key for a given HTTP provider (DeepSeek,
    /// OpenRouter). Set by the app to read from settings rather than passing
    /// keys into the constructor, so the service always sees the key the user
    /// pasted in most recently instead of a copy taken at start-up.
    /// </summary>
    public Func<string, string?>? ApiKeyResolver { get; set; }

    /// <summary>
    /// One shared client for every HTTP provider call, as HttpClient is meant to
    /// be used. Its own timeout is only a backstop against a hang nobody asked
    /// about: each request is actually cut off by <see cref="_commandTimeout"/>
    /// through a linked CTS, the same way <see cref="RunAsync"/> cuts off a CLI.
    /// </summary>
    private static readonly HttpClient HttpApi = new() { Timeout = TimeSpan.FromMinutes(10) };

    public AiProviderService(TimeSpan? commandTimeout = null, TimeSpan? agentTimeout = null)
    {
        _commandTimeout = commandTimeout ?? TimeSpan.FromMinutes(5);
        _agentTimeout = agentTimeout ?? TimeSpan.FromMinutes(20);
        _isolatedWorkDirectory = Path.Combine(Path.GetTempPath(), "ShadowWhispr", "ai-workspace");
    }

    public bool IsCliAvailable(string provider)
    {
        var normalizedProvider = NormalizeProvider(provider);
        if (IsHttpApiProvider(normalizedProvider))
        {
            // There is no CLI to look for: the API is always "installed", and
            // whether it can be used is a question about the key, which is what
            // GetLoginStatusAsync answers.
            return true;
        }

        var command = GetCommand(normalizedProvider);
        return FindOnPath(command) is not null;
    }

    public string GetAuthenticationCommand(string provider) => NormalizeProvider(provider) switch
    {
        Claude => "claude auth login --claudeai",
        Codex => "codex login",
        Gemini => "agy (opens OAuth onboarding when signed out)",
        DeepSeek => "API key (platform.deepseek.com)",
        OpenRouter => "API key (openrouter.ai/keys)",
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    /// <summary>
    /// Asks a provider's CLI whether it is already signed in, without opening
    /// anything the user has to click. Claude and Codex both have a status
    /// command; Antigravity has none, so Gemini always answers "cannot tell".
    /// </summary>
    public async Task<ProviderLoginStatus> GetLoginStatusAsync(
        string provider,
        CancellationToken cancellationToken = default)
    {
        var normalizedProvider = NormalizeProvider(provider);
        if (IsHttpApiProvider(normalizedProvider))
        {
            // For an API-key provider "signed in" simply means a key has been
            // pasted in. No process is spawned and the key is never validated
            // here: a wrong key surfaces as a clear HTTP error on first use.
            return string.IsNullOrWhiteSpace(ApiKeyResolver?.Invoke(normalizedProvider))
                ? ProviderLoginStatus.LoggedOut
                : ProviderLoginStatus.LoggedIn;
        }

        if (normalizedProvider == Gemini)
        {
            // agy has no status command: the only way to find out is to open its
            // window, which is exactly what checking is meant to avoid.
            return ProviderLoginStatus.Unknown;
        }

        if (!IsCliAvailable(normalizedProvider))
        {
            return ProviderLoginStatus.Unknown;
        }

        EnsureIsolatedDirectories();
        IReadOnlyCollection<string> arguments = normalizedProvider == Claude
            ? ["auth", "status", "--json"]
            : ["login", "status"];

        ProcessResult result;
        try
        {
            result = await RunAsync(
                GetCommand(normalizedProvider),
                arguments,
                standardInput: null,
                workingDirectory: _isolatedWorkDirectory,
                environment: null,
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.Write($"Could not read {normalizedProvider} login status: {exception.Message}");
            return ProviderLoginStatus.Unknown;
        }

        var status = normalizedProvider == Claude
            ? ReadClaudeLoginStatus(result)
            : ReadCodexLoginStatus(result);
        AppLog.Write($"{normalizedProvider} login status: {status}");
        return status;
    }

    /// <summary>
    /// Claude prints its status as JSON with a <c>loggedIn</c> flag.
    /// </summary>
    private static ProviderLoginStatus ReadClaudeLoginStatus(ProcessResult result)
    {
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (document.RootElement.TryGetProperty("loggedIn", out var loggedIn) &&
                loggedIn.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return loggedIn.GetBoolean() ? ProviderLoginStatus.LoggedIn : ProviderLoginStatus.LoggedOut;
            }
        }
        catch (JsonException)
        {
            // Fall through: an unreadable answer is treated as "cannot tell"
            // rather than guessing the user out of a working login.
        }

        return ProviderLoginStatus.Unknown;
    }

    /// <summary>
    /// Codex answers in plain words, for example "Logged in using ChatGPT".
    /// </summary>
    private static ProviderLoginStatus ReadCodexLoginStatus(ProcessResult result)
    {
        var output = $"{result.StandardOutput}\n{result.StandardError}";
        if (output.Contains("not logged in", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderLoginStatus.LoggedOut;
        }
        if (result.ExitCode == 0 && output.Contains("logged in", StringComparison.OrdinalIgnoreCase))
        {
            return ProviderLoginStatus.LoggedIn;
        }
        return result.ExitCode == 0 ? ProviderLoginStatus.Unknown : ProviderLoginStatus.LoggedOut;
    }

    /// <summary>
    /// Opens the provider's official interactive subscription sign-in flow in a visible console.
    /// The task completes when the provider CLI exits, allowing the caller to refresh models/status.
    /// </summary>
    public async Task LoginAsync(string provider, CancellationToken cancellationToken = default)
    {
        EnsureIsolatedDirectories();
        var normalizedProvider = NormalizeProvider(provider);
        if (IsHttpApiProvider(normalizedProvider))
        {
            // Nothing to open: there is no sign-in flow, only a key. Thrown
            // rather than silently ignored so a UI path that wrongly offers a
            // login button still tells the user what to actually do.
            throw new AiProviderException(
                $"{normalizedProvider} uses an API key instead of a login — paste it on the AI cleanup page.");
        }

        var arguments = normalizedProvider switch
        {
            Claude => new[] { "auth", "login", "--claudeai" },
            Codex => new[] { "login" },
            // agy has no auth subcommand. Its official first-run onboarding launches Google OAuth
            // automatically when no Antigravity session is available.
            Gemini => Array.Empty<string>(),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };

        await RunInteractiveAsync(GetCommand(normalizedProvider), arguments, cancellationToken);
    }

    /// <summary>
    /// Signs out through the provider's official CLI when it exposes a non-destructive logout command.
    /// </summary>
    public async Task LogoutAsync(string provider, CancellationToken cancellationToken = default)
    {
        EnsureIsolatedDirectories();
        var normalizedProvider = NormalizeProvider(provider);
        if (IsHttpApiProvider(normalizedProvider))
        {
            // "Logging out" of an API key is deleting the key, which lives in
            // settings — same message as LoginAsync so both dead ends point at
            // the one place that actually controls access.
            throw new AiProviderException(
                $"{normalizedProvider} uses an API key instead of a login — paste it on the AI cleanup page.");
        }

        if (normalizedProvider == Gemini)
        {
            // Antigravity exposes sign-out as /logout inside its interactive CLI.
            await RunInteractiveAsync(GetCommand(normalizedProvider), [], cancellationToken);
            return;
        }

        IReadOnlyCollection<string> arguments = normalizedProvider switch
        {
            Claude => new[] { "auth", "logout" },
            Codex => new[] { "logout" },
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };

        var result = await RunAsync(
            GetCommand(normalizedProvider),
            arguments,
            standardInput: null,
            workingDirectory: _isolatedWorkDirectory,
            environment: null,
            cancellationToken);

        EnsureSuccess(normalizedProvider, result);
    }

    public async Task<IReadOnlyList<AiModelOption>> DiscoverModelsAsync(
        string provider,
        CancellationToken cancellationToken = default)
    {
        EnsureIsolatedDirectories();
        return NormalizeProvider(provider) switch
        {
            Claude => ClaudeModels,
            Codex => await DiscoverCodexModelsAsync(cancellationToken),
            Gemini => await DiscoverGeminiModelsAsync(cancellationToken),
            // Fixed lists: see the comment on DeepSeekModels for why nothing is
            // asked over the network here.
            DeepSeek => DeepSeekModels,
            OpenRouter => OpenRouterModels,
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<AiModelOption>>> DiscoverAllModelsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, IReadOnlyList<AiModelOption>>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in Providers)
        {
            result[provider] = await DiscoverModelsAsync(provider, cancellationToken);
        }

        return result;
    }

    public async Task<string> ProcessAsync(
        string provider,
        string modelId,
        string? reasoning,
        string instruction,
        string text,
        CancellationToken cancellationToken = default,
        bool fastMode = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentNullException.ThrowIfNull(instruction);
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return text;
        }

        EnsureIsolatedDirectories();
        var normalizedProvider = NormalizeProvider(provider);
        var prompt = BuildPrompt(instruction, text);
        var output = normalizedProvider switch
        {
            Claude => await ProcessWithClaudeAsync(modelId, reasoning, prompt, cancellationToken),
            Codex => await ProcessWithCodexAsync(modelId, reasoning, prompt, fastMode, cancellationToken),
            Gemini => await ProcessWithGeminiAsync(modelId, reasoning, prompt, cancellationToken),
            DeepSeek or OpenRouter =>
                await ProcessWithHttpAsync(normalizedProvider, modelId, reasoning, prompt, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };

        if (string.IsNullOrWhiteSpace(output))
        {
            throw new AiProviderException($"{normalizedProvider} returned an empty response.");
        }

        return output.Trim();
    }

    /// <summary>
    /// Hands a spoken instruction to a headless Claude Code session that is
    /// allowed to act on this PC, and returns what it reports back.
    ///
    /// Every call is a brand new session: nothing is resumed, and with
    /// <c>--no-session-persistence</c> nothing is written to disk either, so one
    /// spoken sentence can never be coloured by the last one.
    /// </summary>
    /// <param name="instruction">The transcribed instruction, spoken by the user.</param>
    /// <param name="workingDirectory">The folder the session starts in.</param>
    /// <param name="modelId">The chosen agent model; anything unknown falls back to the default.</param>
    /// <param name="effort">The chosen effort level; anything unknown falls back to the default.</param>
    /// <param name="standingInstruction">
    /// The user's own standing facts, added after ours. Theirs come last so that
    /// what they wrote wins where the two disagree.
    /// </param>
    /// <param name="wantsSpokenReply">
    /// Asks the session to add a <c>&lt;spoken&gt;</c> version of its reply for
    /// reading out loud. Only requested when the user has spoken replies turned
    /// on, so nobody else pays for the extra instruction or risks seeing the tag.
    /// </param>
    public async Task<string> RunAgentAsync(
        string instruction,
        string workingDirectory,
        string? modelId = null,
        string? effort = null,
        string? standingInstruction = null,
        bool wantsSpokenReply = false,
        bool fastMode = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instruction);
        var model = NormalizeAgentModelId(modelId);
        var chosenEffort = NormalizeAgentEffort(model, effort);

        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
        {
            AppLog.Write($"Agent run refused: working folder does not exist ({workingDirectory})");
            throw new AiProviderException(
                $"The agent working folder does not exist: {workingDirectory}");
        }

        var systemPrompt = BuildAgentSystemPrompt(standingInstruction, wantsSpokenReply);

        if (GetAgentProvider(model) == Codex)
        {
            return await RunCodexAgentAsync(
                instruction,
                workingDirectory,
                model,
                chosenEffort,
                systemPrompt,
                fastMode && AgentModelSupportsFastMode(model),
                cancellationToken);
        }

        var arguments = new List<string>
        {
            "-p",
            "--model", model,
            "--effort", chosenEffort,
            // Voice gives no way to answer a permission prompt, so a session that
            // stopped to ask would simply hang until it timed out.
            //
            // Both flags are passed on purpose. --permission-mode sets the mode
            // for the session, while --dangerously-skip-permissions is the one
            // that does not depend on the user having accepted Claude Code's
            // one-time bypass warning in an interactive session first - which a
            // user who has only ever used ShadowWhispr never has.
            "--permission-mode", "bypassPermissions",
            "--dangerously-skip-permissions",
            "--no-session-persistence",
            "--output-format", "json",
            "--append-system-prompt", systemPrompt
        };

        AppLog.Write(
            $"Agent run starting: model={model}, effort={chosenEffort}, " +
            $"spoken reply {(wantsSpokenReply ? "requested" : "not requested")}, " +
            $"cwd={workingDirectory}, instruction length={instruction.Length}, " +
            $"standing facts {(string.IsNullOrWhiteSpace(standingInstruction) ? "none" : $"{standingInstruction.Trim().Length} characters")}");

        var result = await RunAsync(
            GetCommand(Claude),
            arguments,
            instruction,
            workingDirectory,
            environment: null,
            cancellationToken,
            _agentTimeout);

        EnsureSuccess(Claude, result);

        string? text;
        bool isError;
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            text = GetString(root, "result");
            isError = root.TryGetProperty("is_error", out var errorFlag) &&
                      errorFlag.ValueKind == JsonValueKind.True;
        }
        catch (JsonException exception)
        {
            AppLog.Write("Agent run returned unreadable JSON", exception);
            throw new AiProviderException("Claude Code returned an unreadable response.", exception);
        }

        if (isError)
        {
            AppLog.Write($"Agent run reported a failure: {text ?? "(no detail)"}");
            throw new AiProviderException(
                string.IsNullOrWhiteSpace(text) ? "Claude Code reported an error." : text);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            AppLog.Write("Agent run finished but returned no text");
            throw new AiProviderException("Claude Code finished without saying anything.");
        }

        AppLog.Write($"Agent run finished, reply length={text.Length}");
        return text.Trim();
    }

    /// <summary>
    /// Runs one spoken instruction through `codex exec` instead of the Claude
    /// CLI.
    ///
    /// This is a separate method rather than a few conditionals in
    /// <see cref="RunAgentAsync"/> because almost nothing carries over: Codex
    /// takes the effort and the service tier as config entries, streams JSONL
    /// events rather than returning one JSON object, and has its own way of
    /// being told not to stop and ask permission.
    /// </summary>
    private async Task<string> RunCodexAgentAsync(
        string instruction,
        string workingDirectory,
        string model,
        string effort,
        string systemPrompt,
        bool fastMode,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "exec",
            "--json",
            "--color", "never",
            "--skip-git-repo-check",
            // The user's own Codex config is deliberately not read: agent mode's
            // model, effort and tier are chosen in ShadowWhispr, and a config
            // file that disagreed would silently win.
            "--ignore-user-config",
            // Unlike cleanup, which runs read-only in a scratch folder, an agent
            // run exists to change things - so it gets the real folder and the
            // ability to write in it. This matches what agent mode already does
            // with Claude, and the warning on the agent page covers both.
            "--dangerously-bypass-approvals-and-sandbox",
            "--cd", workingDirectory,
            "--model", model,
            "--config", $"model_reasoning_effort=\"{EscapeTomlString(effort)}\""
        };

        if (fastMode)
        {
            arguments.Add("--config");
            arguments.Add($"service_tier=\"{PriorityServiceTier}\"");
        }

        AppLog.Write(
            $"Codex agent run starting: model={model}, effort={effort}, " +
            $"fast mode {(fastMode ? "on" : "off")}, cwd={workingDirectory}, " +
            $"instruction length={instruction.Length}");

        // Codex has no equivalent of Claude's --append-system-prompt, so the
        // standing facts and the reply rules are prepended to the instruction.
        // Marked off from what the user actually said, so the model can tell the
        // two apart.
        var prompt = $"""
                      <how-to-reply>
                      {systemPrompt}
                      </how-to-reply>

                      {instruction}
                      """;

        arguments.Add("-");
        var result = await RunAsync(
            GetCommand(Codex),
            arguments,
            prompt,
            workingDirectory,
            environment: null,
            cancellationToken,
            _agentTimeout);

        EnsureSuccess(Codex, result);

        // The reply is the last agent message in the event stream; earlier ones
        // are progress commentary, and taking the first would report what it
        // planned to do rather than what it did.
        var text = string.Empty;
        foreach (var line in SplitLines(result.StandardOutput))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (GetString(root, "type") != "item.completed" ||
                    !root.TryGetProperty("item", out var item) ||
                    GetString(item, "type") != "agent_message")
                {
                    continue;
                }

                var message = GetString(item, "text");
                if (!string.IsNullOrWhiteSpace(message)) text = message;
            }
            catch (JsonException)
            {
                // Codex prints the occasional non-JSON line. Skipping it is
                // right: the reply we want arrives as a well-formed event.
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            AppLog.Write("Codex agent run finished but returned no text");
            throw new AiProviderException("Codex finished without saying anything.");
        }

        AppLog.Write($"Codex agent run finished, reply length={text.Length}");
        return text.Trim();
    }

    private async Task<IReadOnlyList<AiModelOption>> DiscoverCodexModelsAsync(CancellationToken cancellationToken)
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        }

        var cachePath = Path.Combine(codexHome, "models_cache.json");
        if (!File.Exists(cachePath))
        {
            throw new AiProviderException(
                "Codex has no model cache yet. Open Codex once while signed in, then refresh the model list.");
        }

        try
        {
            await using var stream = File.OpenRead(cachePath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Array)
            {
                throw new AiProviderException("Codex's model cache has an unexpected format.");
            }

            var discovered = new List<(int Priority, AiModelOption Model)>();
            foreach (var model in models.EnumerateArray())
            {
                var id = GetString(model, "slug");
                if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var visibility = GetString(model, "visibility");
                if (!string.IsNullOrWhiteSpace(visibility) &&
                    !visibility.Equals("list", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var efforts = new List<string>();
                if (model.TryGetProperty("supported_reasoning_levels", out var levels) &&
                    levels.ValueKind == JsonValueKind.Array)
                {
                    foreach (var level in levels.EnumerateArray())
                    {
                        var effort = GetString(level, "effort");
                        AddUnique(efforts, effort);
                    }
                }

                var displayName = GetString(model, "display_name") ?? id;
                var defaultEffort = GetString(model, "default_reasoning_level") ?? efforts.FirstOrDefault();
                var priority = model.TryGetProperty("priority", out var priorityElement) &&
                               priorityElement.TryGetInt32(out var parsedPriority)
                    ? parsedPriority
                    : int.MaxValue;

                discovered.Add((priority, new AiModelOption(
                    Codex,
                    id,
                    displayName,
                    SortReasoningLevels(efforts),
                    defaultEffort,
                    SupportsFastMode: HasFastSpeedTier(model))));
            }

            return discovered
                .OrderBy(item => item.Priority)
                .ThenBy(item => item.Model.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Model)
                .ToArray();
        }
        catch (AiProviderException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            throw new AiProviderException("Could not read Codex's model cache.", exception);
        }
    }

    /// <summary>
    /// True when Codex's model cache says this model offers the "fast" speed
    /// tier, which the CLI selects with <c>service_tier="priority"</c>. Read from
    /// the cache rather than hardcoded: which models offer it changes with the
    /// model line-up, and a stale hardcoded list would silently offer users a
    /// toggle the model rejects.
    /// </summary>
    private static bool HasFastSpeedTier(JsonElement model)
    {
        if (model.TryGetProperty("additional_speed_tiers", out var tiers) &&
            tiers.ValueKind == JsonValueKind.Array)
        {
            foreach (var tier in tiers.EnumerateArray())
            {
                if (tier.ValueKind == JsonValueKind.String &&
                    string.Equals(tier.GetString(), FastSpeedTier, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<IReadOnlyList<AiModelOption>> DiscoverGeminiModelsAsync(CancellationToken cancellationToken)
    {
        ProcessResult? result = null;
        try
        {
            result = await RunAsync(
                GetCommand(Gemini),
                ["models"],
                standardInput: null,
                workingDirectory: _isolatedWorkDirectory,
                environment: null,
                cancellationToken);
        }
        catch (Exception exception)
        {
            AppLog.Write($"Failed to discover Gemini models: {exception.Message}");
        }

        var grouped = new Dictionary<string, (string DisplayName, List<string> Efforts)>(StringComparer.OrdinalIgnoreCase);

        if (result is not null && result.ExitCode == 0)
        {
            foreach (var rawLine in SplitLines(result.StandardOutput))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Gemini ", StringComparison.OrdinalIgnoreCase))
                {
                    string id;
                    string displayName;
                    string? effort = null;

                    var match = ModelWithEffortRegex().Match(line);
                    if (match.Success)
                    {
                        var modelName = match.Groups["model"].Value.Trim();
                        effort = match.Groups["effort"].Value.ToLowerInvariant();
                        id = FormatGeminiSlug(modelName);
                        displayName = FormatGeminiDisplayName(modelName);
                    }
                    else
                    {
                        var parts = line.Split('-');
                        var lastPart = parts[^1].ToLowerInvariant();
                        if (parts.Length > 2 && ReasoningOrder.Contains(lastPart))
                        {
                            effort = lastPart;
                            var baseSlug = string.Join('-', parts[..^1]);
                            id = baseSlug;
                            displayName = FormatGeminiDisplayName(baseSlug);
                        }
                        else
                        {
                            id = FormatGeminiSlug(line);
                            displayName = FormatGeminiDisplayName(line);
                        }
                    }

                    if (!grouped.TryGetValue(id, out var entry))
                    {
                        entry = (displayName, []);
                        grouped[id] = entry;
                    }

                    AddUnique(entry.Efforts, effort);
                }
            }
        }

        if (grouped.Count == 0)
        {
            return DefaultGeminiModels;
        }

        return grouped
            .Select(pair =>
            {
                var efforts = SortReasoningLevels(pair.Value.Efforts);
                var defaultEffort = efforts.Contains("high", StringComparer.OrdinalIgnoreCase)
                    ? "high"
                    : (efforts.Contains("medium", StringComparer.OrdinalIgnoreCase)
                        ? "medium"
                        : efforts.FirstOrDefault());

                return new AiModelOption(
                    Gemini,
                    pair.Key,
                    pair.Value.DisplayName,
                    efforts,
                    defaultEffort);
            })
            .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string FormatGeminiDisplayName(string text)
    {
        if (text.StartsWith("Gemini ", StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        if (text.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
        {
            var parts = text.Split('-');
            return string.Join(" ", parts.Select(p => ToTitleCase(p)));
        }

        return ToTitleCase(text);
    }

    private static string FormatGeminiSlug(string text)
    {
        if (text.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
        {
            return text.ToLowerInvariant();
        }

        return text.ToLowerInvariant().Replace(' ', '-');
    }

    private async Task<string> ProcessWithClaudeAsync(
        string modelId,
        string? reasoning,
        string prompt,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-p",
            "--model", modelId,
            "--no-session-persistence",
            "--output-format", "json",
            "--permission-mode", "dontAsk",
            "--tools", string.Empty,
            "--disable-slash-commands",
            "--no-chrome",
            "--strict-mcp-config",
            "--mcp-config", "{\"mcpServers\":{}}",
            "--setting-sources", string.Empty,
            "--system-prompt", BaseSystemPrompt
        };
        AddOption(arguments, "--effort", reasoning);

        var result = await RunAsync(
            GetCommand(Claude),
            arguments,
            prompt,
            _isolatedWorkDirectory,
            new Dictionary<string, string?>
            {
                ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1"
            },
            cancellationToken);

        EnsureSuccess(Claude, result);
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            if (root.TryGetProperty("result", out var resultElement) &&
                resultElement.ValueKind == JsonValueKind.String)
            {
                return resultElement.GetString() ?? string.Empty;
            }
        }
        catch (JsonException exception)
        {
            throw new AiProviderException("Claude returned an unreadable response.", exception);
        }

        throw new AiProviderException("Claude did not return final text.");
    }

    private async Task<string> ProcessWithCodexAsync(
        string modelId,
        string? reasoning,
        string prompt,
        bool fastMode,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "--ask-for-approval", "never",
            "exec",
            "--json",
            "--ephemeral",
            "--ignore-user-config",
            "--ignore-rules",
            "--skip-git-repo-check",
            "--color", "never",
            "--sandbox", "read-only",
            "--cd", _isolatedWorkDirectory,
            "--model", modelId
        };
        if (!string.IsNullOrWhiteSpace(reasoning))
        {
            arguments.Add("--config");
            arguments.Add($"model_reasoning_effort=\"{EscapeTomlString(reasoning)}\"");
        }

        // Fast mode is Codex's "priority" service tier. --ignore-user-config above
        // means config.toml is not read, so the tier is only ever what is set here.
        if (fastMode)
        {
            arguments.Add("--config");
            arguments.Add($"service_tier=\"{PriorityServiceTier}\"");
        }

        AppLog.Write($"Codex cleanup starting: model={modelId}, effort={reasoning ?? "default"}, fast mode {(fastMode ? "on" : "off")}");

        arguments.Add("-");
        var result = await RunAsync(
            GetCommand(Codex),
            arguments,
            prompt,
            _isolatedWorkDirectory,
            environment: null,
            cancellationToken);

        EnsureSuccess(Codex, result);
        var finalText = string.Empty;
        foreach (var line in SplitLines(result.StandardOutput))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (GetString(root, "type") != "item.completed" ||
                    !root.TryGetProperty("item", out var item) ||
                    GetString(item, "type") != "agent_message")
                {
                    continue;
                }

                finalText = GetString(item, "text") ?? finalText;
            }
            catch (JsonException)
            {
                // Codex JSONL can include a diagnostic line from its launcher. Ignore it.
            }
        }

        return finalText;
    }

    private async Task<string> ProcessWithGeminiAsync(
        string modelId,
        string? reasoning,
        string prompt,
        CancellationToken cancellationToken)
    {
        var slug = FormatGeminiSlug(modelId);
        var selectedModel = !string.IsNullOrWhiteSpace(reasoning) && !slug.EndsWith($"-{reasoning.ToLowerInvariant()}", StringComparison.OrdinalIgnoreCase)
            ? $"{slug}-{reasoning.ToLowerInvariant()}"
            : slug;

        var result = await RunAsync(
            GetCommand(Gemini),
            ["--model", selectedModel, "--mode", "plan", "--sandbox", "--print", prompt],
            standardInput: null,
            workingDirectory: _isolatedWorkDirectory,
            environment: null,
            cancellationToken);

        EnsureSuccess(Gemini, result);
        return result.StandardOutput;
    }

    /// <summary>
    /// True for the providers that are HTTP APIs rather than CLIs. Everything
    /// that would spawn a process — <see cref="GetCommand"/> included — must
    /// check this first, so the CLI plumbing never sees them.
    /// </summary>
    private static bool IsHttpApiProvider(string provider) =>
        provider is DeepSeek or OpenRouter;

    /// <summary>
    /// The key for an HTTP provider, or a message telling the user where to
    /// paste one. Resolved per call rather than cached so a key added while the
    /// app is running works immediately.
    /// </summary>
    private string ResolveApiKey(string provider)
    {
        var key = ApiKeyResolver?.Invoke(provider)?.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new AiProviderException($"Add your {provider} API key on the AI cleanup page.");
        }

        return key;
    }

    /// <summary>
    /// One cleanup call against an OpenAI-compatible chat-completions API,
    /// shared by DeepSeek and OpenRouter: the request shape and the reply shape
    /// are the same, and only the endpoint, the headers and the spelling of the
    /// reasoning knob differ.
    /// </summary>
    private async Task<string> ProcessWithHttpAsync(
        string provider,
        string modelId,
        string? reasoning,
        string prompt,
        CancellationToken cancellationToken)
    {
        var apiKey = ResolveApiKey(provider);
        var level = string.IsNullOrWhiteSpace(reasoning) ? "off" : reasoning.Trim().ToLowerInvariant();

        var body = new Dictionary<string, object>
        {
            ["model"] = modelId,
            ["messages"] = new[]
            {
                new Dictionary<string, string> { ["role"] = "user", ["content"] = prompt }
            },
            ["stream"] = false
        };

        if (provider == DeepSeek)
        {
            // DeepSeek splits the choice in two: `thinking` switches reasoning
            // on or off, `reasoning_effort` says how hard. "off" is thinking
            // disabled with no effort field at all — sending an effort alongside
            // disabled thinking would be contradictory.
            body["thinking"] = new Dictionary<string, string>
            {
                ["type"] = level == "off" ? "disabled" : "enabled"
            };
            if (level != "off")
            {
                body["reasoning_effort"] = level;
            }
        }
        else
        {
            // OpenRouter folds both halves into one `reasoning` object instead.
            body["reasoning"] = level == "off"
                ? new Dictionary<string, object> { ["enabled"] = false }
                : new Dictionary<string, object> { ["effort"] = level };
        }

        var endpoint = provider == DeepSeek
            ? "https://api.deepseek.com/chat/completions"
            : "https://openrouter.ai/api/v1/chat/completions";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (provider == OpenRouter)
        {
            // OpenRouter's optional attribution headers: they identify the app
            // on openrouter.ai rankings and cost nothing to send.
            request.Headers.Add("HTTP-Referer", "https://github.com/shadowdog-cat/ShadowWhispr");
            request.Headers.Add("X-Title", "ShadowWhispr");
        }

        AppLog.Write($"{provider} cleanup starting: model={modelId}, reasoning={level}");

        // Same timeout shape as RunAsync: the user's token plus the service's
        // command timeout, so an API hang cannot outlive what a CLI would get.
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_commandTimeout);

        string responseBody;
        System.Net.HttpStatusCode statusCode;
        bool isSuccess;
        try
        {
            using var response = await HttpApi.SendAsync(request, timeoutSource.Token);
            statusCode = response.StatusCode;
            isSuccess = response.IsSuccessStatusCode;
            responseBody = await response.Content.ReadAsStringAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            AppLog.Write($"{provider} API call timed out after {_commandTimeout.TotalMinutes:0.#} minutes");
            throw new AiProviderException(
                $"{provider} did not answer within {_commandTimeout.TotalMinutes:0.#} minutes.");
        }
        catch (HttpRequestException exception)
        {
            AppLog.Write($"{provider} API call failed: {exception.Message}");
            throw new AiProviderException($"{provider} could not be reached: {exception.Message}", exception);
        }

        if (!isSuccess)
        {
            // The full body goes to the log (it never contains the key); the
            // user gets the short version with whatever the API said went wrong.
            AppLog.Write($"{provider} API returned HTTP {(int)statusCode}: {responseBody}");
            var apiMessage = TryReadApiErrorMessage(responseBody);
            throw new AiProviderException(string.IsNullOrWhiteSpace(apiMessage)
                ? $"{provider} returned HTTP {(int)statusCode} ({statusCode})."
                : $"{provider} returned HTTP {(int)statusCode}: {apiMessage}");
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var message))
            {
                var content = GetString(message, "content");
                if (!string.IsNullOrWhiteSpace(content))
                {
                    AppLog.Write($"{provider} cleanup finished, reply length={content.Length}");
                    return content;
                }
            }
        }
        catch (JsonException exception)
        {
            AppLog.Write($"{provider} returned unreadable JSON", exception);
            throw new AiProviderException($"{provider} returned an unreadable response.", exception);
        }

        AppLog.Write($"{provider} response had no message content");
        throw new AiProviderException($"{provider} did not return any text.");
    }

    /// <summary>
    /// The API's own explanation of a failure (<c>error.message</c> in the JSON
    /// body), or null when the body is not JSON or has no such field — in which
    /// case the status code alone has to carry the message.
    /// </summary>
    private static string? TryReadApiErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("error", out var error) &&
                   error.ValueKind == JsonValueKind.Object
                ? GetString(error, "message")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyCollection<string> arguments,
        string? standardInput,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var effectiveTimeout = timeout ?? _commandTimeout;
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(effectiveTimeout);

        // Resolve to a full path rather than letting the OS search this
        // process's frozen PATH, so a CLI installed after ShadowWhispr started
        // is still found. Falls back to the bare name if resolution fails, which
        // keeps the original behaviour rather than inventing a new failure.
        var executable = FindOnPath(fileName) ?? fileName;

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Hand the CLI an up-to-date PATH as well: these tools shell out to
        // their own dependencies (node, for one), which this process's stale
        // snapshot may predate.
        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, GetSearchDirectories());

        if (environment is not null)
        {
            foreach (var item in environment)
            {
                startInfo.Environment[item.Key] = item.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new AiProviderException($"Could not start {fileName}.");
            }
        }
        catch (Win32Exception exception)
        {
            AppLog.Write($"AI CLI could not start: {fileName} ({exception.Message})");
            throw new AiProviderException(
                $"{fileName} is not installed or is not available on PATH.",
                exception);
        }

        // Immediately, and before it has had a chance to start children of its
        // own: from here on Windows guarantees this process dies with
        // ShadowWhispr, however ShadowWhispr goes.
        ChildProcessJob.Shared.Assign(process);

        var outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        try
        {
            if (standardInput is not null)
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), timeoutSource.Token);
            process.StandardInput.Close();

            await process.WaitForExitAsync(timeoutSource.Token);
            return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            AppLog.Write($"AI CLI timed out after {effectiveTimeout.TotalMinutes:0.#} minutes: {fileName}");
            throw new AiProviderException($"{fileName} did not finish within {effectiveTimeout.TotalMinutes:0.#} minutes.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private async Task RunInteractiveAsync(
        string command,
        IReadOnlyCollection<string> arguments,
        CancellationToken cancellationToken)
    {
        var executable = FindOnPath(command);
        if (executable is null)
        {
            throw new AiProviderException($"{command} is not installed or is not available on PATH.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = _isolatedWorkDirectory,
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Normal
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new AiProviderException($"Could not start {command}'s sign-in window.");
            }

            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0)
            {
                throw new AiProviderException($"{command}'s sign-in window exited with code {process.ExitCode}.");
            }
        }
        catch (Win32Exception exception)
        {
            throw new AiProviderException($"Could not open {command}'s sign-in window.", exception);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static void EnsureSuccess(string provider, ProcessResult result)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        var details = LastUsefulLine(result.StandardError);
        if (string.IsNullOrWhiteSpace(details))
        {
            details = LastUsefulLine(result.StandardOutput);
        }

        AppLog.Write($"AI CLI failed: {provider} exited with code {result.ExitCode} ({(string.IsNullOrWhiteSpace(details) ? "no output" : details)})");
        throw new AiProviderException(string.IsNullOrWhiteSpace(details)
            ? $"{provider} exited with code {result.ExitCode}. Sign in through its CLI and try again."
            : $"{provider}: {details}");
    }

    private static string BuildPrompt(string instruction, string text) => $$"""
        {{BaseSystemPrompt}}

        Custom instruction:
        <instruction>
        {{instruction}}
        </instruction>

        Transcribed text:
        <transcript>
        {{text}}
        </transcript>
        """;

    private static string NormalizeProvider(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        var match = Providers.FirstOrDefault(item => item.Equals(provider.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown AI provider.");
    }

    private static string GetCommand(string provider) => NormalizeProvider(provider) switch
    {
        Claude => "claude",
        Codex => "codex",
        Gemini => "agy",
        // DeepSeek and OpenRouter deliberately fall through: they are HTTP APIs
        // with no CLI, and every caller checks IsHttpApiProvider first, so
        // reaching here with one of them is a bug worth throwing on.
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    /// <summary>
    /// Finds a provider CLI, searching this process's PATH <em>and</em> the PATH
    /// as it currently stands in the registry.
    ///
    /// A process gets a snapshot of PATH when it starts and never sees later
    /// changes. That used to be harmless because closing the window quit
    /// ShadowWhispr, so the next launch picked up a fresh environment. Now that
    /// it keeps running in the system tray - potentially for days, and started
    /// automatically at login - a CLI installed or updated afterwards would stay
    /// invisible to it until the user thought to fully quit and reopen the app.
    /// Re-reading the registry keeps detection honest without a restart.
    /// </summary>
    private static string? FindOnPath(string command)
    {
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidates = Path.HasExtension(command)
            ? [command]
            : extensions.Select(extension => command + extension.ToLowerInvariant()).Prepend(command);
        var candidateList = candidates.ToList();

        var directories = GetSearchDirectories();
        foreach (var directory in directories)
        {
            foreach (var candidate in candidateList)
            {
                string fullPath;
                try
                {
                    fullPath = Path.Combine(directory, candidate);
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry must not stop the rest of the search.
                    break;
                }

                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        AppLog.Write($"'{command}' was not found in any of the {directories.Count} PATH directories searched");
        return null;
    }

    /// <summary>
    /// The directories to search, in order: this process's PATH first (it is
    /// what child processes will actually inherit), then anything the current
    /// user or machine PATH has gained since this process started.
    /// </summary>
    private static List<string> GetSearchDirectories()
    {
        var directories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddFrom(string? pathValue)
        {
            if (string.IsNullOrEmpty(pathValue)) return;
            foreach (var entry in pathValue.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var directory = entry.Trim('"');
                if (directory.Length > 0 && seen.Add(directory)) directories.Add(directory);
            }
        }

        AddFrom(Environment.GetEnvironmentVariable("PATH"));

        // Reading these can throw if the registry is unavailable; a stale-but-
        // working search beats no search at all.
        try
        {
            AddFrom(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
            AddFrom(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        }
        catch (Exception exception)
        {
            AppLog.Write("Could not re-read PATH from the registry; using this process's PATH only", exception);
        }

        return directories;
    }

    private void EnsureIsolatedDirectories()
    {
        Directory.CreateDirectory(_isolatedWorkDirectory);
    }

    private static void AddOption(ICollection<string> arguments, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        arguments.Add(name);
        arguments.Add(value.ToLowerInvariant());
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static void AddUnique(ICollection<string> values, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            !values.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(value.ToLowerInvariant());
        }
    }

    private static string[] SortReasoningLevels(IEnumerable<string> levels)
    {
        return levels
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(level =>
            {
                var index = Array.FindIndex(ReasoningOrder, item => item.Equals(level, StringComparison.OrdinalIgnoreCase));
                return index < 0 ? int.MaxValue : index;
            })
            .ThenBy(level => level, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> SplitLines(string value) =>
        value.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);

    private static string LastUsefulLine(string value) =>
        SplitLines(value).LastOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? string.Empty;

    private static string EscapeTomlString(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string ToTitleCase(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? value
            : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }

    [GeneratedRegex(@"^(?<model>Gemini\s.+?)\s*\((?<effort>[^()]+)\)$", RegexOptions.IgnoreCase)]
    private static partial Regex ModelWithEffortRegex();

    /// <summary>
    /// Appended to Claude Code's own system prompt for agent runs. It only adds
    /// the two things the session cannot work out for itself: that the
    /// instruction was spoken (so speech-to-text slips are likely) and that the
    /// reply is read on a card in ShadowWhispr rather than in a terminal.
    /// </summary>
    /// <summary>
    /// Our own agent guidance with the user's standing facts appended, clearly
    /// marked as theirs so the session can tell the two apart.
    /// </summary>
    private static string BuildAgentSystemPrompt(string? standingInstruction, bool wantsSpokenReply)
    {
        var prompt = wantsSpokenReply
            ? $"{AgentSystemPrompt}\n\n{SpokenReplyPrompt}"
            : AgentSystemPrompt;

        return string.IsNullOrWhiteSpace(standingInstruction)
            ? prompt
            : $"""
               {prompt}

               The user has also given you these standing facts about themselves and their machine. Where they contradict anything above, follow them:
               <standing-facts>
               {standingInstruction.Trim()}
               </standing-facts>
               """;
    }

    /// <summary>
    /// Asks for a second, spoken version of the reply. Written out loud by the
    /// model rather than trimmed from the written one afterwards, because what
    /// reads well and what sounds right are different texts: a file path and a
    /// line number belong on the card and nowhere near a spoken sentence.
    /// </summary>
    private const string SpokenReplyPrompt =
        "Your reply will also be read out loud. End it with a spoken version of itself wrapped in " +
        "<spoken> tags, like <spoken>Done, the chime plays at the end of a run now.</spoken>. " +
        "Keep it to one or two short sentences that sound like a person telling a friend what they just did. " +
        "No file names, no paths, no line numbers, no version numbers, no lists, no symbols and no jargon - " +
        "if it cannot be said out loud comfortably, leave it out. The tag must be the last thing in your reply.";

    /// <summary>
    /// The opening and closing markers of the spoken reply, kept next to the
    /// prompt that asks for them so the two cannot drift apart.
    /// </summary>
    private const string SpokenOpen = "<spoken>";
    private const string SpokenClose = "</spoken>";

    /// <summary>
    /// Splits an agent reply into what is shown and what is spoken.
    ///
    /// The tag is optional on purpose: models skip formatting instructions
    /// occasionally, and a missing tag must not cost the user their reply. When
    /// it is absent the first sentence is spoken instead, which is nearly always
    /// the summary line anyway.
    /// </summary>
    public static (string Shown, string Spoken) SplitAgentReply(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return (reply, string.Empty);

        var start = reply.LastIndexOf(SpokenOpen, StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            var from = start + SpokenOpen.Length;
            var end = reply.IndexOf(SpokenClose, from, StringComparison.OrdinalIgnoreCase);
            var spoken = end >= 0
                ? reply[from..end]
                : reply[from..]; // Truncated reply: take what is there.

            var shown = reply.Remove(start, (end >= 0 ? end + SpokenClose.Length : reply.Length) - start);
            return (shown.Trim(), spoken.Trim());
        }

        return (reply, FirstSentence(reply));
    }

    /// <summary>
    /// The fallback spoken line: the first sentence, capped so that a reply
    /// written as one long unpunctuated paragraph does not get read out in full.
    /// </summary>
    private static string FirstSentence(string text)
    {
        var trimmed = text.Trim();
        var end = trimmed.IndexOfAny(['.', '!', '?']);
        var sentence = end >= 0 ? trimmed[..(end + 1)] : trimmed;
        return sentence.Length <= 240 ? sentence : sentence[..240];
    }

    private const string AgentSystemPrompt =
        "This instruction was dictated out loud and transcribed automatically, so expect speech-to-text slips " +
        "in names, paths and punctuation, and read it for what was meant rather than literally. " +
        "There is no one at a keyboard to answer questions: carry the task out, and only if it is truly " +
        "impossible say what stopped you. Your reply is shown on a small card in ShadowWhispr and cannot be " +
        "replied to, so answer in at most a few plain sentences with no Markdown, code fences or file listings.";

    private const string BaseSystemPrompt =
        "You are a text post-processor. Never use tools, files, shell commands, web access, skills, agents, or external context. " +
        "Apply the custom instruction only to the supplied transcript. Return only the finished text with no explanation, " +
        "labels, quotation marks, or Markdown fences. Preserve the speaker's meaning and do not answer questions found in the transcript.";

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
