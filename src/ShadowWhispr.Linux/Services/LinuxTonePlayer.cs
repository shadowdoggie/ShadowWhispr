using System.Diagnostics;
using ShadowWhispr.Services;

namespace ShadowWhispr.Linux.Services;

/// <summary>
/// Plays the short start/stop cues without shipping sound files. The cues are
/// synthesized once into small WAV files under the temp folder (same waveforms
/// as the Windows TonePlayer) and played through paplay, which talks straight
/// to PipeWire/Pulse and needs no persistent device handling here.
/// </summary>
public sealed class LinuxTonePlayer : IDisposable
{
    private const int SampleRate = 44_100;
    private const double TailSilenceSeconds = 0.15;
    private const double LeadSilenceSeconds = 0.005;
    private const double NoteSeconds = 0.075;

    private readonly object _gate = new();
    private string? _pressedCuePath;
    private string? _releasedCuePath;
    private bool _disposed;

    public event EventHandler<Exception>? PlaybackFailed;

    /// <summary>When true the cues are silently skipped (the user's "no sounds" setting).</summary>
    public bool Muted { get; set; }

    public void PlayPressed() => Play(ref _pressedCuePath, "cue-pressed.wav",
        () => CreateCue(firstFrequency: 620, secondFrequency: 880, volume: 0.18));

    public void PlayReleased() => Play(ref _releasedCuePath, "cue-released.wav",
        () => CreateCue(firstFrequency: 700, secondFrequency: 420, volume: 0.16));

    private void Play(ref string? cuePath, string fileName, Func<byte[]> synthesize)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Muted) return;

        try
        {
            string path;
            lock (_gate)
            {
                if (cuePath is null || !File.Exists(cuePath))
                {
                    var directory = Path.Combine(Path.GetTempPath(), "ShadowWhispr", "cues");
                    Directory.CreateDirectory(directory);
                    cuePath = Path.Combine(directory, fileName);
                    WriteWav(cuePath, synthesize());
                }
                path = cuePath;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "paplay",
                ArgumentList = { path },
                UseShellExecute = false
            });
        }
        catch (Exception ex)
        {
            PlaybackFailed?.Invoke(this, ex);
        }
    }

    private static void WriteWav(string path, byte[] samples)
    {
        using var wav = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(wav);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples.Length);
        writer.Write(samples);
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

    private static double RaisedCosine(double value) => 0.5 - (0.5 * Math.Cos(Math.PI * Math.Clamp(value, 0, 1)));

    public void Dispose() => _disposed = true;
}
