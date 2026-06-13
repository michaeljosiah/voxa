using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Voxa.Speech.Mistral;

/// <summary>
/// <see cref="ISpeechToTextEngine"/> backed by Mistral's Voxtral transcription endpoint
/// (<c>/v1/audio/transcriptions</c>, OpenAI-compatible schema). The REST API is request/response,
/// so the engine buffers PCM and posts the whole utterance as WAV when the upstream
/// <see cref="SpeechToTextProcessor"/> flushes at speech-end. Each batch yields one final
/// <see cref="TranscriptionResult"/>; interim hypotheses aren't available (VVL-001 WS2, §11 defers streaming).
/// </summary>
public sealed class MistralSpeechToTextEngine : ISpeechToTextEngine
{
    private readonly MistralSpeechOptions _options;
    private readonly HttpClient _http;
    private readonly Channel<TranscriptionResult> _transcripts =
        Channel.CreateUnbounded<TranscriptionResult>();

    private readonly MemoryStream _buffer = new();
    private readonly object _bufferLock = new();
    private CancellationTokenSource? _cts;
    private Task? _flushLoop;
    private int _bytesPerChunk;

    public MistralSpeechToTextEngine(MistralSpeechOptions options, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = httpClient ?? VoxaHttp.Shared;
    }

    public Task StartAsync(CancellationToken ct)
    {
        if (ReferenceEquals(_http, VoxaHttp.Shared))
            _ = VoxaHttp.WarmupAsync(_http, _options.ApiBaseUrl, ct);

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _bytesPerChunk = (int)(_options.InputSampleRate * 2 * _options.SttBufferSeconds);
        if (_options.SttBufferSeconds > 0)
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
        await FlushBufferAsync().ConfigureAwait(false);
        _transcripts.Writer.TryComplete();
    }

    /// <summary>Post whatever's buffered now — called by the processor on <c>UserStoppedSpeakingFrame</c>.</summary>
    public Task FlushAsync() => FlushBufferAsync();

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
                bool overTimeout;
                lock (_bufferLock) overTimeout = _buffer.Length >= _bytesPerChunk;
                if (overTimeout) await FlushBufferAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private async Task FlushBufferAsync()
    {
        byte[] wav;
        lock (_bufferLock)
        {
            if (_buffer.Length == 0) return;
            wav = _buffer.TryGetBuffer(out ArraySegment<byte> seg)
                ? WrapPcmAsWav(seg.AsSpan(), _options.InputSampleRate, channels: 1)
                : WrapPcmAsWav(_buffer.ToArray(), _options.InputSampleRate, channels: 1);
            _buffer.SetLength(0);
        }

        try
        {
            var text = await TranscribeAsync(wav).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
                _transcripts.Writer.TryWrite(new TranscriptionResult(text, IsFinal: true, _options.SttLanguage));
        }
        catch (Exception ex)
        {
            _transcripts.Writer.TryComplete(new InvalidOperationException(
                $"Mistral Voxtral transcription failed: {ex.Message}", ex));
        }
    }

    private async Task<string> TranscribeAsync(byte[] wav)
    {
        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "audio.wav");
        form.Add(new StringContent(_options.SttModel), "model");
        if (!string.IsNullOrEmpty(_options.SttLanguage))
            form.Add(new StringContent(_options.SttLanguage), "language");

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_options.ApiBaseUrl.TrimEnd('/')}/audio/transcriptions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        req.Content = form;

        using var resp = await _http.SendAsync(req).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>().ConfigureAwait(false);
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
