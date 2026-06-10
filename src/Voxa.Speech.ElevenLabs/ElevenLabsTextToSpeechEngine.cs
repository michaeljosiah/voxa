using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voxa.Speech.ElevenLabs;

/// <summary>
/// <see cref="ITextToSpeechEngine"/> backed by ElevenLabs' streaming TTS endpoint
/// (<c>/v1/text-to-speech/{voiceId}/stream</c>). Requests raw PCM at the configured sample rate
/// and yields the response stream in fixed-size chunks.
/// </summary>
public sealed class ElevenLabsTextToSpeechEngine : ITextToSpeechEngine
{
    private const int ChunkSize = 8 * 1024;

    // Plain defaults — anonymous-type member names map to JSON keys verbatim.
    // ElevenLabs expects snake_case (model_id, voice_settings).
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ElevenLabsOptions _options;
    private readonly HttpClient _http;

    public ElevenLabsTextToSpeechEngine(ElevenLabsOptions options, HttpClient? httpClient = null)
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
            text,
            model_id = _options.ModelId,
            voice_settings = _options.VoiceSettings is null ? null : new
            {
                stability = _options.VoiceSettings.Stability,
                similarity_boost = _options.VoiceSettings.SimilarityBoost,
                style = _options.VoiceSettings.Style,
                speed = _options.VoiceSettings.Speed,
                use_speaker_boost = _options.VoiceSettings.UseSpeakerBoost,
            },
        }, JsonOpts);

        var url = $"{_options.ApiBaseUrl.TrimEnd('/')}/text-to-speech/{Uri.EscapeDataString(_options.VoiceId)}/stream?output_format=pcm_{_options.OutputSampleRate}";

        HttpRequestMessage MakeRequest()
        {
            var r = new HttpRequestMessage(HttpMethod.Post, url);
            r.Headers.Add("xi-api-key", _options.ApiKey);
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
