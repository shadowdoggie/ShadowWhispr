using ShadowWhispr.Services;
using Xunit;

namespace ShadowWhispr.Tests;

public sealed class FindOnPathTests : IDisposable
{
    private readonly string _directory;
    private readonly string _originalPath;

    public FindOnPathTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "ShadowWhispr-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(_directory);
        _originalPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        Environment.SetEnvironmentVariable("PATH", _directory + Path.PathSeparator + _originalPath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// npm puts a POSIX sh wrapper named exactly after the command beside the
    /// .cmd shim Windows is meant to run. Windows resolution must skip the
    /// wrapper — it is a shell script Windows cannot start — while elsewhere
    /// the bare name is the executable.
    /// </summary>
    [Fact]
    public void ResolvesTheStartableFileWhenAnNpmShellWrapperSitsBesideIt()
    {
        File.WriteAllText(Path.Combine(_directory, "fakecli"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(_directory, "fakecli.cmd"), "@echo off\r\n");

        var found = AiProviderService.FindOnPath("fakecli");

        Assert.NotNull(found);
        var expected = OperatingSystem.IsWindows() ? "fakecli.cmd" : "fakecli";
        Assert.Equal(expected, Path.GetFileName(found));
    }

    [Fact]
    public void StillFindsACommandGivenWithItsExtension()
    {
        File.WriteAllText(Path.Combine(_directory, "fakecli.cmd"), "@echo off\r\n");

        var found = AiProviderService.FindOnPath("fakecli.cmd");

        Assert.NotNull(found);
        Assert.Equal("fakecli.cmd", Path.GetFileName(found));
    }
}
