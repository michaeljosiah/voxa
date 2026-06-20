using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Voxa.Speech.OpenAI;

/// <summary>
/// <see cref="ISpeechToTextEngine"/> backed by OpenAI's <c>/v1/audio/transcriptions</c> endpoint
/// (Whisper). The engine buffers PCM audio for <see cref="OpenAISpeechOptions.SttBufferSeconds"/>,
/// wraps it as WAV, and posts each chunk for transcription. Each completed batch yields one
/// final <see cref="TranscriptionResult"/>; interim results aren't supported by the REST API.
///
/// For ultra-low-latency streaming, use the Realtime API path
/// (<c>Voxa.Services.AzureVoiceLive</c> with an OpenAI Realtime endpoint) instead.
/// </summary>
public sealed class OpenAIWhisperEngine : ISpeechToTextEngine
{
    private readonly OpenAISpeechOptions _options;
    private readonly HttpClient _http;
    private readonly Channel<TranscriptionResult> _transcripts =
        Channel.CreateUnbounded<TranscriptionResult>();

    private readonly MemoryStream _buffer = new();
    private readonly object _bufferLock = new();
    private CancellationTokenSource? _cts;
    private Task? _flushLoop;
    private int _bytesPerChunk;

    /// <summary>Inject an <see cref="HttpClient"/> for testing; null creates a default one.</summary>
    public OpenAIWhisperEngine(OpenAISpeechOptions options, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = httpClient ?? VoxaHttp.Shared;
    }

    public Task StartAsync(CancellationToken ct)
    {
        // Warm the shared TLS connection so the first transcription skips the handshake.
        if (ReferenceEquals(_http, VoxaHttp.Shared))
            _ = VoxaHttp.WarmupAsync(_http, _options.ApiBaseUrl, ct);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _bytesPerChunk = (int)(_options.InputSampleRate * 2 * _options.SttBufferSeconds);
        // Periodic timer is now a SAFETY BACKSTOP (default 30s) — primary flush path is
        // FlushAsync() called by SpeechToTextProcessor on UserStoppedSpeakingFrame. Setting
        // SttBufferSeconds to 0 disables the timer entirely (caller relies fully on VAD).
        if (_options.SttBufferSeconds > 0)
        {
            _flushLoop = Task.Run(() => FlushLoopAsync(_cts.Token));
        }
        return Task.CompletedTask;
    }

    public ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        lock (_bufferLock) _buffer.Write(pcm.Span);
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync(CancellationToken ct)
        => _transcripts.Reader.ReadAllAsync(ct);

    public async Task StopAsync()
    {
        // Drain the last buffered utterance while the session token is still live, so a graceful stop keeps
        // the final utterance. FlushAsync uses _cts.Token, which is linked to the processor lifetime — so an
        // aborted session cancels this final flush too rather than blocking teardown on it (CQ-008).
        await FlushAsync(force: true).ConfigureAwait(false);
        _cts?.Cancel();
        if (_flushLoop is not null)
        {
            try { await _flushLoop.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        _transcripts.Writer.TryComplete();
    }

    /// <summary>
    /// Force-flush whatever's buffered to Whisper right now. Called by the upstream
    /// <c>SpeechToTextProcessor</c> when it sees a <c>UserStoppedSpeakingFrame</c> — gives
    /// sub-second turn latency instead of waiting for the next timer tick.
    /// </summary>
    public Task FlushAsync() => FlushAsync(force: true);

    public ValueTask DisposeAsync()
    {
        _cts?.Dispose();
        _buffer.Dispose();   // never disposes the shared/injected HttpClient
        return ValueTask.CompletedTask;
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(_options.SttBufferSeconds * 1000), ct).ConfigureAwait(false);
                // Safety-backstop only: only fire if the buffer has actually accumulated past
                // the timeout (i.e. VAD never fired UserStoppedSpeaking — runaway monologue or
                // VAD-less pipeline). Otherwise this is a no-op.
                bool overTimeout;
                lock (_bufferLock) overTimeout = _buffer.Length >= _bytesPerChunk;
                if (overTimeout) await FlushAsync(force: false).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task FlushAsync(bool force)
    {
        byte[] wav;
        lock (_bufferLock)
        {
            if (_buffer.Length == 0) return;
            _ = force; // unused — kept for API symmetry with StopAsync path
            // Single allocation: WAV header + PCM copied straight out of the MemoryStream's
            // internal buffer. Replaces ToArray() (copy 1) + a second copy inside WrapPcmAsWav.
            // TryGetBuffer always succeeds for our own `new MemoryStream()`, but stay correct
            // if the stream type ever changes.
            wav = _buffer.TryGetBuffer(out ArraySegment<byte> seg)
                ? WrapPcmAsWav(seg.AsSpan(), _options.InputSampleRate, channels: 1)
                : WrapPcmAsWav(_buffer.ToArray(), _options.InputSampleRate, channels: 1);
            _buffer.SetLength(0);
        }

        // Session token (linked to the processor lifetime in StartAsync): an aborted session / pipeline
        // teardown cancels the in-flight HTTP call instead of blocking up to HttpClient.Timeout (~100 s) — CQ-008.
        var ct = _cts?.Token ?? CancellationToken.None;
        try
        {
            var text = await TranscribeAsync(wav, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                _transcripts.Writer.TryWrite(new TranscriptionResult(text, IsFinal: true, _options.SttLanguage));
            }
        }
        catch (OperationCanceledException) { /* session torn down mid-flush — teardown, not a transcription failure */ }
        catch (Exception ex)
        {
            _transcripts.Writer.TryComplete(new InvalidOperationException(
                $"OpenAI Whisper transcription failed: {ex.Message}", ex));
        }
    }

    private async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(wav);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audioContent, "file", "audio.wav");
        form.Add(new StringContent(_options.SttModel), "model");
        if (!string.IsNullOrEmpty(_options.SttLanguage))
        {
            form.Add(new StringContent(_options.SttLanguage), "language");
        }
        form.Add(new StringContent("json"), "response_format");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_options.ApiBaseUrl.TrimEnd('/')}/audio/transcriptions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        req.Content = form;

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
        return json.TryGetProperty("text", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
    }

    private static byte[] WrapPcmAsWav(ReadOnlySpan<byte> pcm, int sampleRate, int channels)
    {
        const int bitsPerSample = 16;
        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        int blockAlign = channels * (bitsPerSample / 8);

        var wav = new byte[44 + pcm.Length];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4, 4), 36 + pcm.Length);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22, 2), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28, 4), byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32, 2), (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34, 2), bitsPerSample);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40, 4), pcm.Length);
        pcm.CopyTo(wav.AsSpan(44));
        return wav;
    }
}
