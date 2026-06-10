using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Voxa.Speech.Mistral;

/// <summary>
/// <see cref="ITextToSpeechEngine"/> backed by Mistral's Voxtral-TTS audio endpoint
/// (<c>/v1/audio/speech</c>, OpenAI-compatible schema). Requests <c>response_format=pcm</c> and
/// streams the response in fixed-size chunks.
/// </summary>
public sealed class MistralTextToSpeechEngine : ITextToSpeechEngine
{
    private const int ChunkSize = 8 * 1024;

    private readonly MistralSpeechOptions _options;
    private readonly HttpClient _http;

    public MistralTextToSpeechEngine(MistralSpeechOptions options, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = httpClient ?? VoxaHttp.Shared;
    }

    public Task StartAsync(CancellationToken ct)
    {
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
            model = _options.Model,
            input = text,
            voice = _options.Voice,
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
