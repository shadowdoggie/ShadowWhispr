using System.Text.Json;
using ShadowWhispr.Models;
using ShadowWhispr.Services;
using Xunit;

namespace ShadowWhispr.Tests;

public sealed class MicrophoneSelectionTests
{
    [Fact]
    public void MicrophoneDefaultsToWindowsDefault()
    {
        Assert.Equal(string.Empty, new AppSettings().Microphone);
    }

    [Fact]
    public void MicrophoneChoiceSurvivesSaveAndLoad()
    {
        var settings = new AppSettings { Microphone = "USB Condenser Microphone" };

        var reloaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));

        Assert.NotNull(reloaded);
        Assert.Equal("USB Condenser Microphone", reloaded.Microphone);
    }

    [Fact]
    public void SettingsFileWithoutMicrophoneFieldLoadsAsWindowsDefault()
    {
        // Settings written by versions before the microphone picker existed.
        var reloaded = JsonSerializer.Deserialize<AppSettings>("""{"Hotkey":"Right Ctrl"}""");

        Assert.NotNull(reloaded);
        Assert.Equal(string.Empty, reloaded.Microphone);
    }

    [Fact]
    public void DeviceListAlwaysStartsWithTheWindowsDefaultEntry()
    {
        var devices = AudioRecorderService.ListMicrophones();

        Assert.NotEmpty(devices);
        Assert.True(devices[0].IsWindowsDefault);
        Assert.Equal(MicrophoneDevice.WindowsDefaultName, devices[0].Name);
    }
}
