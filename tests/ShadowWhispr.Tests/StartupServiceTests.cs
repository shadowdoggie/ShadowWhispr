using Microsoft.Win32;
using ShadowWhispr.Services;
using Xunit;

namespace ShadowWhispr.Tests;

/// <summary>
/// Verifies the real "start with Windows" registry entry rather than a stand-in,
/// because a silent failure here means the user ticks a box that does nothing.
/// Whatever the machine had before is captured and restored afterwards.
/// </summary>
public sealed class StartupServiceTests : IDisposable
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ShadowWhispr";

    private readonly string? _originalValue;

    public StartupServiceTests()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        _originalValue = key?.GetValue(ValueName) as string;
    }

    [Fact]
    public void EnablingWritesATrayLaunchCommandAndDisablingRemovesIt()
    {
        Assert.True(StartupService.Apply(enabled: true));
        Assert.True(StartupService.IsEnabled());

        using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false))
        {
            var value = key?.GetValue(ValueName) as string;
            Assert.NotNull(value);
            // Must start hidden, or every login throws a window at the user.
            Assert.Contains(StartupService.TrayArgument, value);
            // The path must be quoted: ShadowWhispr installs under a path with
            // no spaces today, but "Program Files" would break an unquoted one.
            Assert.StartsWith("\"", value);
        }

        Assert.True(StartupService.Apply(enabled: false));
        Assert.False(StartupService.IsEnabled());
    }

    [Fact]
    public void DisablingWhenAlreadyDisabledIsNotAnError()
    {
        StartupService.Apply(enabled: false);

        Assert.True(StartupService.Apply(enabled: false));
        Assert.False(StartupService.IsEnabled());
    }

    public void Dispose()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            if (_originalValue is null) key.DeleteValue(ValueName, throwOnMissingValue: false);
            else key.SetValue(ValueName, _originalValue, RegistryValueKind.String);
        }
        catch (Exception)
        {
            // Nothing useful to do in teardown; the assertions already ran.
        }
    }
}
