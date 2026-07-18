using ShadowWhispr.Services;
using Xunit;

namespace ShadowWhispr.Tests;

/// <summary>
/// Exercises the contract between setup-stt.ps1 and the in-app progress screen
/// by running real PowerShell scripts that emit the same markers. A silent
/// break here would leave the user staring at a progress bar that never moves.
/// </summary>
public sealed class SpeechSetupProgressTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ShadowWhisprTests", Guid.NewGuid().ToString("N"), "scripts");

    public SpeechSetupProgressTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ProgressMarkersBecomeProgressReports()
    {
        var script = WriteScript("""
            Write-Host "noise that should be ignored"
            Write-Host "##SW## 14|Creating the local Python environment"
            Write-Host "##SW## 90|Starting the speech engine for the first time"
            exit 0
            """);

        var service = new SpeechSetupService();
        var reports = new List<SetupProgressEventArgs>();
        service.Progress += (_, args) => reports.Add(args);

        var failure = await service.RunAsync(script, TestContext.Current.CancellationToken);

        Assert.Null(failure);
        Assert.Contains(reports, r => r.Percent == 14 && r.Message == "Creating the local Python environment");
        Assert.Contains(reports, r => r.Percent == 90);
        Assert.Contains(reports, r => r.Percent == 100);
    }

    [Fact]
    public async Task ScriptFailureIsReportedWithTheScriptsOwnMessage()
    {
        var script = WriteScript("""
            Write-Host "##SW## 22|Downloading speech and CUDA packages (about 2 GB)"
            Write-Host "##SWERR## Could not reach the download server."
            exit 1
            """);

        var failure = await new SpeechSetupService().RunAsync(script, TestContext.Current.CancellationToken);

        Assert.Equal("Could not reach the download server.", failure);
    }

    [Fact]
    public async Task NonZeroExitWithoutAnErrorMarkerStillFails()
    {
        var script = WriteScript("exit 3");

        var failure = await new SpeechSetupService().RunAsync(script, TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(failure));
    }

    [Fact]
    public async Task MissingScriptIsReportedRatherThanThrowing()
    {
        var failure = await new SpeechSetupService()
            .RunAsync(Path.Combine(_directory, "does-not-exist.ps1"), TestContext.Current.CancellationToken);

        Assert.Contains("Setup script not found", failure);
    }

    /// <summary>
    /// The script must never block on Read-Host when the app runs it hidden,
    /// because there is no console for the user to press Enter in.
    /// </summary>
    [Fact]
    public async Task TheNoPauseEnvironmentVariableIsSetForTheScript()
    {
        var script = WriteScript("""
            if ($env:SHADOWWHISPR_SETUP_NOPAUSE -eq "1") { exit 0 } else { exit 9 }
            """);

        Assert.Null(await new SpeechSetupService().RunAsync(script, TestContext.Current.CancellationToken));
    }

    private string WriteScript(string body)
    {
        var path = Path.Combine(_directory, "setup-stt.ps1");
        File.WriteAllText(path, body);
        return path;
    }

    public void Dispose()
    {
        try
        {
            var root = Directory.GetParent(_directory)?.FullName;
            if (root is not null) Directory.Delete(root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

