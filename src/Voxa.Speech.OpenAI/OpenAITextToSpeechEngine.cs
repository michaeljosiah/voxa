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
    private readonly bool _ownsHttpClient;

    public OpenAITextToSpeechEngine(OpenAISpeechOptions options, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async IAsyncEnumerable<byte[]> SynthesizeAsync(
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

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_options.ApiBaseUrl.TrimEnd('/')}/audio/speech");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[ChunkSize];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, ChunkSize), ct).ConfigureAwait(false)) > 0)
        {
            var chunk = new byte[read];
            Buffer.BlockCopy(buffer, 0, chunk, 0, read);
            yield return chunk;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient) _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
