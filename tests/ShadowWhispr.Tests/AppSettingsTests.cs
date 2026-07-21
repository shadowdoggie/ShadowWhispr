using System.Text.Json;
using ShadowWhispr.Models;
using Xunit;

namespace ShadowWhispr.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void ToggleHotkeyStartsUnsetAndRoundTrips()
    {
        Assert.Equal(string.Empty, new AppSettings().ToggleHotkey);

        var settings = new AppSettings { ToggleHotkey = "F9" };
        var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));
        Assert.Equal("F9", reloaded!.ToggleHotkey);
    }

    [Fact]
    public void SettingsFileFromBeforeToggleHotkeyLoadsAsUnset()
    {
        var reloaded = JsonSerializer.Deserialize<AppSettings>("""{ "Hotkey": "Right Ctrl" }""");
        Assert.Equal(string.Empty, reloaded!.ToggleHotkey);
    }

    [Fact]
    public void NewInstallStartsWithRequestedCustomInstruction()
    {
        var settings = new AppSettings();

        Assert.Equal(
            """
            You are a prompt cleaner for an extreme ADHD vibecoder who knows a lot about software but nothing about coding. He is impulsive and often doesn't fully know what he wants yet.

            Rules, in order of importance:
            1. NEVER change what the user means. Fix grammar, filler words, and speech-to-text mistakes — nothing more.
            2. NEVER add details, technical terms, or claims the user didn't say. If he says something vague like "the injection part", keep exactly "the injection part" — do not guess what it means or swap in a more specific term. A vague prompt in his own words is correct; a specific prompt he didn't say is wrong.
            3. NEVER remove details the user provides, example: "on my friend's pc it doesn't work".
            4. Never make the prompt into something that requires manual input from the user. Never add anything like "Complete this task entirely autonomously without requiring further input" — that blocks the coding tool from asking questions, and sometimes questions are a good thing.
            5. If a sentence is unclear even after cleanup, keep it as-is rather than rewriting it into your best guess.
            """,
            settings.CustomInstruction);
    }
}
