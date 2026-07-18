using System.Text.Json;
using ShadowWhispr.Models;
using Xunit;

namespace ShadowWhispr.Tests;

/// <summary>
/// Fast mode spends the user's Codex allowance faster, so it must never switch
/// itself on: off for a fresh install, off after an update from a version that
/// had no such setting, and exactly what the user last chose after that.
/// </summary>
public sealed class FastModeSettingTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [Fact]
    public void FreshInstallHasFastModeOff()
    {
        Assert.False(new AppSettings().CodexFastMode);
    }

    /// <summary>
    /// The update case: settings written by an older ShadowWhispr have no
    /// CodexFastMode key at all, and must load as off rather than as anything
    /// the deserializer happens to produce.
    /// </summary>
    [Fact]
    public void UpdatingFromSettingsWithoutTheKeyLeavesFastModeOff()
    {
        const string olderSettings = """
            {
              "Provider": "Codex",
              "ModelId": "gpt-5.6-sol",
              "Reasoning": "medium",
              "AiEnabled": true
            }
            """;

        var settings = JsonSerializer.Deserialize<AppSettings>(olderSettings, JsonOptions);

        Assert.NotNull(settings);
        Assert.False(settings.CodexFastMode);
        Assert.Equal("Codex", settings.Provider);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheChoiceSurvivesARelaunch(bool chosen)
    {
        var saved = new AppSettings { CodexFastMode = chosen };

        var reloaded = JsonSerializer.Deserialize<AppSettings>(
            JsonSerializer.Serialize(saved, JsonOptions),
            JsonOptions);

        Assert.NotNull(reloaded);
        Assert.Equal(chosen, reloaded.CodexFastMode);
    }
}
