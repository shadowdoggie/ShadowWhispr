using System;
using System.Threading;
using NAudio.Wave;

namespace ShadowWhispr.Services;

/// <summary>
/// Plays short generated cues without shipping sound files.
///
/// One output device is opened and kept running, and every cue is pushed into
/// its buffer. Opening a fresh device per cue used to drop or clip a cue when
/// two came close together: the second open raced the first device's teardown,
/// and the device could stop before its last partly filled buffer had been
/// heard. The device is closed again after a short idle pause so ShadowWhispr
/// does not hold the speakers open all day, and so a device change (headphones
/// plugged in) is picked up on the next cue.
/// </summary>
public sealed class TonePlayer : IDisposable
{
    private const int SampleRate = 44_100;

    /// <summary>Silence appended to every cue so the tail is never clipped by the device's last buffer.</summary>
    private const double TailSilenceSeconds = 0.15;

    /// <summary>How long the device stays open after the last cue.</summary>
    private static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly Timer _idleTimer;
    private WaveOutEvent? _output;
    private BufferedWaveProvider? _buffer;
    private bool _disposed;

    public TonePlayer()
    {
        _idleTimer = new Timer(_ => CloseDevice(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event EventHandler<Exception>? PlaybackFailed;

    /// <summary>When true the cues are silently skipped (the user's "no sounds" setting).</summary>
    public bool Muted { get; set; }

    public void PlayPressed() => Play(CreateRisingCue());

    public void PlayReleased() => Play(CreateFallingCue());

    private void Play(byte[] samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Muted) return;

        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                EnsureDevice();
                // A cue that arrives while the previous one is still sounding
                // replaces it rather than queueing behind it, so the pair of
                // cues never drifts away from the key presses that caused them.
                _buffer!.ClearBuffer();
                _buffer.AddSamples(samples, 0, samples.Length);
                _idleTimer.Change(IdleClose, Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception ex)
        {
            CloseDevice();
            PlaybackFailed?.Invoke(this, ex);
        }
    }

    /// <summary>Opens the output device if it is not already running. Call under <see cref="_gate"/>.</summary>
    private void EnsureDevice()
    {
        if (_output is not null && _buffer is not null) return;

        DisposeDevice();
        _buffer = new BufferedWaveProvider(new WaveFormat(SampleRate, 16, 1))
        {
            // Keeps the device fed with silence between cues, so it stays
            // running instead of stopping the moment a cue ends.
            ReadFully = true,
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(1),
        };

        var output = new WaveOutEvent { DesiredLatency = 100 };
        output.PlaybackStopped += OnPlaybackStopped;
        output.Init(_buffer);
        output.Play();
        _output = output;
    }

    /// <summary>
    /// The device only stops on its own when something went wrong (the device
    /// was removed, for instance). Drop it so the next cue opens a fresh one.
    /// </summary>
    private void OnPlaybackStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null)
        {
            PlaybackFailed?.Invoke(this, args.Exception);
        }
    }

    private void CloseDevice()
    {
        lock (_gate)
        {
            DisposeDevice();
        }
    }

    /// <summary>Call under <see cref="_gate"/>.</summary>
    private void DisposeDevice()
    {
        if (_output is not null)
        {
            _output.PlaybackStopped -= OnPlaybackStopped;
            try
            {
                _output.Dispose();
            }
            catch (Exception ex)
            {
                PlaybackFailed?.Invoke(this, ex);
            }

            _output = null;
        }

        _buffer = null;
    }

    private static byte[] CreateRisingCue() => CreateCue(
        durationSeconds: 0.13,
        frequencyAt: progress => progress < 0.48 ? 620 : 880,
        volume: 0.18);

    private static byte[] CreateFallingCue() => CreateCue(
        durationSeconds: 0.11,
        frequencyAt: progress => progress < 0.48 ? 700 : 420,
        volume: 0.16);

    private static byte[] CreateCue(double durationSeconds, Func<double, double> frequencyAt, double volume)
    {
        int sampleCount = (int)(SampleRate * durationSeconds);
        int tailCount = (int)(SampleRate * TailSilenceSeconds);
        var result = new byte[(sampleCount + tailCount) * sizeof(short)];
        double phase = 0;

        for (int i = 0; i < sampleCount; i++)
        {
            double progress = (double)i / sampleCount;
            double envelope = Math.Min(1, progress / 0.06) * Math.Min(1, (1 - progress) / 0.16);
            phase += 2 * Math.PI * frequencyAt(progress) / SampleRate;
            short sample = (short)(Math.Sin(phase) * short.MaxValue * volume * envelope);
            result[i * 2] = unchecked((byte)sample);
            result[i * 2 + 1] = unchecked((byte)(sample >> 8));
        }

        return result;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _idleTimer.Dispose();
        CloseDevice();
    }
}
