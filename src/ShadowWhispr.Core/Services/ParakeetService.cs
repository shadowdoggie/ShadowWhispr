using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ShadowWhispr.Services;

public sealed class ParakeetService : IAsyncDisposable
{
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly object _startupLock = new();
    private Task? _startupTask;
    private Process? _process;
    private StreamWriter? _input;
    private StreamReader? _output;
    private int _nextId;
    private bool _disposed;

    public bool IsReady { get; private set; }
    public string Device { get; private set; } = "unknown";
    public string? LastError { get; private set; }

    public event EventHandler? Ready;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        Task startupTask;
        lock (_startupLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_startupTask is null ||
                (_startupTask.IsCompleted && (!IsReady || _process is not { HasExited: false })))
            {
                _startupTask = StartCoreAsync();
            }

            startupTask = _startupTask;
        }

        return startupTask.WaitAsync(cancellationToken);
    }

    private async Task StartCoreAsync()
    {
        await TearDownWorkerAsync();

        var appDirectory = AppContext.BaseDirectory;
        var projectRoot = FindProjectRoot(appDirectory);
        var python = VenvPython(projectRoot);
        var setupComplete = Path.Combine(projectRoot, ".venv", "setup-complete");
        var worker = Path.Combine(projectRoot, "stt", "worker.py");

        // The marker is written as the setup script's last step; a .venv without
        // it is a partial install (e.g. the model download was interrupted).
        if (!File.Exists(python) || !File.Exists(setupComplete))
            throw new SpeechSetupRequiredException(ResolveSetupScript(projectRoot, appDirectory));
        if (!File.Exists(worker))
            worker = Path.Combine(appDirectory, "stt", "worker.py");

        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = projectRoot
        };
        startInfo.ArgumentList.Add(worker);
        startInfo.ArgumentList.Add("--server");

        Process? process = null;
        try
        {
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Parakeet.");
            lock (_startupLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _process = process;
                _input = process.StandardInput;
                _output = process.StandardOutput;
                LastError = null;
            }
            _ = DrainErrorsAsync(process, process.StandardError);

            var firstLine = await process.StandardOutput.ReadLineAsync();
            if (firstLine is null) throw new InvalidOperationException(LastError ?? "Parakeet exited while loading.");
            using var readyMessage = JsonDocument.Parse(firstLine);
            if (!readyMessage.RootElement.TryGetProperty("ready", out var ready) || !ready.GetBoolean())
            {
                // The worker signals when its model files are missing from the
                // local cache; reopening setup re-downloads them.
                if (readyMessage.RootElement.TryGetProperty("setup_required", out var setupRequired) &&
                    setupRequired.ValueKind == JsonValueKind.True)
                    throw new SpeechSetupRequiredException(ResolveSetupScript(projectRoot, appDirectory));

                var message = readyMessage.RootElement.TryGetProperty("error", out var error)
                    ? error.GetString()
                    : null;
                throw new InvalidOperationException(message ?? "Parakeet could not start.");
            }

            lock (_startupLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (!ReferenceEquals(_process, process))
                    throw new InvalidOperationException("Parakeet was replaced while loading.");

                Device = readyMessage.RootElement.TryGetProperty("device", out var device)
                    ? device.GetString() ?? "unknown"
                    : "unknown";
                IsReady = true;
            }
            Ready?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            AppLog.Write($"Parakeet worker start failed: {exception.Message} (last stderr: {LastError ?? "none"})");
            LastError = exception.Message;
            await TearDownWorkerAsync(process);
            throw;
        }
    }

    public async Task<string> TranscribeAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            await StartAsync(cancellationToken);
            var responseCompleted = false;
            try
            {
                var id = Interlocked.Increment(ref _nextId);
                var payload = JsonSerializer.Serialize(new { id, audio_path = audioPath });
                await _input!.WriteLineAsync(payload.AsMemory(), cancellationToken);
                await _input.FlushAsync(cancellationToken);

                var line = await _output!.ReadLineAsync(cancellationToken);
                if (line is null) throw new InvalidOperationException(LastError ?? "Parakeet stopped unexpectedly.");
                using var result = JsonDocument.Parse(line);
                if (!result.RootElement.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id)
                    throw new InvalidOperationException("Parakeet returned an unexpected response.");

                if (result.RootElement.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                {
                    if (error.ValueKind != JsonValueKind.String)
                        throw new InvalidOperationException("Parakeet returned an invalid error response.");
                    responseCompleted = true;
                    throw new InvalidOperationException(error.GetString() ?? "Parakeet could not transcribe the audio.");
                }
                if (!result.RootElement.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException("Parakeet returned an invalid transcription response.");

                responseCompleted = true;
                return text.GetString()?.Trim() ?? string.Empty;
            }
            catch (Exception exception)
            {
                AppLog.Write($"Transcription failed: {exception.Message}");
                if (!responseCompleted)
                {
                    LastError = exception.Message;
                    await TearDownWorkerAsync();
                }
                throw;
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task DrainErrorsAsync(Process process, StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (_startupLock)
            {
                if (ReferenceEquals(_process, process)) LastError = line;
            }
        }
    }

    private async Task TearDownWorkerAsync(Process? expectedProcess = null)
    {
        Process? process;
        StreamWriter? input;
        StreamReader? output;
        lock (_startupLock)
        {
            if (expectedProcess is not null && !ReferenceEquals(_process, expectedProcess))
            {
                process = expectedProcess;
                input = null;
                output = null;
            }
            else
            {
                IsReady = false;
                Device = "unknown";
                process = _process;
                input = _input;
                output = _output;
                _process = null;
                _input = null;
                _output = null;
                _startupTask = null;
            }
        }

        try
        {
            if (process is { HasExited: false }) process.Kill(true);
        }
        catch { }

        try
        {
            if (input is not null) await input.DisposeAsync();
        }
        catch { }

        try
        {
            output?.Dispose();
        }
        catch { }

        try
        {
            process?.Dispose();
        }
        catch { }
    }

    /// <summary>The interpreter inside a local environment, per platform layout.</summary>
    private static string VenvPython(string root) => OperatingSystem.IsWindows()
        ? Path.Combine(root, ".venv", "Scripts", "python.exe")
        : Path.Combine(root, ".venv", "bin", "python");

    private static string SetupScriptName => OperatingSystem.IsWindows() ? "setup-stt.ps1" : "setup-stt.sh";

    /// <summary>
    /// Locate the one-time speech setup script. Installed builds keep it under
    /// {app}\scripts; a source checkout keeps it under {repo}\scripts.
    /// </summary>
    private static string ResolveSetupScript(string projectRoot, string appDirectory)
    {
        foreach (var baseDir in new[] { projectRoot, appDirectory })
        {
            var candidate = Path.Combine(baseDir, "scripts", SetupScriptName);
            if (File.Exists(candidate)) return candidate;
        }
        return Path.Combine(appDirectory, "scripts", SetupScriptName);
    }

    private static string FindProjectRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "stt")) &&
                File.Exists(VenvPython(directory.FullName)))
                return directory.FullName;
            directory = directory.Parent;
        }
        return start;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_startupLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        await TearDownWorkerAsync();
        _requestLock.Dispose();
    }
}
