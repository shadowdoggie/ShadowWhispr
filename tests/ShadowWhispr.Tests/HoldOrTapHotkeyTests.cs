using System;
using System.Collections.Generic;
using System.Threading;
using ShadowWhispr.Services;
using Xunit;

namespace ShadowWhispr.Tests;

/// <summary>
/// Covers the hold-or-tap behaviour of one dictation key. Windows drops
/// synthesised keystrokes before the keyboard hook sees them, so these tests
/// drive the hook's decision logic directly instead of pressing real keys.
/// </summary>
public sealed class HoldOrTapHotkeyTests
{
    private const int VkF13 = 0x7C;
    private const int VkF14 = 0x7D;
    private const int VkA = 0x41;

    private static (GlobalHotkeyService Service, List<string> Events) Build()
    {
        var service = new GlobalHotkeyService(HoldHotkey.Parse("F13"))
        {
            RawHotkey = HoldHotkey.Parse("F14"),
            TapThreshold = TimeSpan.FromMilliseconds(150)
        };

        var events = new List<string>();
        service.Pressed += (_, e) => events.Add($"pressed:{e.Kind}");
        service.Latched += (_, e) => events.Add($"latched:{e.Kind}");
        service.Released += (_, e) => events.Add($"released:{e.Kind}");
        return (service, events);
    }

    private static bool Down(GlobalHotkeyService service, int key) => service.HandleKey(key, isDown: true, isUp: false);
    private static bool Up(GlobalHotkeyService service, int key) => service.HandleKey(key, isDown: false, isUp: true);

    [Fact]
    public void HoldingLongerThanTheThresholdEndsWithTheKeyGoingUp()
    {
        var (service, events) = Build();

        Down(service, VkF13);
        Thread.Sleep(250);
        Up(service, VkF13);

        Assert.Equal(["pressed:Primary", "released:Primary"], events);
        Assert.False(service.IsHeld);
    }

    [Fact]
    public void QuickTapKeepsRecordingUntilTheNextPress()
    {
        var (service, events) = Build();

        Down(service, VkF13);
        Up(service, VkF13);
        Assert.Equal(["pressed:Primary", "latched:Primary"], events);
        Assert.True(service.IsHeld);

        Down(service, VkF13);
        Assert.Equal(["pressed:Primary", "latched:Primary", "released:Primary"], events);
        Assert.False(service.IsHeld);

        // Letting that stopping press go must not start anything new.
        Up(service, VkF13);
        Assert.Equal(3, events.Count);
    }

    [Fact]
    public void TapAndTheKeyStaysDown_RecordingRunsOnAfterTheStop()
    {
        var (service, events) = Build();

        Down(service, VkF13);
        Up(service, VkF13);
        Down(service, VkF13);
        Thread.Sleep(250);
        Up(service, VkF13);

        Assert.Equal(["pressed:Primary", "latched:Primary", "released:Primary"], events);
    }

    [Fact]
    public void TheOtherDictationKeyAlsoStopsALatchedRecording()
    {
        var (service, events) = Build();

        Down(service, VkF13);
        Up(service, VkF13);
        Down(service, VkF14);

        Assert.Equal(["pressed:Primary", "latched:Primary", "released:Primary"], events);
        Assert.False(service.IsHeld);
    }

    [Fact]
    public void TypingWhileLatchedNeitherStopsNorGetsSwallowed()
    {
        var (service, events) = Build();

        Down(service, VkF13);
        Up(service, VkF13);

        Assert.False(Down(service, VkA));
        Assert.False(Up(service, VkA));
        Assert.Equal(["pressed:Primary", "latched:Primary"], events);
        Assert.True(service.IsHeld);
    }

    [Fact]
    public void RawKeyReportsItsOwnKindBothWays()
    {
        var (service, events) = Build();

        Down(service, VkF14);
        Thread.Sleep(250);
        Up(service, VkF14);
        Down(service, VkF14);
        Up(service, VkF14);

        Assert.Equal(["pressed:Raw", "released:Raw", "pressed:Raw", "latched:Raw"], events);
    }

    [Fact]
    public void DictationKeyIsKeptFromTheFocusedApp()
    {
        var (service, _) = Build();

        Assert.True(Down(service, VkF13));
        Assert.True(Down(service, VkF13));  // auto-repeat while held
        Assert.True(Up(service, VkF13));
        Assert.True(Down(service, VkF13));  // the press that stops the tap recording
        Assert.True(Up(service, VkF13));
    }

    [Fact]
    public void PausingDictationEndsALatchedRecording()
    {
        var (service, events) = Build();

        Down(service, VkF13);
        Up(service, VkF13);
        service.Enabled = false;

        Assert.Equal(["pressed:Primary", "latched:Primary", "released:Primary"], events);
        Assert.False(service.IsHeld);

        // With the hotkeys off the key must reach the focused app untouched.
        Assert.False(Down(service, VkF13));
        Assert.Equal(3, events.Count);
    }
}
