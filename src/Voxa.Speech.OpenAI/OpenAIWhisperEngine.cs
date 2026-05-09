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
    private readonly bool _ownsHttpClient;
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
        _http = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _bytesPerChunk = (int)(_options.InputSampleRate * 2 * _options.SttBufferSeconds);
        _flushLoop = Task.Run(() => FlushLoopAsync(_cts.Token));
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
        _cts?.Cancel();
        if (_flushLoop is not null)
        {
            try { await _flushLoop.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        await FlushAsync(force: true).ConfigureAwait(false);
        _transcripts.Writer.TryComplete();
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Dispose();
        if (_ownsHttpClient) _http.Dispose();
        _buffer.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(_options.SttBufferSeconds * 1000), ct).ConfigureAwait(false);
                await FlushAsync(force: false).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task FlushAsync(bool force)
    {
        byte[] pcm;
        lock (_bufferLock)
        {
            if (_buffer.Length == 0) return;
            // Always flush whatever's accumulated. The timer ticks every SttBufferSeconds — that
            // IS the latency budget. The previous "wait for ≥ SttBufferSeconds of audio" check
            // meant a short utterance never crossed the threshold, the timer kept skipping the
            // flush, and the bot fell minutes behind. The `force` flag is kept for the StopAsync
            // path where we explicitly want to drain regardless of state.
            _ = force; // currently equivalent — kept for API symmetry with StopAsync path
            pcm = _buffer.ToArray();
            _buffer.SetLength(0);
        }

        try
        {
            var text = await TranscribeAsync(pcm).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                _transcripts.Writer.TryWrite(new TranscriptionResult(text, IsFinal: true, _options.SttLanguage));
            }
        }
        catch (Exception ex)
        {
            _transcripts.Writer.TryComplete(new InvalidOperationException(
                $"OpenAI Whisper transcription failed: {ex.Message}", ex));
        }
    }

    private async Task<string> TranscribeAsync(byte[] pcm)
    {
        var wav = WrapPcmAsWav(pcm, _options.InputSampleRate, channels: 1);

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

        using var resp = await _http.SendAsync(req).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
        return json.TryGetProperty("text", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
    }

    private static byte[] WrapPcmAsWav(byte[] pcm, int sampleRate, int channels)
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
        Buffer.BlockCopy(pcm, 0, wav, 44, pcm.Length);
        return wav;
    }
}
