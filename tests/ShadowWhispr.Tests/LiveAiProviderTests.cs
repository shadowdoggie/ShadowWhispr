using ShadowWhispr.Services;
using Xunit;

namespace ShadowWhispr.Tests;

public sealed class LiveAiProviderTests
{
    [Theory]
    [InlineData(AiProviderService.Claude)]
    [InlineData(AiProviderService.Codex)]
    [InlineData(AiProviderService.Gemini)]
    [Trait("Category", "Live")]
    public async Task SignedInProviderCanCleanText(string provider)
    {
        if (Environment.GetEnvironmentVariable("SHADOWWHISPR_RUN_LIVE_AI_TESTS") != "1") return;

        var service = new AiProviderService(TimeSpan.FromMinutes(5));
        var models = await service.DiscoverModelsAsync(provider, TestContext.Current.CancellationToken);
        var model = provider switch
        {
            AiProviderService.Claude => models.First(item => item.Id == "claude-sonnet-5"),
            AiProviderService.Codex => models.FirstOrDefault(item => item.Id.Contains("mini")) ?? models[0],
            AiProviderService.Gemini => models.First(item => item.DisplayName.Contains("Flash")),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };
        var effort = model.ReasoningLevels.Contains("low") ? "low" : model.DefaultReasoningLevel;

        var result = await service.ProcessAsync(
            provider,
            model.Id,
            effort,
            "Add normal punctuation. Do not change or add words.",
            "hello world",
            TestContext.Current.CancellationToken);

        Assert.Contains("hello", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("world", result, StringComparison.OrdinalIgnoreCase);
    }
}
