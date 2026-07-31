using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NAudio.Wave;

namespace ShadowWhispr.Services;

/// <summary>One prebuilt Gemini voice, as shown in the voice dropdown.</summary>
public sealed record VoiceOption(string Id, string Character)
{
    /// <summary>What the dropdown shows: the name plus what it sounds like.</summary>
    public string DisplayName => $"{Id} — {Character}";
}

/// <summary>
/// Speaks text out loud with Gemini's Live API.
///
/// Live rather than the dedicated TTS models on purpose: the TTS models are
/// capped at a handful of requests a minute on the free tier, which a spoken
/// reply after every agent run would hit almost immediately. Live has no such
/// cap, and returns audio in about the same time.
/// </summary>
public sealed class GeminiVoiceService : IDisposable
{
    /// <summary>
    /// The only model used. Held here rather than offered as a setting because
    /// the free-tier limits that made Live the right choice are specific to it.
    /// </summary>
    private const string Model = "models/gemini-3.1-flash-live-preview";

    private const string Endpoint =
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta." +
        "GenerativeService.BidiGenerateContent?key=";

    /// <summary>Gemini Live returns 24 kHz 16-bit mono PCM, and cannot be asked for anything else.</summary>
    private static readonly WaveFormat OutputFormat = new(24000, 16, 1);

    public const string DefaultVoice = "Leda";

    /// <summary>
    /// Live is a conversational model, so left alone it answers the text rather
    /// than reading it. This tells it to behave like a plain TTS engine.
    /// </summary>
    private const string ReaderInstruction =
        "You are a text-to-speech engine. Read the user's message aloud word for word, in a warm, " +
        "relaxed, natural speaking voice. Never greet, answer, summarise, translate, comment on, or add " +
        "anything at all to the message. Speak exactly what you were given and nothing else.";

    /// <summary>
    /// Every prebuilt voice Gemini offers, with the character Google gives each
    /// one. The full list is offered rather than a curated few because which
    /// voice sounds right is entirely personal.
    /// </summary>
    public static IReadOnlyList<VoiceOption> Voices { get; } =
    [
        new("Leda", "youthful"),
        new("Aoede", "breezy"),
        new("Kore", "firm"),
        new("Zephyr", "bright"),
        new("Callirrhoe", "easy-going"),
        new("Sulafat", "warm"),
        new("Achernar", "soft"),
        new("Autonoe", "bright"),
        new("Despina", "smooth"),
        new("Erinome", "clear"),
        new("Laomedeia", "upbeat"),
        new("Gacrux", "mature"),
        new("Pulcherrima", "forward"),
        new("Vindemiatrix", "gentle"),
        new("Sadachbia", "lively"),
        new("Achird", "friendly"),
        new("Zubenelgenubi", "casual"),
        new("Algieba", "smooth"),
        new("Puck", "upbeat"),
        new("Charon", "informative"),
        new("Fenrir", "excitable"),
        new("Orus", "firm"),
        new("Enceladus", "breathy"),
        new("Iapetus", "clear"),
        new("Umbriel", "easy-going"),
        new("Algenib", "gravelly"),
        new("Rasalgethi", "informative"),
        new("Alnilam", "firm"),
        new("Schedar", "even"),
        new("Sadaltager", "knowledgeable")
    ];

    /// <summary>Falls back to the default rather than failing on an unknown or hand-edited name.</summary>
    public static string NormalizeVoice(string? voice) =>
        Voices.Any(v => string.Equals(v.Id, voice, StringComparison.OrdinalIgnoreCase))
            ? Voices.First(v => string.Equals(v.Id, voice, StringComparison.OrdinalIgnoreCase)).Id
            : DefaultVoice;

    /// <summary>A whole run, connection included, cannot outlive this.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    private readonly object _gate = new();
    private WaveOutEvent? _output;
    private BufferedWaveProvider? _buffer;

    /// <summary>Cancels whatever is currently being fetched or played, so a new request can take over.</summary>
    private CancellationTokenSource? _speaking;

