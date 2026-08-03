using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ShadowWhispr.Services;

namespace ShadowWhispr.Linux.Services;

public sealed class AudioRecordingEventArgs(string filePath) : EventArgs
{
    public string FilePath { get; } = filePath;
}

/// <summary>
/// A capture device as PipeWire/PulseAudio reports it. A null source name means
/// "follow the system default microphone". The human-readable description is
/// what gets stored in settings, so the choice survives source names changing
/// their numeric suffixes between sessions.
/// </summary>
public sealed record MicrophoneDevice(string? SourceName, string Label)
{
    public const string SystemDefaultLabel = "System default microphone";

    public static MicrophoneDevice SystemDefault { get; } = new(null, SystemDefaultLabel);

    public bool IsSystemDefault => SourceName is null;

    public string Name => Label;

    public override string ToString() => Label;
}

/// <summary>
/// Records microphone audio in the format Parakeet expects (16 kHz mono s16le)
/// by running parec against the PipeWire/Pulse server and wrapping the raw
/// stream in a WAV header when the recording stops.
/// </summary>
public sealed class LinuxAudioRecorderService : IDisposable, IAsyncDisposable
{
    private const int SampleRate = 16_000;
    private const int SigInt = 2;

    private readonly object _gate = new();
    private readonly string _recordingDirectory;
    private Process? _parec;
    private FileStream? _rawFile;
    private Task? _pumpTask;
    private TaskCompletionSource<string?>? _stopCompletion;
    private string? _currentRawPath;
    private string _preferredDeviceName = string.Empty;
    private bool _disposed;

    public LinuxAudioRecorderService(string? recordingDirectory = null)
    {
        _recordingDirectory = recordingDirectory
            ?? Path.Combine(Path.GetTempPath(), "ShadowWhispr", "recordings");
        RetryPendingCleanup();
    }

    public event EventHandler<AudioRecordingEventArgs>? RecordingStarted;
    public event EventHandler<AudioRecordingEventArgs>? RecordingStopped;
    public event EventHandler<Exception>? RecordingFailed;

    /// <summary>
    /// The label of the microphone recordings should use, matched against the
    /// sources the sound server lists when recording starts. Empty means the
    /// system default.
    /// </summary>
    public string PreferredDeviceName
    {
        get { lock (_gate) return _preferredDeviceName; }
        set { lock (_gate) _preferredDeviceName = value ?? string.Empty; }
    }

    public bool IsRecording
    {
        get { lock (_gate) return _parec is not null; }
    }

    /// <summary>Lists the microphones the sound server currently offers, default first.</summary>
    public static IReadOnlyList<MicrophoneDevice> ListMicrophones()
    {
        var devices = new List<MicrophoneDevice> { MicrophoneDevice.SystemDefault };
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pactl",
                ArgumentList = { "-f", "json", "list", "sources" },
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            using var process = Process.Start(startInfo);
            if (process is null) return devices;
            var json = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);

