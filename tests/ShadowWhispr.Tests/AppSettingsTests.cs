using ShadowWhispr.Models;
using Xunit;

namespace ShadowWhispr.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void DefaultCustomInstructionStaysLocked()
    {
        var settings = new AppSettings();

        Assert.Equal(
            "You are a prompt improver/rebuilder for an extreme adhd vibecoder guy who knows alot about software but nothing about coding. The user you improve/rebuild this prompt for is very impulsive so often doesn't really know what he wants.",
            settings.CustomInstruction);
    }
}
