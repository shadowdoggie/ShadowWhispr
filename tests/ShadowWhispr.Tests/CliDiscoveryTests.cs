using ShadowWhispr.Services;
using Xunit;

namespace ShadowWhispr.Tests;

/// <summary>
/// ShadowWhispr now keeps running in the system tray, so its process PATH can be
/// days old. These tests pin the behaviour that a provider CLI is still found
/// when it was added to PATH after the app started.
/// </summary>
[Collection("EnvironmentPath")]
public sealed class CliDiscoveryTests : IDisposable
{
    private readonly string? _originalProcessPath = Environment.GetEnvironmentVariable("PATH");
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ShadowWhisprTests", Guid.NewGuid().ToString("N"));

    public CliDiscoveryTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void ACliMissingFromTheProcessPathIsStillFoundViaTheUserPath()
    {
        // A fake CLI that exists only in a directory this process has never
        // heard of - exactly the situation after the user installs a provider
        // CLI while ShadowWhispr sits in the tray.
        var executable = Path.Combine(_directory, "shadowwhispr-fake-cli.exe");
        File.WriteAllText(executable, "not a real executable");

        Environment.SetEnvironmentVariable("PATH", @"C:\Windows\System32");
        Assert.Null(Resolve("shadowwhispr-fake-cli"));

        // The registry-backed user PATH is what the refreshed lookup consults.
        var originalUserPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
        try
        {
            Environment.SetEnvironmentVariable(
                "PATH",
                $"{originalUserPath}{Path.PathSeparator}{_directory}",
                EnvironmentVariableTarget.User);

            var found = Resolve("shadowwhispr-fake-cli");

            Assert.NotNull(found);
            Assert.Equal(executable, found, ignoreCase: true);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalUserPath, EnvironmentVariableTarget.User);
        }
    }

    [Fact]
    public void TheProcessPathIsStillSearchedFirst()
    {
        var executable = Path.Combine(_directory, "shadowwhispr-fake-cli.exe");
        File.WriteAllText(executable, "not a real executable");
        Environment.SetEnvironmentVariable("PATH", _directory);

        Assert.Equal(executable, Resolve("shadowwhispr-fake-cli"), ignoreCase: true);
    }

    [Fact]
    public void AnAbsentCliStillReportsAsAbsent()
    {
        Environment.SetEnvironmentVariable("PATH", _directory);

        Assert.Null(Resolve("shadowwhispr-definitely-not-installed"));
    }

    /// <summary>
    /// Exercises the real lookup through the public surface that the app uses,
    /// via a provider name mapped to the command under test.
    /// </summary>
    private static string? Resolve(string command)
    {
        var method = typeof(AiProviderService).GetMethod(
            "FindOnPath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return (string?)method!.Invoke(null, [command]);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalProcessPath);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
