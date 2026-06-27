using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Voxa.Speech.Mistral;

/// <summary>
/// <see cref="ISpeechToTextEngine"/> backed by Mistral's Voxtral transcription endpoint
/// (<c>/v1/audio/transcriptions</c>, OpenAI-compatible schema). The API takes a complete clip (not incremental
/// audio), so the engine buffers PCM and posts the whole utterance as WAV when the upstream
/// <see cref="SpeechToTextProcessor"/> flushes at speech-end.
///
/// <para>When <see cref="MistralSpeechOptions.SttStreaming"/> is set (the default), the POST uses
/// <c>stream=true</c> and the SSE response is surfaced as interim <see cref="TranscriptionResult"/>s
/// (<c>transcription.text.delta</c>) followed by one final (<c>transcription.done</c>) — lower perceived latency.
/// With streaming off, the whole utterance yields a single batched final.</para>
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
        // Drain the last buffered utterance while the session token is still live, so a graceful stop keeps
        // it. FlushBufferAsync uses _cts.Token (linked to the processor lifetime), so an aborted session
        // cancels this final flush too rather than blocking teardown up to HttpClient.Timeout (CQ-008).
        await FlushBufferAsync().ConfigureAwait(false);
        _cts?.Cancel();
        if (_flushLoop is not null)
        {
            try { await _flushLoop.ConfigureAwait(false); } catch { /* shutdown */ }
        }
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
                ? Pcm16Wav.Wrap(seg.AsSpan(), _options.InputSampleRate)
                : Pcm16Wav.Wrap(_buffer.ToArray(), _options.InputSampleRate);
            _buffer.SetLength(0);
        }

        // Session token (linked to the processor lifetime in StartAsync): an aborted session / pipeline
        // teardown cancels the in-flight HTTP call instead of blocking up to HttpClient.Timeout (~100 s) — CQ-008.
        var ct = _cts?.Token ?? CancellationToken.None;
        try
        {
            if (_options.SttStreaming)
            {
                await TranscribeStreamingAsync(wav, ct).ConfigureAwait(false);
            }
            else
            {
                var text = await TranscribeAsync(wav, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(text))
                    _transcripts.Writer.TryWrite(new TranscriptionResult(text, IsFinal: true, _options.SttLanguage));
            }
        }
        catch (OperationCanceledException) { /* session torn down mid-flush — teardown, not a transcription failure */ }
        catch (Exception ex)
        {
            _transcripts.Writer.TryComplete(new InvalidOperationException(
                $"Mistral Voxtral transcription failed: {ex.Message}", ex));
        }
    }

    private async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        using var form = BuildForm(wav, stream: false);
        using var req = NewTranscriptionRequest(form);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
        return json.TryGetProperty("text", out var t) ? (t.GetString() ?? string.Empty) : string.Empty;
    }

    /// <summary>
    /// Streaming variant: POST <c>stream=true</c> and surface the SSE response as interim deltas + one final.
    /// Writes directly to the transcript channel (interims with <c>IsFinal:false</c>, the settled text as final).
    /// </summary>
    private async Task TranscribeStreamingAsync(byte[] wav, CancellationToken ct)
    {
        using var form = BuildForm(wav, stream: true);
        using var req = NewTranscriptionRequest(form);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(body, Encoding.UTF8);

        var running = new StringBuilder();
        var language = _options.SttLanguage;
        var emittedFinal = false;

        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (!MistralSttStream.TryReadDataLine(line, out var payload)) continue;
            if (MistralSttStream.IsDoneSentinel(payload)) break;

            if (MistralSttStream.Parse(payload) is not { } ev) continue;
            if (ev.Language is { Length: > 0 } lang) language = lang;

            switch (ev.Kind)
            {
                case MistralSttEventKind.Delta when ev.Text.Length > 0:
                    running.Append(ev.Text);
                    _transcripts.Writer.TryWrite(new TranscriptionResult(running.ToString(), IsFinal: false, language));
                    break;
                case MistralSttEventKind.Done:
                    var final = ev.Text.Length > 0 ? ev.Text : running.ToString();
                    if (!string.IsNullOrWhiteSpace(final))
                    {
                        _transcripts.Writer.TryWrite(new TranscriptionResult(final, IsFinal: true, language));
                        emittedFinal = true;
                    }
                    break;
            }
        }

        // Stream closed without an explicit done event but deltas accumulated — settle them as the final.
        if (!emittedFinal && running.Length > 0)
            _transcripts.Writer.TryWrite(new TranscriptionResult(running.ToString(), IsFinal: true, language));
    }

    private MultipartFormDataContent BuildForm(byte[] wav, bool stream)
    {
        var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "audio.wav");
        form.Add(new StringContent(_options.SttModel), "model");
        if (stream)
            form.Add(new StringContent("true"), "stream");
        if (!string.IsNullOrEmpty(_options.SttLanguage))
            form.Add(new StringContent(_options.SttLanguage), "language");
        return form;
    }

    private HttpRequestMessage NewTranscriptionRequest(HttpContent content)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_options.ApiBaseUrl.TrimEnd('/')}/audio/transcriptions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        req.Content = content;
        return req;
    }

}