    /// <summary>Raised when speaking fails, so the caller can log it without this class knowing how.</summary>
    public event EventHandler<Exception>? SpeechFailed;

    /// <summary>
    /// Fetches the spoken form of <paramref name="text"/> and plays it, starting
    /// playback on the first chunk rather than waiting for the whole reply, so
    /// speech begins about a second after asking.
    ///
    /// Any speech already playing is stopped first: the newest reply is the one
    /// the user is waiting on.
    /// </summary>
    public async Task SpeakAsync(
        string text,
        string apiKey,
        string? voice,
        double volume,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            AppLog.Write("Voice reply skipped: nothing to say");
            return;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            AppLog.Write("Voice reply skipped: no API key set");
            return;
        }

        Stop();

        var chosen = NormalizeVoice(voice);
        var gain = Math.Clamp(volume, 0d, 1d);

        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        run.CancelAfter(Timeout);
        lock (_gate) _speaking = run;
        var token = run.Token;

        var started = System.Diagnostics.Stopwatch.StartNew();
        var totalBytes = 0;

        try
        {
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(Endpoint + apiKey), token);
            AppLog.Write($"Voice reply connecting: voice={chosen}, {text.Length} characters");

            await SendAsync(socket, new
            {
                setup = new
                {
                    model = Model,
                    generationConfig = new
                    {
                        responseModalities = new[] { "AUDIO" },
                        speechConfig = new
                        {
                            voiceConfig = new { prebuiltVoiceConfig = new { voiceName = chosen } }
                        }
                    },
                    systemInstruction = new { parts = new[] { new { text = ReaderInstruction } } }
                }
            }, token);

            // The server acknowledges setup before it will accept a turn.
            var ack = await ReceiveAsync(socket, token);
            if (ack is null) throw new InvalidOperationException("Gemini closed the connection during setup.");

            await SendAsync(socket, new
            {
                clientContent = new
                {
                    turns = new[] { new { role = "user", parts = new[] { new { text } } } },
                    turnComplete = true
                }
            }, token);

            var firstChunk = true;
            while (!token.IsCancellationRequested)
            {
                var message = await ReceiveAsync(socket, token);
                if (message is null) break;

                using var document = JsonDocument.Parse(message);
                var root = document.RootElement;
                if (!root.TryGetProperty("serverContent", out var content)) continue;

                if (content.TryGetProperty("modelTurn", out var turn) &&
                    turn.TryGetProperty("parts", out var parts))
                {
                    foreach (var part in parts.EnumerateArray())
                    {
                        if (!part.TryGetProperty("inlineData", out var inline) ||
                            !inline.TryGetProperty("data", out var data)) continue;

                        var pcm = Convert.FromBase64String(data.GetString() ?? string.Empty);
                        if (pcm.Length == 0) continue;

                        totalBytes += pcm.Length;
                        if (firstChunk)
                        {
                            firstChunk = false;
                            AppLog.Write($"Voice reply first audio after {started.ElapsedMilliseconds}ms");
                        }

                        ApplyGain(pcm, gain);
                        Enqueue(pcm);
                    }
                }

                if (content.TryGetProperty("turnComplete", out var done) &&
                    done.ValueKind == JsonValueKind.True) break;

                if (content.TryGetProperty("interrupted", out var stopped) &&
                    stopped.ValueKind == JsonValueKind.True)
                {
                    AppLog.Write("Voice reply interrupted by Gemini");
                    break;
                }
            }

