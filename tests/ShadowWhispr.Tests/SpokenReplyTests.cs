using System.Text.Json;
using ShadowWhispr.Models;
using ShadowWhispr.Services;
using Xunit;

namespace ShadowWhispr.Tests;

/// <summary>
/// An agent reply carries two texts: the one shown on screen and the shorter one
/// read out loud. Splitting them wrongly is worse than not speaking at all - it
/// either reads file paths out or leaves markup on screen - so every shape a
/// model might return is pinned down here.
/// </summary>
public sealed class SpokenReplyTests
{
    [Fact]
    public void TheSpokenPartIsTakenOutOfWhatIsShown()
    {
        const string reply =
            "Updated TonePlayer.cs and rebuilt the project.\n<spoken>Done, the chime works now.</spoken>";

        var (shown, spoken) = AiProviderService.SplitAgentReply(reply);

        Assert.Equal("Updated TonePlayer.cs and rebuilt the project.", shown);
        Assert.Equal("Done, the chime works now.", spoken);
    }

    /// <summary>
    /// The tag is an instruction the model can ignore, and a reply without one
    /// must still be shown in full and still say something out loud.
    /// </summary>
    [Fact]
    public void AReplyWithNoTagIsKeptWholeAndSpeaksItsFirstSentence()
    {
        const string reply = "I renamed the folder. It has three files in it now.";

        var (shown, spoken) = AiProviderService.SplitAgentReply(reply);

        Assert.Equal(reply, shown);
        Assert.Equal("I renamed the folder.", spoken);
    }

    /// <summary>
    /// A reply cut off mid-tag still has a usable spoken half, and must not
    /// leave the opening marker sitting on screen.
    /// </summary>
    [Fact]
    public void AnUnclosedTagStillSplits()
    {
        const string reply = "Sorted the screenshots.\n<spoken>All done, they are on your desktop.";

        var (shown, spoken) = AiProviderService.SplitAgentReply(reply);

        Assert.Equal("Sorted the screenshots.", shown);
        Assert.Equal("All done, they are on your desktop.", spoken);
        Assert.DoesNotContain("<spoken>", shown, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One long unpunctuated paragraph would otherwise be read out in its
    /// entirety, which is exactly the wall of speech the feature exists to avoid.
    /// </summary>
    [Fact]
    public void AnUnpunctuatedReplyIsCappedBeforeItIsSpoken()
    {
        var reply = new string('a', 900);

        var (_, spoken) = AiProviderService.SplitAgentReply(reply);

        Assert.True(spoken.Length <= 240, $"spoken was {spoken.Length} characters");
    }

    [Fact]
    public void AnEmptyReplyIsHandled()
    {
        var (shown, spoken) = AiProviderService.SplitAgentReply(string.Empty);

        Assert.Equal(string.Empty, shown);
        Assert.Equal(string.Empty, spoken);
    }

    /// <summary>
    /// A hand-edited or outdated settings file must still speak, rather than
    /// failing on a voice Gemini has never heard of.
    /// </summary>
    [Theory]
    [InlineData("Leda", "Leda")]
    [InlineData("leda", "Leda")]
    [InlineData("Zephyr", "Zephyr")]
    [InlineData("NotARealVoice", GeminiVoiceService.DefaultVoice)]
    [InlineData("", GeminiVoiceService.DefaultVoice)]
    [InlineData(null, GeminiVoiceService.DefaultVoice)]
    public void UnknownVoicesFallBackToTheDefault(string? stored, string expected)
    {
        Assert.Equal(expected, GeminiVoiceService.NormalizeVoice(stored));
    }

    [Fact]
    public void TheDefaultVoiceIsOneWeActuallyOffer()
    {
        Assert.Contains(GeminiVoiceService.Voices, voice => voice.Id == GeminiVoiceService.DefaultVoice);
    }

    /// <summary>
    /// Speaking costs money and sends text to Google, so it must be off until
    /// the user turns it on, both on a fresh install and after an update from a
    /// version that had no such setting.
    /// </summary>
    [Fact]
    public void SpokenRepliesAreOffUntilTurnedOn()
    {
        Assert.False(new AppSettings().VoiceReplyEnabled);
        Assert.False(new AppSettings().WillSpeakAgentReply);

        const string olderSettings = """{ "AiEnabled": true, "AgentModeEnabled": true }""";
        var settings = JsonSerializer.Deserialize<AppSettings>(olderSettings);

        Assert.NotNull(settings);
        Assert.False(settings.VoiceReplyEnabled);
        Assert.False(settings.WillSpeakAgentReply);
    }

    /// <summary>
    /// Turned on without a key there is nothing to call, and the run must not
    /// try. This is the same guard shape as WillCleanAgentInstruction.
    /// </summary>
    [Fact]
    public void SpeakingNeedsBothTheSwitchAndAKey()
    {
        Assert.False(new AppSettings { VoiceReplyEnabled = true }.WillSpeakAgentReply);
        Assert.False(new AppSettings { VoiceApiKey = "abc" }.WillSpeakAgentReply);
        Assert.False(new AppSettings { VoiceReplyEnabled = true, VoiceApiKey = "   " }.WillSpeakAgentReply);
        Assert.True(new AppSettings { VoiceReplyEnabled = true, VoiceApiKey = "abc" }.WillSpeakAgentReply);
    }

    [Fact]
    public void TheVoiceChoiceAndVolumeSurviveARelaunch()
    {
        var saved = new AppSettings
        {
            VoiceReplyEnabled = true,
            VoiceApiKey = "test-key",
            VoiceName = "Aoede",
            VoiceVolume = 0.6
        };

        var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved));

        Assert.NotNull(reloaded);
        Assert.True(reloaded.VoiceReplyEnabled);
        Assert.Equal("test-key", reloaded.VoiceApiKey);
        Assert.Equal("Aoede", reloaded.VoiceName);
        Assert.Equal(0.6, reloaded.VoiceVolume);
    }
}
