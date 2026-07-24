using System.Threading.Tasks;
using ShadowWhispr.Services;
using Xunit;

namespace ShadowWhispr.Tests;

public sealed class ProviderLoginStatusTests
{
    /// <summary>
    /// Antigravity has no way to be asked, so guessing is not allowed: Gemini's
    /// Login button must stay usable rather than being greyed out on a hunch.
    /// </summary>
    [Fact]
    public async Task GeminiNeverClaimsToKnow()
    {
        var status = await new AiProviderService().GetLoginStatusAsync(AiProviderService.Gemini, TestContext.Current.CancellationToken);

        Assert.Equal(ProviderLoginStatus.Unknown, status);
    }

    /// <summary>
    /// Runs against the real CLIs on this machine. Whatever they answer, it has
    /// to be one of the three known outcomes and must not throw — a failing
    /// status check may never break the settings screen.
    /// </summary>
    [Theory]
    [InlineData(AiProviderService.Claude)]
    [InlineData(AiProviderService.Codex)]
    public async Task StatusCheckAlwaysAnswersWithoutThrowing(string provider)
    {
        var status = await new AiProviderService().GetLoginStatusAsync(provider, TestContext.Current.CancellationToken);

        Assert.Contains(status, new[]
        {
            ProviderLoginStatus.Unknown,
            ProviderLoginStatus.LoggedIn,
            ProviderLoginStatus.LoggedOut
        });
    }
}
