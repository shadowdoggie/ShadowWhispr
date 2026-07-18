using ShadowWhispr.Services;
using Xunit;

namespace ShadowWhispr.Tests;

public sealed class AiProviderDiscoveryTests
{
    private readonly AiProviderService _service = new(TimeSpan.FromSeconds(60));

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
