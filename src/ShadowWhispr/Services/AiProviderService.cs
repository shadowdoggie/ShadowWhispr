using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShadowWhispr.Models;

namespace ShadowWhispr.Services;

public sealed partial class AiProviderService
{
    public const string Claude = "Claude";
    public const string Codex = "Codex";
    public const string Gemini = "Gemini";

    public static IReadOnlyList<string> Providers { get; } = [Claude, Codex, Gemini];

    private static readonly IReadOnlyList<AiModelOption> ClaudeModels =
    [
        new(Claude, "claude-fable-5", "Claude Fable 5", ["low", "medium", "high", "xhigh", "max"], "high"),
        new(Claude, "claude-opus-4-8", "Claude Opus 4.8", ["low", "medium", "high", "xhigh", "max"], "high"),
        new(Claude, "claude-sonnet-5", "Claude Sonnet 5", ["low", "medium", "high", "xhigh", "max"], "high"),
        new(Claude, "claude-haiku-4-5", "Claude Haiku 4.5", [], null)
    ];

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
        CancellationToken cancellationToken = default)
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
            Codex => await ProcessWithCodexAsync(modelId, reasoning, prompt, cancellationToken),
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
                    defaultEffort)));
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

    private async Task<IReadOnlyList<AiModelOption>> DiscoverGeminiModelsAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            GetCommand(Gemini),
            ["models"],
            standardInput: null,
            workingDirectory: _isolatedWorkDirectory,
            environment: null,
            cancellationToken);

        EnsureSuccess(Gemini, result);
        var grouped = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in SplitLines(result.StandardOutput))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("Gemini ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = ModelWithEffortRegex().Match(line);
            var modelName = match.Success ? match.Groups["model"].Value.Trim() : line;
            var effort = match.Success ? match.Groups["effort"].Value.ToLowerInvariant() : null;
            if (!grouped.TryGetValue(modelName, out var efforts))
            {
                efforts = [];
                grouped[modelName] = efforts;
            }

            AddUnique(efforts, effort);
        }

        return grouped
            .Select(pair =>
            {
                var efforts = SortReasoningLevels(pair.Value);
                return new AiModelOption(
                    Gemini,
                    pair.Key,
                    pair.Key,
                    efforts,
                    efforts.Contains("medium", StringComparer.OrdinalIgnoreCase)
                        ? "medium"
                        : efforts.FirstOrDefault());
            })
            .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
        var selectedModel = ModelWithEffortRegex().IsMatch(modelId) || string.IsNullOrWhiteSpace(reasoning)
            ? modelId
            : $"{modelId} ({ToTitleCase(reasoning)})";

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

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
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

    private static string? FindOnPath(string command)
    {
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var candidates = Path.HasExtension(command)
            ? [command]
            : extensions.Select(extension => command + extension.ToLowerInvariant()).Prepend(command);

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var candidate in candidates)
            {
                var fullPath = Path.Combine(directory.Trim('"'), candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
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
