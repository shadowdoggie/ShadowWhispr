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
    private static byte[] Pressed() => TonePlayer.CreateCue(0.18, 620, 880);
    private static byte[] Released() => TonePlayer.CreateCue(0.16, 700, 420);
    private static byte[] Cancelled() => TonePlayer.CreateCue(0.16, 520, 390, 290);
    private static byte[] Finished() => TonePlayer.CreateCue(0.10, 590, 740, 880);

    [Fact]
    public void TheThreeNoteCuesAreLongerThanTheTwoNoteOnes()
    {
        Assert.Equal(Pressed().Length, Released().Length);
        Assert.True(
            Cancelled().Length > Released().Length,
            "the cancel cue has to be audibly longer than the end-of-dictation cue");
        Assert.Equal(Cancelled().Length, Finished().Length);
    }

    /// <summary>
    /// The finish chime arrives unannounced, minutes after the last keypress,
    /// so it has to be the quietest of the four rather than the loudest.
    /// </summary>
    [Fact]
    public void TheFinishedChimeIsQuieterThanEveryOtherCue()
    {
        static int Loudest(byte[] cue)
        {
            var peak = 0;
            for (var at = 0; at < cue.Length; at += 2)
            {
                peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(cue, at)));
            }
            return peak;
        }

        var finished = Loudest(Finished());
        Assert.True(finished < Loudest(Pressed()));
        Assert.True(finished < Loudest(Released()));
        Assert.True(finished < Loudest(Cancelled()));
    }

    [Fact]
    public void EveryCueStartsAndEndsAtSilence()
    {
        foreach (var cue in new[] { Pressed(), Released(), Cancelled(), Finished() })
        {
            // A cue that starts or ends part-way up its waveform is heard as a
            // click, which is what the fades either side exist to prevent.
            Assert.Equal(0, BitConverter.ToInt16(cue, 0));
            Assert.Equal(0, BitConverter.ToInt16(cue, cue.Length - 2));
        }
    }
}
