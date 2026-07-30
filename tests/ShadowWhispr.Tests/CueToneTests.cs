using System;
using ShadowWhispr.Services;
using Xunit;

namespace ShadowWhispr.Tests;

/// <summary>
/// The cues are the only feedback there is while the ShadowWhispr window is
/// hidden, so each one has to be tellable apart from the others by ear alone.
/// </summary>
public sealed class CueToneTests
{
    [Fact]
    public void TheCancelCueIsLongerThanTheOthers()
    {
        var pressed = TonePlayer.CreateCue(0.18, 620, 880);
        var released = TonePlayer.CreateCue(0.16, 700, 420);
        var cancelled = TonePlayer.CreateCue(0.16, 520, 390, 290);

        Assert.Equal(pressed.Length, released.Length);
        Assert.True(
            cancelled.Length > released.Length,
            "the cancel cue has to be audibly longer than the end-of-dictation cue");
    }

    [Fact]
    public void EveryCueStartsAndEndsAtSilence()
    {
        foreach (var cue in new[]
                 {
                     TonePlayer.CreateCue(0.18, 620, 880),
                     TonePlayer.CreateCue(0.16, 700, 420),
                     TonePlayer.CreateCue(0.16, 520, 390, 290)
                 })
        {
            // A cue that starts or ends part-way up its waveform is heard as a
            // click, which is what the fades either side exist to prevent.
            Assert.Equal(0, BitConverter.ToInt16(cue, 0));
            Assert.Equal(0, BitConverter.ToInt16(cue, cue.Length - 2));
        }
    }
}
