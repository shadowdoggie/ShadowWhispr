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

    /// <summary>
    /// Pressing the agent key again must actually stop the session, not just
    /// stop waiting for it: a headless Claude Code that keeps running would go
    /// on acting on the machine after the user called it off. Proven by the
    /// claude process count returning to what it was before the run.
    /// </summary>
    [Fact]
    [Trait("Category", "Live")]
    public async Task CancellingAnAgentRunKillsTheClaudeProcess()
    {
        if (Environment.GetEnvironmentVariable("SHADOWWHISPR_RUN_LIVE_AI_TESTS") != "1") return;

        // Identified by process id rather than by count: node processes come and
        // go on a developer machine, so a count says nothing about whether this
        // particular run's processes are gone.
        static HashSet<int> RunnerPids() =>
        [
            .. System.Diagnostics.Process.GetProcessesByName("claude").Select(p => p.Id),
            .. System.Diagnostics.Process.GetProcessesByName("node").Select(p => p.Id)
        ];

        var before = RunnerPids();
        var service = new AiProviderService();
        using var cancel = new CancellationTokenSource();

        // Deliberately slow, so the run is still going when it is called off.
        // A prompt the model can simply answer finishes in seconds and would
        // leave nothing to cancel.
        var run = service.RunAgentAsync(
            "Run this exact command and wait for it to finish: powershell -NoProfile -Command \"Start-Sleep -Seconds 240\"",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            cancellationToken: cancel.Token);

        // Polled rather than slept through: waiting a fixed time races a run
        // that is either slower or faster to get going than the guess.
        int[] started = [];
        for (var attempt = 0; attempt < 30 && started.Length == 0; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            started = RunnerPids().Except(before).ToArray();
        }

        Assert.True(started.Length > 0, "the agent run never started a process");

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // Process teardown is not instant, so allow a moment before judging it.
        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var survivors = RunnerPids().Intersect(started).ToArray();
        Assert.True(
            survivors.Length == 0,
            $"the cancelled run left {survivors.Length} process(es) alive: {string.Join(", ", survivors)}");
    }
}
