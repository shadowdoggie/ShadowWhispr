using ShadowWhispr.Services;
using Xunit;

namespace ShadowWhispr.Tests;

public sealed class HotkeyBindingTests
{
    [Theory]
    [InlineData("F13", 0x7C)]
    [InlineData("F24", 0x87)]
    [InlineData("Ctrl + Shift + F13", 0x7C)]
    [InlineData("Right Ctrl", 0xA3)]
    public void ExtendedAndLegacyHotkeysRoundTrip(string text, int virtualKey)
    {
        Assert.True(HoldHotkey.TryParse(text, out var hotkey));
        Assert.Equal(virtualKey, hotkey.VirtualKey);
        Assert.True(HoldHotkey.TryParse(hotkey.ToString(), out var roundTrip));
        Assert.Equal(hotkey, roundTrip);
    }

    [Fact]
    public void CapturedChordKeepsEveryModifier()
    {
        var hotkey = HoldHotkey.FromVirtualKey(
            0x7C,
            ctrl: true,
            shift: true,
            alt: true,
            win: true);

        Assert.Equal("Ctrl + Shift + Alt + Win + F13", hotkey.ToString());
    }
}
