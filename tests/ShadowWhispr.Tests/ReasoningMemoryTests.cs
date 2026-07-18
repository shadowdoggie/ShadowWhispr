using System.Text.Json;
using ShadowWhispr.Models;
using Xunit;

namespace ShadowWhispr.Tests;

/// <summary>
/// Each provider keeps its own reasoning effort, so switching away and back
/// restores what was chosen rather than inheriting the other provider's value.
/// </summary>
public sealed class ReasoningMemoryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Fact]
    public void EachProviderKeepsItsOwnEffort()
    {
        var settings = new AppSettings();
        settings.SetReasoningFor("Claude", "high");
        settings.SetReasoningFor("Codex", "medium");
        settings.SetReasoningFor("Gemini", "low");

        Assert.Equal("high", settings.GetReasoningFor("Claude"));
        Assert.Equal("medium", settings.GetReasoningFor("Codex"));
        Assert.Equal("low", settings.GetReasoningFor("Gemini"));
    }

    [Fact]
    public void SwitchingProviderDoesNotOverwriteTheOtherProvidersEffort()
    {
        var settings = new AppSettings();
        settings.SetReasoningFor("Claude", "max");

        // Switching to Codex and choosing a different effort there.
        settings.SetReasoningFor("Codex", "low");

        Assert.Equal("max", settings.GetReasoningFor("Claude"));
    }

    /// <summary>
    /// The reasoning list is empty for a moment while a provider's models are
    /// discovered. Saving in that window must not wipe the remembered value.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankEffortNeverErasesWhatWasRemembered(string? blank)
    {
        var settings = new AppSettings();
        settings.SetReasoningFor("Codex", "xhigh");

        settings.SetReasoningFor("Codex", blank);

        Assert.Equal("xhigh", settings.GetReasoningFor("Codex"));
    }

    [Fact]
    public void RememberedEffortsSurviveAnAppRestart()
    {
        var settings = new AppSettings { Provider = "Codex" };
        settings.SetReasoningFor("Claude", "high");
        settings.SetReasoningFor("Codex", "medium");

        var reloaded = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings, JsonOptions), JsonOptions);

        Assert.NotNull(reloaded);
        Assert.Equal("high", reloaded!.GetReasoningFor("Claude"));
        Assert.Equal("medium", reloaded.GetReasoningFor("Codex"));
    }

    /// <summary>
    /// A settings file written before per-provider memory existed has only the
    /// single shared value; the provider that was in use must keep it.
    /// </summary>
    [Fact]
    public void AnOlderSettingsFileKeepsTheEffortItAlreadyHad()
    {
        const string legacyJson = """
            {
              "Provider": "Codex",
              "ModelId": "gpt-5.6-sol",
              "Reasoning": "medium"
            }
            """;

        var settings = JsonSerializer.Deserialize<AppSettings>(legacyJson, JsonOptions);

        Assert.NotNull(settings);
        Assert.Equal("medium", settings!.GetReasoningFor("Codex"));
        // Nothing was ever chosen for the other providers, so they start unset
        // and fall back to each model's own default.
        Assert.Null(settings.GetReasoningFor("Claude"));
    }

    [Fact]
    public void EachProviderKeepsItsOwnModel()
    {
        var settings = new AppSettings();
        settings.SetModelFor("Claude", "claude-opus-4-8");
        settings.SetModelFor("Codex", "gpt-5.6-sol");

        Assert.Equal("claude-opus-4-8", settings.GetModelFor("Claude"));
        Assert.Equal("gpt-5.6-sol", settings.GetModelFor("Codex"));
    }

    [Fact]
    public void ModelAndEffortSurviveAnAppRestartTogether()
    {
        var settings = new AppSettings { Provider = "Claude" };
        settings.SetModelFor("Claude", "claude-opus-4-8");
        settings.SetReasoningFor("Claude", "max");
        settings.SetModelFor("Codex", "gpt-5.6-sol");
        settings.SetReasoningFor("Codex", "medium");

        var reloaded = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(settings, JsonOptions), JsonOptions);

        Assert.NotNull(reloaded);
        Assert.Equal("claude-opus-4-8", reloaded!.GetModelFor("Claude"));
        Assert.Equal("max", reloaded.GetReasoningFor("Claude"));
        Assert.Equal("gpt-5.6-sol", reloaded.GetModelFor("Codex"));
        Assert.Equal("medium", reloaded.GetReasoningFor("Codex"));
    }

    [Fact]
    public void AnOlderSettingsFileKeepsTheModelItAlreadyHad()
    {
        const string legacyJson = """
            {
              "Provider": "Codex",
              "ModelId": "gpt-5.6-sol",
              "Reasoning": "medium"
            }
            """;

        var settings = JsonSerializer.Deserialize<AppSettings>(legacyJson, JsonOptions);

        Assert.NotNull(settings);
        Assert.Equal("gpt-5.6-sol", settings!.GetModelFor("Codex"));
        Assert.Null(settings.GetModelFor("Claude"));
    }

    [Fact]
    public void ABlankModelNeverErasesWhatWasRemembered()
    {
        var settings = new AppSettings();
        settings.SetModelFor("Claude", "claude-opus-4-8");

        settings.SetModelFor("Claude", "");

        Assert.Equal("claude-opus-4-8", settings.GetModelFor("Claude"));
    }

    [Fact]
    public void ProviderNamesAreMatchedRegardlessOfCase()
    {
        // Deserialization loses the case-insensitive comparer, so this is the
        // realistic path: a dictionary built by System.Text.Json.
        var settings = JsonSerializer.Deserialize<AppSettings>("""
            { "ReasoningByProvider": { "claude": "xhigh" } }
            """, JsonOptions);

        Assert.NotNull(settings);
        Assert.Equal("xhigh", settings!.GetReasoningFor("Claude"));

        settings.SetReasoningFor("Claude", "low");

        Assert.Equal("low", settings.GetReasoningFor("Claude"));
        Assert.Equal("low", settings.GetReasoningFor("claude"));
        Assert.Single(settings.ReasoningByProvider);
    }
}
