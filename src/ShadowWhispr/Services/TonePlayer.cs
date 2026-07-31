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

    /// <summary>
    /// Silence in front of every cue. Kept very short: it only has to cover the
    /// device settling, and every millisecond here is a millisecond between the
    /// key press and the cue being heard.
    /// </summary>
    private const double LeadSilenceSeconds = 0.005;

    /// <summary>
    /// How long the device stays open after the last cue. Long enough that a
    /// whole dictation session reuses one open device — opening it is the
    /// slowest part, and a reopen is heard as a late first cue.
    /// </summary>
    private static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(60);

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

    /// <summary>
    /// The cue for calling off an agent run. Three notes rather than two, lower
    /// and walking further down: stopping the agent and finishing a dictation
    /// are very different things, and a cue that merely resembled the other one
    /// would leave you unsure which of the two you had just done.
    /// </summary>
    public void PlayCancelled() => Play(CreateCancelledCue());

    /// <summary>
    /// The cue for an agent run finishing on its own. Three notes walking up
    /// where the stop cue walks down, and quieter than the rest: it arrives
    /// unannounced, minutes after you last touched a key, so it should be enough
    /// to notice and not enough to make you jump.
    /// </summary>
    public void PlayFinished() => Play(CreateFinishedCue());

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
                // Appended rather than replacing whatever is still sounding:
                // cutting a note off mid-waveform is heard as a click. Each cue
                // is short, so a fast press pair simply plays both in order.
                _buffer!.AddSamples(samples, 0, samples.Length);
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

        // A cue added to the buffer is only picked up at the next refill, so the
        // buffer size is also the delay before it is heard. Four small buffers
        // keep that delay short while still leaving enough refills in flight
        // that the device cannot run dry (which would crackle).
        var output = new WaveOutEvent { DesiredLatency = 80, NumberOfBuffers = 4 };
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

    /// <summary>Length of each of the two notes in a cue.</summary>
    private const double NoteSeconds = 0.075;

    private static byte[] CreateRisingCue() => CreateCue(volume: 0.18, 620, 880);

    private static byte[] CreateFallingCue() => CreateCue(volume: 0.16, 700, 420);

    private static byte[] CreateCancelledCue() => CreateCue(volume: 0.16, 520, 390, 290);

    private static byte[] CreateFinishedCue() => CreateCue(volume: 0.10, 590, 740, 880);

    /// <summary>
    /// Builds a cue from separate steady notes, one after the other.
    ///
    /// Each note fades in and out on its own along a raised cosine, so the
    /// waveform reaches silence before the pitch changes. That is what keeps the
    /// cue click-free without sliding between the pitches — a slide turns the
    /// beeps into a swooping sound nobody asked for.
    /// </summary>
    internal static byte[] CreateCue(double volume, params double[] frequencies)
    {
        int noteCount = (int)(SampleRate * NoteSeconds);
        int leadCount = (int)(SampleRate * LeadSilenceSeconds);
        int tailCount = (int)(SampleRate * TailSilenceSeconds);
        var result = new byte[(leadCount + (noteCount * frequencies.Length) + tailCount) * sizeof(short)];

        for (int note = 0; note < frequencies.Length; note++)
        {
            WriteNote(result, leadCount + (noteCount * note), noteCount, frequencies[note], volume);
        }

        return result;
    }

    private static void WriteNote(byte[] target, int startSample, int sampleCount, double frequency, double volume)
    {
        // Fade lengths as a share of the note: long enough for the ramp to be
        // gradual, short enough that the note still has a solid steady middle.
        const double fadeIn = 0.22;
        const double fadeOut = 0.4;
        double phase = 0;

        for (int i = 0; i < sampleCount; i++)
        {
            double progress = (double)i / sampleCount;
            double envelope =
                RaisedCosine(progress / fadeIn) *
                RaisedCosine((1 - progress) / fadeOut);

            phase += 2 * Math.PI * frequency / SampleRate;
            short sample = (short)(Math.Sin(phase) * short.MaxValue * volume * envelope);
            int at = (startSample + i) * 2;
            target[at] = unchecked((byte)sample);
            target[at + 1] = unchecked((byte)(sample >> 8));
        }
    }

    /// <summary>A 0..1 fade with flat ends, so the waveform has no corner where the fade starts or stops.</summary>
    private static double RaisedCosine(double value) => 0.5 - (0.5 * Math.Cos(Math.PI * Math.Clamp(value, 0, 1)));

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