            using var document = JsonDocument.Parse(json);
            foreach (var source in document.RootElement.EnumerateArray())
            {
                var name = source.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (name is null || name.EndsWith(".monitor", StringComparison.Ordinal)) continue;
                var description = source.TryGetProperty("description", out var d) ? d.GetString() : null;
                devices.Add(new MicrophoneDevice(name, description ?? name));
            }
        }
        catch (Exception exception)
        {
            AppLog.Write("Could not list the available microphones", exception);
        }
        return devices;
    }

    private static string? ResolveSourceName(string preferredLabel)
    {
        if (string.IsNullOrWhiteSpace(preferredLabel) ||
            string.Equals(preferredLabel, MicrophoneDevice.SystemDefaultLabel, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var device in ListMicrophones())
        {
            if (!device.IsSystemDefault &&
                string.Equals(device.Label, preferredLabel, StringComparison.OrdinalIgnoreCase))
            {
                return device.SourceName;
            }
        }

        AppLog.Write($"Chosen microphone '{preferredLabel}' is not connected; using the system default instead");
        return null;
    }

    public Task<string> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        string rawPath;
        lock (_gate)
        {
            if (_parec is not null)
            {
                throw new InvalidOperationException("A recording is already in progress.");
            }

            Directory.CreateDirectory(_recordingDirectory);
            rawPath = Path.Combine(
                _recordingDirectory,
                $"shadowwhispr-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.raw");

            var source = ResolveSourceName(_preferredDeviceName);
            AppLog.Write($"Recording from microphone {(source is null ? "(system default)" : $"'{_preferredDeviceName}'")}");

            var startInfo = new ProcessStartInfo
            {
                FileName = "parec",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--format=s16le");
            startInfo.ArgumentList.Add($"--rate={SampleRate}");
            startInfo.ArgumentList.Add("--channels=1");
            startInfo.ArgumentList.Add("--latency-msec=50");
            if (source is not null) startInfo.ArgumentList.Add($"--device={source}");

            FileStream? rawFile = null;
            Process? parec = null;
            try
            {
                rawFile = new FileStream(rawPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                parec = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("parec could not be started — is PipeWire/Pulse running?");

                _parec = parec;
                _rawFile = rawFile;
                _currentRawPath = rawPath;
                _stopCompletion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pumpTask = PumpAsync(parec, rawFile);
            }
            catch (Exception startException)
            {
                AppLog.Write("Starting the microphone recording failed", startException);
                _parec = null;
                _rawFile = null;
                _currentRawPath = null;
                _stopCompletion = null;
                _pumpTask = null;
                rawFile?.Dispose();
                try { parec?.Kill(); } catch { }
                parec?.Dispose();
                TryDelete(rawPath);
                throw;
            }
        }

        RecordingStarted?.Invoke(this, new AudioRecordingEventArgs(rawPath));
        return Task.FromResult(rawPath);
    }

    /// <summary>
    /// Copies parec's raw stream to disk and finishes the recording when the
    /// stream ends — normally because SIGINT told parec to stop, otherwise
    /// because capture died, which the exit code then distinguishes.
    /// </summary>
    private async Task PumpAsync(Process parec, FileStream rawFile)
    {
        Exception? failure = null;
        try
        {
            await parec.StandardOutput.BaseStream.CopyToAsync(rawFile);
            await parec.WaitForExitAsync();

            // SIGINT termination reports 130 through the shell but a raw exit
            // signal here; any recorded bytes mean capture itself worked.
            if (rawFile.Length == 0)
            {
                var stderr = await parec.StandardError.ReadToEndAsync();
                failure = new InvalidOperationException(
                    string.IsNullOrWhiteSpace(stderr)
                        ? "No audio was captured — check the microphone."
                        : $"Recording failed: {stderr.Trim()}");
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        CompleteStopped(failure);
    }

    /// <summary>
    /// Stops capture and completes only after the WAV file has been written.
    /// </summary>
    public async Task<string?> StopAsync(CancellationToken cancellationToken = default)
    {
        Process? parec;
        Task<string?> completion;
        lock (_gate)
        {
            parec = _parec;
            completion = _stopCompletion?.Task ?? Task.FromResult<string?>(null);
        }

        if (parec is null) return null;

        try
        {
            if (!parec.HasExited) kill(parec.Id, SigInt);
        }
        catch (Exception exception)
        {
            AppLog.Write("Asking parec to stop failed; killing it", exception);
            try { parec.Kill(); } catch { }
        }

        return await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void CompleteStopped(Exception? exception)
    {
        Process? parec;
        FileStream? rawFile;
        TaskCompletionSource<string?>? completion;
        string? rawPath;

        lock (_gate)
        {
            parec = _parec;
            rawFile = _rawFile;
            completion = _stopCompletion;
            rawPath = _currentRawPath;

            _parec = null;
            _rawFile = null;
            _stopCompletion = null;
            _currentRawPath = null;
            _pumpTask = null;
        }

        if (parec is null) return;

        string? wavPath = null;
        try
        {
            rawFile?.Dispose();
            parec.Dispose();

            if (exception is null && rawPath is not null)
            {
                wavPath = Path.ChangeExtension(rawPath, ".wav");
                WriteWav(rawPath, wavPath);
                TryDelete(rawPath);
            }
        }
        catch (Exception finishError)
        {
            exception ??= finishError;
        }

        if (exception is not null)
        {
            AppLog.Write("Microphone capture stopped with an error", exception);
            if (rawPath is not null) TryDelete(rawPath);
            if (wavPath is not null) TryDelete(wavPath);
            completion?.TrySetException(exception);
            RecordingFailed?.Invoke(this, exception);
            return;
        }

        completion?.TrySetResult(wavPath);
        if (wavPath is not null)
        {
            RecordingStopped?.Invoke(this, new AudioRecordingEventArgs(wavPath));
        }
    }

    /// <summary>Wraps the captured raw s16le samples in a minimal WAV container.</summary>
    private static void WriteWav(string rawPath, string wavPath)
    {
        using var raw = new FileStream(rawPath, FileMode.Open, FileAccess.Read);
        using var wav = new FileStream(wavPath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(wav);

        int dataLength = checked((int)raw.Length);
        const short channels = 1;
        const short bitsPerSample = 16;
        const int byteRate = SampleRate * channels * (bitsPerSample / 8);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write(channels);
        writer.Write(SampleRate);
        writer.Write(byteRate);
        writer.Write((short)(channels * (bitsPerSample / 8)));
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataLength);
        raw.CopyTo(wav);
    }

    /// <summary>Deletes a recording once transcription no longer needs it.</summary>
    public void DeleteRecording(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        string fullPath = Path.GetFullPath(filePath);
        string allowedDirectory = Path.GetFullPath(_recordingDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(allowedDirectory, StringComparison.Ordinal))
        {
            throw new ArgumentException("Only ShadowWhispr temporary recordings can be deleted.", nameof(filePath));
        }

        TryDelete(fullPath);
    }

    private void RetryPendingCleanup()
    {
        if (!Directory.Exists(_recordingDirectory)) return;
        foreach (var pattern in new[] { "shadowwhispr-*.wav", "shadowwhispr-*.raw" })
        {
            foreach (var filePath in Directory.EnumerateFiles(_recordingDirectory, pattern))
            {
                TryDelete(filePath);
            }
        }
    }

    private static void TryDelete(string filePath)
    {
        try
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        string? filePath = null;
        try
        {
            filePath = await StopAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AppLog.Write("Stopping the recorder during shutdown failed", exception);
        }
        finally
        {
            if (filePath is not null) TryDelete(filePath);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
