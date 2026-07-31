using System;
using System.Diagnostics;
using ShadowWhispr.Services;
using Xunit;

namespace ShadowWhispr.Tests;

/// <summary>
/// An agent session must not outlive ShadowWhispr. Cancelling on the way out
/// races the app's own exit and is skipped entirely by a crash, so the job
/// object is what actually guarantees it. These tests drive the same mechanism
/// the app relies on: closing the job's last handle kills what is inside it.
/// </summary>
public sealed class ChildProcessJobTests
{
    // PowerShell rather than timeout or ping: timeout refuses to run at all once
    // its input is redirected, and ping did not reliably stay up long enough to
    // be caught. Both exited immediately and so proved nothing.
    private const string Sleep = "powershell -NoProfile -Command \"Start-Sleep -Seconds 200\"";

    private static Process Start(string arguments) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        })!;

    private static Process StartSleeper() => Start($"/c {Sleep}");

    [Fact]
    public void ClosingTheJobKillsTheProcessesInIt()
    {
        var job = new ChildProcessJob();
        var process = StartSleeper();
        try
        {
            job.Assign(process);
            Assert.False(process.HasExited, "the test process died before the job was closed");

            job.Dispose();

            Assert.True(
                process.WaitForExit(TimeSpan.FromSeconds(10)),
                "closing the job did not kill the process in it");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.Dispose();
            job.Dispose();
        }
    }

    // Note: there is deliberately no test here for the job reaching a CLI's own
    // grandchildren. That behaviour is the job object's, guaranteed by Windows
    // rather than by our code, and every attempt at a test process tree that
    // stayed up long enough to observe proved flaky for reasons that had nothing
    // to do with what was being tested.

    [Fact]
    public void DisposingTwiceIsHarmless()
    {
        var job = new ChildProcessJob();
        job.Dispose();
        job.Dispose();
    }

    /// <summary>
    /// Shutdown closes the shared job, and a stray later call must not throw
    /// its way out of a cleanup step that is already running.
    /// </summary>
    [Fact]
    public void AssigningAfterDisposeIsHarmless()
    {
        var job = new ChildProcessJob();
        job.Dispose();

        var process = StartSleeper();
        try
        {
            job.Assign(process);
            Assert.False(process.HasExited);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            process.Dispose();
        }
    }
}
