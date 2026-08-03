using System.Diagnostics;
using ShadowWhispr.Services;

namespace ShadowWhispr.Linux.Services;

/// <summary>
/// Plays the short start/stop cues without shipping sound files.
///
/// One pacat playback stream is opened and kept running, and every cue is
/// written into its stdin. Spawning a fresh player per cue clipped the cue's
/// start — the same bug the Windows TonePlayer once had with per-cue devices —
/// because playback begins while PipeWire is still waking the sink. The stream
/// is closed again after a short idle pause so ShadowWhispr does not hold the
/// speakers open all day, and so a device change (headphones plugged in) is
/// picked up on the next cue.
/// </summary>
public sealed class LinuxTonePlayer : IDisposable
{
    private const int SampleRate = 44_100;

    /// <summary>Silence appended to every cue so the tail is never clipped by an underrun.</summary>
    private const double TailSilenceSeconds = 0.15;

    /// <summary>
    /// Silence in front of every cue. Kept very short: with the stream already
    /// open it only has to cover scheduling, and every millisecond here is a
    /// millisecond between the key press and the cue being heard.
    /// </summary>
    private const double LeadSilenceSeconds = 0.005;

    /// <summary>Length of each of the two notes in a cue.</summary>
    private const double NoteSeconds = 0.075;

    /// <summary>
    /// How long the stream stays open after the last cue. Long enough that a
    /// whole dictation session reuses one open stream — opening it is the
    /// slowest part, and a reopen is heard as a clipped first cue.
    /// </summary>
    private static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly Timer _idleTimer;
    private Process? _pacat;
    private byte[]? _pressedCue;
    private byte[]? _releasedCue;
    private bool _disposed;

    public LinuxTonePlayer()
    {
        _idleTimer = new Timer(_ => CloseStream(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event EventHandler<Exception>? PlaybackFailed;

    /// <summary>When true the cues are silently skipped (the user's "no sounds" setting).</summary>
    public bool Muted { get; set; }

    public void PlayPressed() =>
        Play(_pressedCue ??= CreateCue(firstFrequency: 620, secondFrequency: 880, volume: 0.18));

    public void PlayReleased() =>
        Play(_releasedCue ??= CreateCue(firstFrequency: 700, secondFrequency: 420, volume: 0.16));

    private void Play(byte[] samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Muted) return;

        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                EnsureStream();
                // Appended to whatever is still sounding rather than replacing
                // it: each cue is short, so a fast press pair plays both in order.
                var stdin = _pacat!.StandardInput.BaseStream;
                stdin.Write(samples, 0, samples.Length);
                stdin.Flush();
                _idleTimer.Change(IdleClose, Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception ex)
        {
            CloseStream();
            PlaybackFailed?.Invoke(this, ex);
        }
    }

    /// <summary>Opens the playback stream if it is not already running. Call under <see cref="_gate"/>.</summary>
    private void EnsureStream()
    {
        if (_pacat is { HasExited: false }) return;

        DisposeStream();
        var startInfo = new ProcessStartInfo
        {
            FileName = "pacat",
            UseShellExecute = false,
            RedirectStandardInput = true
        };
        startInfo.ArgumentList.Add("--format=s16le");
        startInfo.ArgumentList.Add($"--rate={SampleRate}");
        startInfo.ArgumentList.Add("--channels=1");
        startInfo.ArgumentList.Add("--latency-msec=80");

        _pacat = Process.Start(startInfo)
            ?? throw new InvalidOperationException("pacat could not be started — is PipeWire/Pulse running?");
    }

    private void CloseStream()
    {
        lock (_gate)
        {
            DisposeStream();
        }
    }

    /// <summary>Call under <see cref="_gate"/>.</summary>
    private void DisposeStream()
    {
        if (_pacat is null) return;
        try
        {
            // Closing stdin lets pacat drain what is still buffered and exit on
            // its own; a kill here could cut off a cue that just started.
            _pacat.StandardInput.Close();
            if (!_pacat.WaitForExit(1000)) _pacat.Kill();
        }
        catch (Exception ex)
        {
            AppLog.Write("Closing the cue playback stream failed", ex);
        }
        finally
        {
            _pacat.Dispose();
            _pacat = null;
        }
    }

    /// <summary>
    /// Builds a two-note cue: two separate steady notes, one after the other,
    /// each fading in and out along a raised cosine so the cue is click-free.
    /// Identical waveform to the Windows app, so both platforms sound the same.
    /// </summary>
    private static byte[] CreateCue(double firstFrequency, double secondFrequency, double volume)
    {
        int noteCount = (int)(SampleRate * NoteSeconds);
        int leadCount = (int)(SampleRate * LeadSilenceSeconds);
        int tailCount = (int)(SampleRate * TailSilenceSeconds);
        var result = new byte[(leadCount + (noteCount * 2) + tailCount) * sizeof(short)];

        WriteNote(result, leadCount, noteCount, firstFrequency, volume);
        WriteNote(result, leadCount + noteCount, noteCount, secondFrequency, volume);
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
        CloseStream();
    }
}
