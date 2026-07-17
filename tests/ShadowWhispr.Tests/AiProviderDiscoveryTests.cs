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
    [InlineData(AiProviderService.Kimi, 3)]
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