            // Closing is a courtesy; a failure here has no effect on the audio
            // that already arrived, so it must not turn a good run into a bad one.
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            }
            catch (Exception ex)
            {
                AppLog.Write("Closing the voice socket failed (audio was already received)", ex);
            }

            AppLog.Write(
                $"Voice reply received: {totalBytes} bytes in {started.ElapsedMilliseconds}ms, " +
                $"about {totalBytes / (double)(OutputFormat.SampleRate * 2):0.0}s of audio");

            await WaitForPlaybackAsync(token);
        }
        catch (OperationCanceledException)
        {
            // Either the user stopped it or a newer reply took over. Neither is
            // a failure, and the caller has nothing to do about it.
            AppLog.Write("Voice reply stopped");
            StopPlayback();
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Write("Voice reply failed", ex);
            StopPlayback();
            SpeechFailed?.Invoke(this, ex);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_speaking, run)) _speaking = null;
            }
        }
    }

    /// <summary>
    /// Stops whatever is being fetched or spoken right now. Safe to call when
    /// nothing is happening, so the abort hotkey can call it unconditionally.
    /// </summary>
    public void Stop()
    {
        CancellationTokenSource? current;
        lock (_gate) current = _speaking;

        if (current is not null)
        {
            try
            {
                current.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // It finished between being read and being cancelled.
            }
        }

        StopPlayback();
    }

    /// <summary>True while audio is queued or playing, so callers can tell whether Stop would do anything.</summary>
    public bool IsSpeaking
    {
        get
        {
            lock (_gate)
            {
                return _speaking is not null ||
                       (_buffer is not null && _buffer.BufferedBytes > 0);
            }
        }
    }

    /// <summary>
    /// Scales the samples in place. Done here rather than through the output
    /// device's own volume, because that one is shared with the rest of the app's
    /// audio session and would quietly turn the cue tones down too.
    /// </summary>
    private static void ApplyGain(byte[] pcm, double gain)
    {
        if (gain >= 0.999) return;

        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (int)Math.Round(BitConverter.ToInt16(pcm, i) * gain);
            sample = Math.Clamp(sample, short.MinValue, short.MaxValue);
            var scaled = BitConverter.GetBytes((short)sample);
            pcm[i] = scaled[0];
            pcm[i + 1] = scaled[1];
        }
    }

    /// <summary>
    /// Hands one chunk to the output device, opening it on the first chunk. The
    /// device is opened lazily so that having the feature switched on does not
    /// hold an audio device open for a user who never triggers a run.
    /// </summary>
    private void Enqueue(byte[] pcm)
    {
        lock (_gate)
        {
            if (_output is null)
            {
                _buffer = new BufferedWaveProvider(OutputFormat)
                {
                    // A long reply is a lot of audio, and the default 5 seconds
                    // would throw the rest of it away.
                    BufferDuration = TimeSpan.FromMinutes(5),
                    DiscardOnBufferOverflow = true
                };
                _output = new WaveOutEvent { DesiredLatency = 120 };
                _output.Init(_buffer);
                _output.Play();
            }

            _buffer!.AddSamples(pcm, 0, pcm.Length);
        }
    }

    /// <summary>
    /// Waits for the queued audio to finish playing, so the caller knows when
    /// the reply has actually been heard rather than merely downloaded.
    /// </summary>
    private async Task WaitForPlaybackAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            int remaining;
            lock (_gate) remaining = _buffer?.BufferedBytes ?? 0;
            if (remaining == 0) break;
            await Task.Delay(120, cancellationToken);
        }

        StopPlayback();
    }

    /// <summary>
    /// Silences and releases the output device. Unlike the cue tones, speech is
    /// cut off mid-word deliberately: when the user aborts a run they want it to
    /// stop talking now, and a click is a fair price for that.
    /// </summary>
    private void StopPlayback()
    {
        lock (_gate)
        {
            try
            {
                _buffer?.ClearBuffer();
                _output?.Stop();
                _output?.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Write("Stopping voice playback failed", ex);
            }
            finally
            {
                _output = null;
                _buffer = null;
            }
        }
    }

    private static async Task SendAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }

    /// <summary>
    /// Reads one whole message. Gemini splits large audio messages across
    /// frames, so a single receive is not enough.
    /// </summary>
    private static async Task<string?> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[32 * 1024];
        using var message = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }

        return message.Length == 0 ? null : Encoding.UTF8.GetString(message.ToArray());
    }

    public void Dispose()
    {
        Stop();
        StopPlayback();
    }
}
