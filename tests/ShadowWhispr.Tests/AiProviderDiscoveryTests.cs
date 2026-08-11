using ShadowWhispr.Services;
using Xunit;

namespace ShadowWhispr.Tests;

public sealed class AiProviderDiscoveryTests
{
    private readonly AiProviderService _service = new(TimeSpan.FromSeconds(60));

    [Fact]
    public void ParsesTabbedAgyOutputAsUniqueModelsWithReasoningLevels()
    {
        const string output =
            "gemini-3.6-flash-high\tGemini 3.6 Flash (High)\n" +
            "gemini-3.6-flash-medium\tGemini 3.6 Flash (Medium)\n" +
            "gemini-3.6-flash-low\tGemini 3.6 Flash (Low)\n" +
            "gemini-3.1-pro-high\tGemini 3.1 Pro (High)\n" +
            "gemini-3.1-pro-low\tGemini 3.1 Pro (Low)\n" +
            "claude-sonnet-4-6\tClaude Sonnet 4.6 (Thinking)";

        var models = AiProviderService.ParseGeminiModelLines(output);

        Assert.Equal(2, models.Count);
        var flash = Assert.Single(models, model => model.Id == "gemini-3.6-flash");
        Assert.Equal("Gemini 3.6 Flash", flash.DisplayName);
        Assert.Equal(["low", "medium", "high"], flash.ReasoningLevels);
        Assert.Equal("high", flash.DefaultReasoningLevel);
        Assert.DoesNotContain(models, model => model.Id.Contains('\t'));
    }

    [Theory]
    [InlineData(AiProviderService.Claude, 4)]
    [InlineData(AiProviderService.Codex, 1)]
    [InlineData(AiProviderService.Gemini, 2)]
    public async Task DiscoversSignedInProviderModels(string provider, int minimumCount)
    {
        Assert.True(_service.IsCliAvailable(provider), $"{provider} CLI is missing");
        var models = await _service.DiscoverModelsAsync(provider, TestContext.Current.CancellationToken);
        Assert.True(models.Count >= minimumCount, $"Expected at least {minimumCount} {provider} models");
        Assert.All(models, model =>
        {
            Assert.False(string.IsNullOrWhiteSpace(model.Id));
            Assert.False(string.IsNullOrWhiteSpace(model.DisplayName));
        });
    }

    /// <summary>
    /// Fast mode must only be offered where Codex advertises the tier. Codex
    /// treats an unsupported tier as "omitted from requests", so a wrongly
    /// offered switch would look like it worked while doing nothing.
    /// </summary>
    [Fact]
    public async Task CodexModelsReportTheirFastModeSupport()
    {
        var models = await _service.DiscoverModelsAsync(
            AiProviderService.Codex,
            TestContext.Current.CancellationToken);
        Assert.NotEmpty(models);
        Assert.Contains(models, model => model.SupportsFastMode);
    }

    [Theory]
    [InlineData(AiProviderService.Claude)]
    [InlineData(AiProviderService.Gemini)]
    public async Task OnlyCodexEverOffersFastMode(string provider)
    {
        var models = await _service.DiscoverModelsAsync(provider, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(models, model => model.SupportsFastMode);
    }

    [Fact]
    public async Task ClaudeHaikuDoesNotOfferUnsupportedEffortPicker()
    {
        var models = await _service.DiscoverModelsAsync(
            AiProviderService.Claude,
            TestContext.Current.CancellationToken);
        var haiku = Assert.Single(models, model => model.Id == "claude-haiku-4-5");
        Assert.Empty(haiku.ReasoningLevels);
    }
}
