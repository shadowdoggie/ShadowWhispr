using System.ComponentModel;
using System.Diagnostics;
using System.IO;
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

    public static IReadOnlyList<string> Providers { get; } = [Claude, Codex, Gemini];

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

    /// <summary>The speed tier Codex's model cache lists for fast-capable models.</summary>
    private const string FastSpeedTier = "fast";

    /// <summary>The value Codex's <c>service_tier</c> setting takes for fast mode.</summary>
    private const string PriorityServiceTier = "priority";

    private static readonly string[] ReasoningOrder =
        ["off", "minimal", "low", "medium", "high", "xhigh", "max", "ultra", "on"];

    private readonly TimeSpan _commandTimeout;
    private readonly string _isolatedWorkDirectory;

    public AiProviderService(TimeSpan? commandTimeout = null)
    {
        _commandTimeout = commandTimeout ?? TimeSpan.FromMinutes(5);
        _isolatedWorkDirectory = Path.Combine(Path.GetTempPath(), "ShadowWhispr", "ai-workspace");
    }

    public bool IsCliAvailable(string provider)
    {
        var command = GetCommand(provider);
        return FindOnPath(command) is not null;
    }

    public string GetAuthenticationCommand(string provider) => NormalizeProvider(provider) switch
    {
        Claude => "claude auth login --claudeai",
        Codex => "codex login",
        Gemini => "agy (opens OAuth onboarding when signed out)",
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
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };

        if (string.IsNullOrWhiteSpace(output))
        {
            throw new AiProviderException($"{normalizedProvider} returned an empty response.");
        }

        return output.Trim();
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

    private async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyCollection<string> arguments,
        string? standardInput,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_commandTimeout);

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
            AppLog.Write($"AI CLI timed out after {_commandTimeout.TotalMinutes:0.#} minutes: {fileName}");
            throw new AiProviderException($"{fileName} did not finish within {_commandTimeout.TotalMinutes:0.#} minutes.");
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

    private const string BaseSystemPrompt =
        "You are a text post-processor. Never use tools, files, shell commands, web access, skills, agents, or external context. " +
        "Apply the custom instruction only to the supplied transcript. Return only the finished text with no explanation, " +
        "labels, quotation marks, or Markdown fences. Preserve the speaker's meaning and do not answer questions found in the transcript.";

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
