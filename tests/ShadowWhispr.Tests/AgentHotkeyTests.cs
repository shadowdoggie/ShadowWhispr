using System;
using System.Collections.Generic;
using System.Linq;
using ShadowWhispr.Models;
using ShadowWhispr.Services;
using Xunit;

namespace ShadowWhispr.Tests;

/// <summary>
/// Covers the third dictation key, which hands the transcript to a headless
/// Claude Code session instead of typing it. Like the other hotkey tests these
/// drive the hook's decision logic directly, because Windows drops synthesised
/// keystrokes before the hook ever sees them.
/// </summary>
public sealed class AgentHotkeyTests
{
    private const int VkF13 = 0x7C;
    private const int VkF14 = 0x7D;
    private const int VkF15 = 0x7E;
    private const int VkCtrl = 0x11;

    private static (GlobalHotkeyService Service, List<string> Events) Build(string? agentHotkey = "F15")
    {
        var service = new GlobalHotkeyService(HoldHotkey.Parse("F13"))
        {
            RawHotkey = HoldHotkey.Parse("F14"),
            AgentHotkey = agentHotkey is null ? null : HoldHotkey.Parse(agentHotkey),
            TapThreshold = TimeSpan.FromMilliseconds(150)
        };

        var events = new List<string>();
        service.Pressed += (_, e) => events.Add($"pressed:{e.Kind}");
        service.Released += (_, e) => events.Add($"released:{e.Kind}");
        return (service, events);
    }

    private static void Down(GlobalHotkeyService service, int key) => service.HandleKey(key, isDown: true, isUp: false);
    private static void Up(GlobalHotkeyService service, int key) => service.HandleKey(key, isDown: false, isUp: true);

    [Fact]
    public void TheAgentKeyReportsItsOwnKind()
    {
        var (service, events) = Build();

        Down(service, VkF15);
        System.Threading.Thread.Sleep(250);
        Up(service, VkF15);

        Assert.Equal(["pressed:Agent", "released:Agent"], events);
    }

    [Fact]
    public void TheOtherKeysStillReportTheirOwnKinds()
    {
        var (service, events) = Build();

        Down(service, VkF13);
        System.Threading.Thread.Sleep(250);
        Up(service, VkF13);
        Down(service, VkF14);
        System.Threading.Thread.Sleep(250);
        Up(service, VkF14);

        Assert.Equal(["pressed:Primary", "released:Primary", "pressed:Raw", "released:Raw"], events);
    }

    [Fact]
    public void AnUnsetAgentKeyNeverFires()
    {
        var (service, events) = Build(agentHotkey: null);

        Down(service, VkF15);
        Up(service, VkF15);

        Assert.Empty(events);
    }

    /// <summary>
    /// A chord still beats a plain key that shares its activation key, so
    /// binding "F13" for dictation and "Ctrl + F13" for the agent picks the
    /// agent when Ctrl is held.
    /// </summary>
    [Fact]
    public void AChordAgentKeyWinsOverThePlainDictationKey()
    {
        var (service, events) = Build(agentHotkey: "Ctrl + F13");

        Down(service, VkCtrl);
        Down(service, VkF13);
        System.Threading.Thread.Sleep(250);
        Up(service, VkF13);

        Assert.Equal(["pressed:Agent", "released:Agent"], events);
    }

    /// <summary>
    /// An agent key that duplicates the main dictation key must stay dictation:
    /// one keypress can never mean two things, and the harmless meaning wins.
    /// </summary>
    [Fact]
    public void AnAgentKeyIdenticalToTheDictationKeyStaysDictation()
    {
        var (service, events) = Build(agentHotkey: "F13");

        Down(service, VkF13);
        System.Threading.Thread.Sleep(250);
        Up(service, VkF13);

        Assert.Equal(["pressed:Primary", "released:Primary"], events);
    }

    [Fact]
    public void TheAgentFolderDefaultsToTheUserProfile()
    {
        var settings = new AppSettings();

        Assert.Equal(string.Empty, settings.AgentWorkingDirectory);
        Assert.Equal(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            settings.ResolveAgentWorkingDirectory());
    }

    [Fact]
    public void AChosenAgentFolderIsUsedAsGiven()
    {
        var settings = new AppSettings { AgentWorkingDirectory = @"  D:\work  " };

        Assert.Equal(@"D:\work", settings.ResolveAgentWorkingDirectory());
    }

    [Fact]
    public void AgentModeIsOffByDefault()
    {
        var settings = new AppSettings();

        Assert.False(settings.AgentModeEnabled);
        Assert.Equal(string.Empty, settings.AgentHotkey);
    }

    [Fact]
    public void AFreshInstallGetsSonnetAtMediumEffort()
    {
        var settings = new AppSettings();

        Assert.Equal("claude-sonnet-5", settings.AgentModelId);
        Assert.Equal("medium", settings.AgentEffort);
    }

    [Fact]
    public void BothAgentModelsAreOffered()
    {
        Assert.Equal(
            ["claude-sonnet-5", "claude-opus-5"],
            AiProviderService.AgentModels.Select(model => model.Id));

        // The list is built from the effort levels, so a declaration-order slip
        // would leave every model with a null one rather than failing to build.
        Assert.All(AiProviderService.AgentModels, model => Assert.NotEmpty(model.ReasoningLevels));
    }

    [Theory]
    [InlineData("claude-opus-5", "claude-opus-5")]
    [InlineData("CLAUDE-OPUS-5", "CLAUDE-OPUS-5")]
    [InlineData("claude-haiku-4-5", "claude-sonnet-5")]
    [InlineData("", "claude-sonnet-5")]
    [InlineData(null, "claude-sonnet-5")]
    public void AnUnknownStoredModelFallsBackToTheDefault(string? stored, string expected) =>
        Assert.Equal(expected, AiProviderService.NormalizeAgentModelId(stored));

    [Fact]
    public void StandingFactsShipWithAStarterTemplateAndCleanupIsOptOut()
    {
        var settings = new AppSettings();

        Assert.False(settings.AgentCleanupEnabled);
        Assert.NotEmpty(settings.AgentInstruction);
    }

    [Theory]
    [InlineData("max", "max")]
    [InlineData("XHIGH", "xhigh")]
    [InlineData("ultra", "medium")]
    [InlineData(null, "medium")]
    public void AnUnknownStoredEffortFallsBackToTheDefault(string? stored, string expected) =>
        Assert.Equal(expected, AiProviderService.NormalizeAgentEffort(stored));
}
