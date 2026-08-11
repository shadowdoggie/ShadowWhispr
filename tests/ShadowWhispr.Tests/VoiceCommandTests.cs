using ShadowWhispr.Services;
using Xunit;

namespace ShadowWhispr.Tests;

public sealed class VoiceCommandTests
{
    [Theory]
    [InlineData("open agy", "AGY", "agy")]
    [InlineData("Open AGY.", "AGY", "agy")]
    [InlineData("open 83!", "AGY", "agy")]
    [InlineData("open anti gravity", "AGY", "agy")]
    [InlineData("open antigravity", "AGY", "agy")]
    [InlineData("open anti-gravity cli", "AGY", "agy")]
    [InlineData("open code", "OpenCode", "opencode")]
    [InlineData("opencode", "OpenCode", "opencode")]
    [InlineData("open coat", "OpenCode", "opencode")]
    [InlineData("Open codecs.", "Codex CLI", "codex")]
    [InlineData("open open code", "OpenCode", "opencode")]
    [InlineData("open codex", "Codex CLI", "codex")]
    [InlineData("open codec", "Codex CLI", "codex")]
    [InlineData("codec cli", "Codex CLI", "codex")]
    [InlineData("codex cli", "Codex CLI", "codex")]
    [InlineData("open claude", "Claude Code", "claude")]
    [InlineData("cloud code", "Claude Code", "claude")]
    [InlineData("claude code", "Claude Code", "claude")]
    [InlineData("open cloud coat", "Claude Code", "claude")]
    [InlineData("open grok", "Grok Build", "grok")]
    [InlineData("Open Groc.", "Grok Build", "grok")]
    [InlineData("Open Grock build.", "Grok Build", "grok")]
    [InlineData("grog build", "Grok Build", "grok")]
    [InlineData("open frog build", "Grok Build", "grok")]
    public void VoiceCommandsMatchPhoneticVariations(string input, string expectedName, string expectedExe)
    {
        Assert.True(VoiceCommandService.TryMatchCommand(input, out var result));
        Assert.NotNull(result);
        Assert.Equal(expectedName, result!.MatchedName);
        Assert.Equal(expectedExe, result.Executable);
    }

    [Theory]
    [InlineData("I want to open code in my editor")]
    [InlineData("This is a prompt about agy")]
    [InlineData("Just regular text being dictated")]
    [InlineData("")]
    [InlineData(null)]
    public void NonCommandSentencesDoNotMatch(string? input)
    {
        Assert.False(VoiceCommandService.TryMatchCommand(input, out var result));
        Assert.Null(result);
    }
}
