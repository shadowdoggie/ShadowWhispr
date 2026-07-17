using System;
using System.Collections.Generic;
using System.IO;
using NAudio.Wave;

namespace ShadowWhispr.Services;

/// <summary>Plays short generated cues without shipping sound files.</summary>
public sealed class TonePlayer : IDisposable
{
    private const int SampleRate = 44_100;
    private readonly object _gate = new();
    private readonly HashSet<Playback> _active = [];
    private bool _disposed;

    public event EventHandler<Exception>? PlaybackFailed;

    public void PlayPressed() => Play(CreateRisingCue());

    public void PlayReleased() => Play(CreateFallingCue());

    private void Play(byte[] samples)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Playback? playback = null;
        try
        {
            var stream = new RawSourceWaveStream(
                new MemoryStream(samples, writable: false),
                new WaveFormat(SampleRate, 16, 1));
            var output = new WaveOutEvent { DesiredLatency = 80 };
            playback = new Playback(output, stream, OnPlaybackStopped);

            lock (_gate)
            {
                _active.Add(playback);
            }

            output.Init(stream);
            output.Play();
        }
        catch (Exception ex)
        {
            if (playback is not null)
            {
                lock (_gate)
                {
                    _active.Remove(playback);
                }

                playback.Dispose();
            }

            PlaybackFailed?.Invoke(this, ex);
        }
    }

    private void OnPlaybackStopped(Playback playback, Exception? exception)
    {
        lock (_gate)
        {
            _active.Remove(playback);
        }

        playback.Dispose();
        if (exception is not null)
        {
            PlaybackFailed?.Invoke(this, exception);
        }
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
        var result = new byte[sampleCount * sizeof(short)];
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Playback[] playbacks;
        lock (_gate)
        {
            playbacks = [.. _active];
            _active.Clear();
        }

        foreach (Playback playback in playbacks)
        {
            playback.Dispose();
        }
    }

    private sealed class Playback : IDisposable
    {
        private readonly WaveOutEvent _output;
        private readonly RawSourceWaveStream _stream;
        private readonly Action<Playback, Exception?> _onStopped;
        private bool _disposed;

        public Playback(
            WaveOutEvent output,
            RawSourceWaveStream stream,
            Action<Playback, Exception?> onStopped)
        {
            _output = output;
            _stream = stream;
            _onStopped = onStopped;
            _output.PlaybackStopped += HandleStopped;
        }

        private void HandleStopped(object? sender, StoppedEventArgs args) => _onStopped(this, args.Exception);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _output.PlaybackStopped -= HandleStopped;
            _output.Dispose();
            _stream.Dispose();
        }
    }
}
