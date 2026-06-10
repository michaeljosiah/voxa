using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Voxa.Speech.OpenAI;

/// <summary>
/// <see cref="ITextToSpeechEngine"/> backed by OpenAI's <c>/v1/audio/speech</c> endpoint.
/// Requests <c>response_format=pcm</c> (raw 24 kHz 16-bit mono) and yields the response stream
/// in fixed-size chunks for low-latency downstream playback.
/// </summary>
public sealed class OpenAITextToSpeechEngine : ITextToSpeechEngine
{
    private const int ChunkSize = 8 * 1024;

    private readonly OpenAISpeechOptions _options;
    private readonly HttpClient _http;

    public OpenAITextToSpeechEngine(OpenAISpeechOptions options, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = httpClient ?? VoxaHttp.Shared;
    }

    public Task StartAsync(CancellationToken ct)
    {
        // Warm the shared connection pool so the first synthesis of the session skips the TLS
        // handshake. Only for the shared client — an injected client is the caller's to manage.
        if (ReferenceEquals(_http, VoxaHttp.Shared))
            _ = VoxaHttp.WarmupAsync(_http, _options.ApiBaseUrl, ct);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;

        var body = JsonSerializer.Serialize(new
        {
            model = _options.TtsModel,
            input = text,
            voice = _options.TtsVoice,
            response_format = "pcm",
        });
        var url = $"{_options.ApiBaseUrl.TrimEnd('/')}/audio/speech";

        HttpRequestMessage MakeRequest()
        {
            var r = new HttpRequestMessage(HttpMethod.Post, url);
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            r.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            return r;
        }

        using var resp = await VoxaHttp.SendWithSingleRetryAsync(_http, MakeRequest, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await foreach (var chunk in PcmStreamReader.ReadEvenChunksAsync(stream, ChunkSize, ct).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;   // never disposes the shared/injected client
}
