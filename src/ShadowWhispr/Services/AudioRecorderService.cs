using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace ShadowWhispr.Services;

public sealed class AudioRecordingEventArgs(string filePath) : EventArgs
{
    public string FilePath { get; } = filePath;
}

/// <summary>
/// A capture device as Windows reports it. Number -1 is the wave mapper, which
/// always follows whatever Windows currently considers the default microphone.
/// </summary>
public sealed record MicrophoneDevice(int Number, string Name)
{
    public const int WindowsDefaultNumber = -1;
    public const string WindowsDefaultName = "Windows default microphone";

    public static MicrophoneDevice WindowsDefault { get; } = new(WindowsDefaultNumber, WindowsDefaultName);

    public bool IsWindowsDefault => Number == WindowsDefaultNumber;
}

/// <summary>Records microphone audio in the format expected by Parakeet.</summary>
public sealed class AudioRecorderService : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly string _recordingDirectory;
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private TaskCompletionSource<string?>? _stopCompletion;
    private string? _currentFilePath;
    private Exception? _captureException;
    private bool _disposed;

    public AudioRecorderService(string? recordingDirectory = null)
    {
        _recordingDirectory = recordingDirectory
            ?? Path.Combine(Path.GetTempPath(), "ShadowWhispr", "recordings");
        RetryPendingCleanup();
    }

    public event EventHandler<AudioRecordingEventArgs>? RecordingStarted;
    public event EventHandler<AudioRecordingEventArgs>? RecordingStopped;
    public event EventHandler<Exception>? RecordingFailed;

    /// <summary>
    /// The name of the microphone recordings should use, matched against the
    /// devices Windows lists at the moment recording starts. Empty means the
    /// Windows default. Matching by name (not number) keeps the choice valid
    /// when plugging or unplugging devices reshuffles the numbering.
    /// </summary>
    public string PreferredDeviceName
    {
        get
        {
            lock (_gate)
            {
                return _preferredDeviceName;
            }
        }
        set
        {
            lock (_gate)
            {
                _preferredDeviceName = value ?? string.Empty;
            }
        }
    }

    private string _preferredDeviceName = string.Empty;

    /// <summary>Lists the microphones Windows currently offers, default first.</summary>
    public static IReadOnlyList<MicrophoneDevice> ListMicrophones()
    {
        var devices = new List<MicrophoneDevice> { MicrophoneDevice.WindowsDefault };
        int count = 0;
        try
        {
            count = WaveInEvent.DeviceCount;
        }
        catch (Exception ex)
        {
            AppLog.Write("Could not count the available microphones", ex);
        }

        for (int i = 0; i < count; i++)
        {
            try
            {
                devices.Add(new MicrophoneDevice(i, WaveInEvent.GetCapabilities(i).ProductName));
            }
            catch (Exception ex)
            {
                AppLog.Write($"Could not read the name of microphone {i}", ex);
            }
        }

        return devices;
    }

    /// <summary>
    /// Finds the device number for the preferred microphone. Falls back to the
    /// Windows default (and says so in the log) when that microphone is gone,
    /// so dictation keeps working instead of erroring on an unplugged device.
    /// </summary>
    private static int ResolveDeviceNumber(string preferredName)
    {
        if (string.IsNullOrWhiteSpace(preferredName) ||
            string.Equals(preferredName, MicrophoneDevice.WindowsDefaultName, StringComparison.OrdinalIgnoreCase))
        {
            return MicrophoneDevice.WindowsDefaultNumber;
        }

        foreach (var device in ListMicrophones())
        {
            if (!device.IsWindowsDefault &&
                string.Equals(device.Name, preferredName, StringComparison.OrdinalIgnoreCase))
            {
                return device.Number;
            }
        }

        AppLog.Write($"Chosen microphone '{preferredName}' is not connected; using the Windows default instead");
        return MicrophoneDevice.WindowsDefaultNumber;
    }

    public bool IsRecording
    {
        get
        {
            lock (_gate)
            {
                return _waveIn is not null;
            }
        }
    }

    public string? CurrentFilePath
    {
        get
        {
            lock (_gate)
            {
                return _currentFilePath;
            }
        }
    }

    public Task<string> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        string filePath;
        lock (_gate)
        {
            if (_waveIn is not null)
            {
                throw new InvalidOperationException("A recording is already in progress.");
            }

            Directory.CreateDirectory(_recordingDirectory);
            filePath = Path.Combine(
                _recordingDirectory,
                $"shadowwhispr-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.wav");

            int deviceNumber = ResolveDeviceNumber(_preferredDeviceName);
            AppLog.Write($"Recording from microphone {(deviceNumber == MicrophoneDevice.WindowsDefaultNumber ? "(Windows default)" : $"'{_preferredDeviceName}'")}");
            var waveIn = new WaveInEvent
            {
                DeviceNumber = deviceNumber,
                WaveFormat = new WaveFormat(16_000, 16, 1),
                BufferMilliseconds = 50,
                NumberOfBuffers = 3
            };
            WaveFileWriter? writer = null;

            try
            {
                writer = new WaveFileWriter(filePath, waveIn.WaveFormat);
                waveIn.DataAvailable += OnDataAvailable;
                waveIn.RecordingStopped += OnRecordingStopped;

                _waveIn = waveIn;
                _writer = writer;
                _currentFilePath = filePath;
                _captureException = null;
                _stopCompletion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                waveIn.StartRecording();
            }
            catch (Exception startException)
            {
                AppLog.Write("Starting the microphone recording failed", startException);
                _waveIn = null;
                _writer = null;
                _currentFilePath = null;
                _captureException = null;
                _stopCompletion = null;
                writer?.Dispose();
                waveIn.Dispose();
                TryDelete(filePath);
                throw;
            }
        }

        RecordingStarted?.Invoke(this, new AudioRecordingEventArgs(filePath));
        return Task.FromResult(filePath);
    }

    /// <summary>
    /// Stops capture and completes only after the WAV header has been finalized.
    /// </summary>
    public async Task<string?> StopAsync(CancellationToken cancellationToken = default)
    {
        WaveInEvent? waveIn;
        Task<string?> completion;

        lock (_gate)
        {
            waveIn = _waveIn;
            completion = _stopCompletion?.Task ?? Task.FromResult<string?>(null);
        }

        if (waveIn is null)
        {
            return null;
        }

        try
        {
            waveIn.StopRecording();
        }
        catch (Exception ex)
        {
            CompleteStopped(ex);
        }

        return await completion.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes a recording once transcription no longer needs it.</summary>
    public void DeleteRecording(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        string fullPath = Path.GetFullPath(filePath);
        string allowedDirectory = Path.GetFullPath(_recordingDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(allowedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only ShadowWhispr temporary recordings can be deleted.", nameof(filePath));
        }

        TryDelete(fullPath);
    }

    private void RetryPendingCleanup()
    {
        if (!Directory.Exists(_recordingDirectory)) return;

        foreach (var filePath in Directory.EnumerateFiles(
                     _recordingDirectory,
                     "shadowwhispr-*.wav",
                     SearchOption.TopDirectoryOnly))
        {
            TryDelete(filePath);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        try
        {
            lock (_gate)
            {
                _writer?.Write(args.Buffer, 0, args.BytesRecorded);
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _captureException ??= ex;
            }

            try
            {
                _waveIn?.StopRecording();
            }
            catch
            {
                // The original write error is the useful failure to report.
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        CompleteStopped(args.Exception);
    }

    private void CompleteStopped(Exception? exception)
    {
        WaveInEvent? waveIn;
        WaveFileWriter? writer;
        TaskCompletionSource<string?>? completion;
        string? filePath;

        lock (_gate)
        {
            waveIn = _waveIn;
            writer = _writer;
            completion = _stopCompletion;
            filePath = _currentFilePath;
            exception ??= _captureException;

            _waveIn = null;
            _writer = null;
            _stopCompletion = null;
            _currentFilePath = null;
            _captureException = null;
        }

        if (waveIn is null)
        {
            return;
        }

        waveIn.DataAvailable -= OnDataAvailable;
        waveIn.RecordingStopped -= OnRecordingStopped;

        try
        {
            writer?.Dispose();
            waveIn.Dispose();
        }
        catch (Exception disposeError)
        {
            exception ??= disposeError;
        }

        if (exception is not null)
        {
            AppLog.Write("Microphone capture stopped with an error", exception);
            if (filePath is not null)
            {
                TryDelete(filePath);
            }

            completion?.TrySetException(exception);
            RecordingFailed?.Invoke(this, exception);
            return;
        }

        completion?.TrySetResult(filePath);
        if (filePath is not null)
        {
            RecordingStopped?.Invoke(this, new AudioRecordingEventArgs(filePath));
        }
    }

    private static void TryDelete(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (IOException)
        {
            // Cleanup can be retried on the next app launch.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup can be retried on the next app launch.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        string? filePath = null;
        try
        {
            filePath = await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            if (filePath is not null)
            {
                TryDelete(filePath);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
