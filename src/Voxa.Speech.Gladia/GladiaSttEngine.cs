using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Voxa.Speech.Gladia;

/// <summary>
/// Gladia live (v2) streaming <see cref="ISpeechToTextEngine"/> over <see cref="WebSocketSttEngine"/>. Gladia is
/// two-step: an HTTP <c>POST /v2/live</c> (with the API key) returns a pre-signed WebSocket URL, which the base
/// then connects to — so this engine overrides <see cref="WebSocketSttEngine.ResolveEndpointAsync"/> to do the
/// init POST. Locked segments accumulate into one VAD-gated final per utterance (see <see cref="WebSocketSttEngine"/>).
/// </summary>
public sealed class GladiaSttEngine : WebSocketSttEngine
{
    private static readonly byte[] StopFrame = Encoding.UTF8.GetBytes("{\"type\":\"stop_recording\"}");
    private readonly GladiaOptions _options;
    private readonly HttpClient _http;

    public GladiaSttEngine(GladiaOptions options, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = httpClient ?? VoxaHttp.Shared;
    }

    protected override string? Language => _options.Language;

    protected override async Task<Uri> ResolveEndpointAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_options.ApiBaseUrl.TrimEnd('/')}/v2/live");
        req.Headers.Add("x-gladia-key", _options.ApiKey);
        req.Content = JsonContent.Create(new
        {
            encoding = "wav/pcm",
            sample_rate = _options.InputSampleRate,
            bit_depth = 16,
            channels = 1,
            language_config = string.IsNullOrEmpty(_options.Language) ? null : new { languages = new[] { _options.Language } },
            // Stream partial (interim) transcripts, not just finals — gives a live transcript and lets the
            // shared accumulator re-arm on a new utterance's first interim instead of relying on the timeout.
            messages_config = new { receive_partial_transcripts = true },
        });

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var session = await resp.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
        var url = session.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(url))
            throw new InvalidOperationException("Gladia live session did not return a WebSocket url.");
        return new Uri(url);
    }

    protected override ReadOnlyMemory<byte>? BuildCloseMessage() => StopFrame;

    protected override IEnumerable<SttFragment> ParseMessage(string message) => Parse(message);

    /// <summary>
    /// Pure parser (testable). Only a <c>transcript</c> message with non-empty <c>data.utterance.text</c> yields a
    /// fragment; <c>data.is_final</c> marks it a locked segment. Other message types yield nothing.
    /// </summary>
    internal static IEnumerable<SttFragment> Parse(string message)
    {
        using var doc = JsonDocument.Parse(message);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) yield break;
        if (!root.TryGetProperty("type", out var type) || type.GetString() != "transcript") yield break;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) yield break;

        var text = data.TryGetProperty("utterance", out var utt) && utt.TryGetProperty("text", out var t)
            ? t.GetString() ?? string.Empty : string.Empty;
        if (text.Length == 0) yield break;

        bool isFinal = data.TryGetProperty("is_final", out var f) && f.ValueKind == JsonValueKind.True;
        yield return new SttFragment(text, isFinal);
    }
}
