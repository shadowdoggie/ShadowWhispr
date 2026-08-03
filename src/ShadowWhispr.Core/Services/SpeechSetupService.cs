using System.Diagnostics;
using System.IO;
using System.Text;

namespace ShadowWhispr.Services;

public sealed class SetupProgressEventArgs(int percent, string message) : EventArgs
{
    /// <summary>0-100 overall progress across the whole one-time setup.</summary>
    public int Percent { get; } = percent;

    public string Message { get; } = message;
}

/// <summary>
/// Runs the one-time speech setup script without showing a console, turning its
/// "##SW## percent|message" markers into progress the app can display. The
/// script keeps writing setup-log.txt itself, so a failure is still fully
/// diagnosable from disk afterwards.
/// </summary>
public sealed class SpeechSetupService
{
    /// <summary>
    /// Approximate finished size of the speech-model folder, used only to show
    /// "x.x GB of ~2.4 GB" while the model downloads. It is a display estimate,
    /// not a check — nothing fails if the real total differs.
    /// </summary>
    private const double ModelDownloadBytesEstimate = 2.4 * 1000 * 1000 * 1000;

    /// <summary>The percent marker the script emits when the model download starts.</summary>
    private const int ModelDownloadPercent = 62;

    private const string ProgressMarker = "##SW## ";
    private const string ErrorMarker = "##SWERR## ";

    public event EventHandler<SetupProgressEventArgs>? Progress;

    /// <summary>
    /// Runs setup to completion. Returns null on success, or a plain-English
    /// error suitable for showing directly to the user.
    /// </summary>
    public async Task<string?> RunAsync(
        string setupScriptPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(setupScriptPath))
        {
            AppLog.Write($"Speech setup script not found at: {setupScriptPath}");
            return "Setup script not found - please reinstall ShadowWhispr.";
        }

        var scriptDirectory = Path.GetDirectoryName(setupScriptPath) ?? AppContext.BaseDirectory;
        var projectRoot = Path.GetFullPath(Path.Combine(scriptDirectory, ".."));
        var modelDirectory = Path.Combine(projectRoot, "speech-model");

        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "bash",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = scriptDirectory
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
        }
        startInfo.ArgumentList.Add(setupScriptPath);
        // Without a console there is nobody to press Enter, so the script must
        // not pause on failure - it would hang forever as an invisible process.
        startInfo.Environment["SHADOWWHISPR_SETUP_NOPAUSE"] = "1";

        AppLog.Write($"Running speech setup (hidden): {setupScriptPath}");
        Report(0, "Starting setup");

        // The last lines are kept so a failure can be explained from the log
        // without dumping every line of pip's output into app-log.txt.
        var recentOutput = new Queue<string>();
        string? reportedError = null;
        using var modelPolling = new CancellationTokenSource();

        try
        {
            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("PowerShell could not be started.");

            var errorReader = ReadAllAsync(process.StandardError, recentOutput);

            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                Remember(recentOutput, line);

                if (line.StartsWith(ErrorMarker, StringComparison.Ordinal))
                {
                    reportedError = line[ErrorMarker.Length..].Trim();
                    continue;
                }

                if (!line.StartsWith(ProgressMarker, StringComparison.Ordinal)) continue;

                var payload = line[ProgressMarker.Length..];
                var separator = payload.IndexOf('|');
                if (separator <= 0 || !int.TryParse(payload[..separator], out var percent)) continue;

                var message = payload[(separator + 1)..].Trim();
                AppLog.Write($"Setup step: {percent}% {message}");
                Report(percent, message);

                // The model download is one long silent step, so from here the
                // folder is polled to show real megabytes arriving.
                if (percent == ModelDownloadPercent)
                    StartModelDownloadPolling(modelDirectory, modelPolling.Token);
                else
                    modelPolling.Cancel();
            }

            modelPolling.Cancel();
            await process.WaitForExitAsync(cancellationToken);
            await errorReader;

            if (process.ExitCode == 0)
            {
                AppLog.Write("Speech setup finished successfully");
                Report(100, "Setup complete");
                return null;
            }

            var failure = reportedError ?? "Setup did not finish.";
            AppLog.Write(
                $"Speech setup failed (exit code {process.ExitCode}): {failure}{Environment.NewLine}" +
                $"Last output:{Environment.NewLine}{string.Join(Environment.NewLine, recentOutput)}");
            return failure;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLog.Write("Running the speech setup script failed", exception);
            return $"Could not run setup: {exception.Message}";
        }
        finally
        {
            modelPolling.Cancel();
        }
    }

    /// <summary>
    /// Reports the growing size of the model folder once a second. This is
    /// display-only: it never decides success, so an unreadable folder is
    /// simply skipped rather than failing the setup.
    /// </summary>
    private void StartModelDownloadPolling(string modelDirectory, CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

                    long bytes = DirectorySize(modelDirectory);
                    if (bytes <= 0) continue;

                    var fraction = Math.Clamp(bytes / ModelDownloadBytesEstimate, 0, 1);
                    var gigabytes = bytes / 1_000_000_000d;
                    Report(
                        ModelDownloadPercent + (int)(fraction * (90 - ModelDownloadPercent)),
                        $"Downloading the speech model - {gigabytes:0.0} GB of about 2.4 GB");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception)
            {
                AppLog.Write("Model download progress polling stopped", exception);
            }
        }, CancellationToken.None);
    }

    private static long DirectorySize(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return 0;
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return total;
        }
        catch (Exception)
        {
            // Files appear and vanish mid-download; a failed measurement just
            // means this tick shows nothing new.
            return 0;
        }
    }

    private static async Task ReadAllAsync(StreamReader reader, Queue<string> recentOutput)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            Remember(recentOutput, line);
        }
    }

    private static void Remember(Queue<string> recentOutput, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (recentOutput)
        {
            recentOutput.Enqueue(line);
            while (recentOutput.Count > 40) recentOutput.Dequeue();
        }
    }

    private void Report(int percent, string message) =>
        Progress?.Invoke(this, new SetupProgressEventArgs(Math.Clamp(percent, 0, 100), message));
}
