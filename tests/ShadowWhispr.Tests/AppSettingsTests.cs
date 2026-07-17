using ShadowWhispr.Models;
using Xunit;

namespace ShadowWhispr.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void NewInstallStartsWithRequestedCustomInstruction()
    {
        var settings = new AppSettings();

        Assert.Equal(
            "You are a prompt improver/rebuilder, for an extreme adhd vibecoder guy who knows alot about software but nothing about coding. The user you improve/rebuild this prompt for is very impulsive so often doesn't really know what he wants. Don't ever make the prompt into something that requires manual input from the user. Don't ever say anything like this or similar: \"Complete this task entirely autonomously without requiring further input.\", because this causes the vibecoding tool to not be able to ask questions if it wants to, and sometimes questions are a good thing.",
            settings.CustomInstruction);
    }
}
