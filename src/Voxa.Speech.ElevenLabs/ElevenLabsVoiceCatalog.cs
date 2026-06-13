using System.Net.Http.Headers;
using System.Text.Json;
using Voxa.Speech.Voices;

namespace Voxa.Speech.ElevenLabs;

/// <summary>
/// ElevenLabs voice catalog + cloning (VVL-001 WS1) over the public REST API: list (<c>GET /voices</c>),
/// instant clone (<c>POST /voices/add</c>, multipart), and delete (<c>DELETE /voices/{id}</c>). Only the
/// API key is required — not <c>VoiceId</c>, which only synthesis needs. A blank key surfaces as a
/// <see cref="VoiceProviderException"/> with <see cref="VoiceProviderException.MissingApiKey"/> set, and a
/// non-success response (e.g. free-tier cloning is plan-gated) carries the provider's own message.
/// </summary>
public sealed class ElevenLabsVoiceCatalog : IVoiceCatalogProvider, IVoiceCloneProvider
{
    private const string ProviderName = "ElevenLabs";

    private readonly ElevenLabsOptions _options;
    private readonly HttpClient _http;

    public ElevenLabsVoiceCatalog(ElevenLabsOptions options, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = httpClient ?? VoxaHttp.Shared;
    }

    private string BaseUrl => _options.ApiBaseUrl.TrimEnd('/');

    public async Task<IReadOnlyList<ProviderVoice>> ListVoicesAsync(CancellationToken ct)
    {
        RequireKey();

        using var req = Authed(HttpMethod.Get, $"{BaseUrl}/voices");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureOkAsync(resp, "list voices", ct).ConfigureAwait(false);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var voices = new List<ProviderVoice>();
        if (doc.RootElement.TryGetProperty("voices", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in arr.EnumerateArray())
            {
                var id = Str(v, "voice_id");
                if (id is null) continue;
                voices.Add(new ProviderVoice(
                    Id: id,
                    DisplayName: Str(v, "name") ?? id,
                    ProviderName: ProviderName,
                    Kind: KindFromCategory(Str(v, "category")),
                    Language: null,
                    PreviewUrl: Str(v, "preview_url"),
                    Description: Str(v, "description")));
            }
        }
        return voices;
    }

    public async Task<ProviderVoice> CreateVoiceAsync(VoiceCloneRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireKey();
        if (request.Samples.Count == 0)
            throw new VoiceProviderException("At least one audio sample is required to clone a voice.");

        using var form = new MultipartFormDataContent { { new StringContent(request.Name), "name" } };
        if (!string.IsNullOrWhiteSpace(request.Description))
            form.Add(new StringContent(request.Description), "description");
        foreach (var sample in request.Samples)
        {
            var part = new ByteArrayContent(sample.Data.ToArray());
            part.Headers.ContentType = new MediaTypeHeaderValue(sample.Mime);
            form.Add(part, "files", sample.FileName);
        }

        using var req = Authed(HttpMethod.Post, $"{BaseUrl}/voices/add");
        req.Content = form;
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureOkAsync(resp, "clone voice", ct).ConfigureAwait(false);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var id = Str(doc.RootElement, "voice_id")
                 ?? throw new VoiceProviderException("ElevenLabs accepted the clone but returned no voice id.");

        // The newly minted voice, as the picker will see it — a fresh clone is always Cloned.
        return new ProviderVoice(id, request.Name, ProviderName, VoiceKind.Cloned,
            request.Language, PreviewUrl: null, request.Description);
    }

    public async Task DeleteVoiceAsync(string voiceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(voiceId)) throw new ArgumentException("Voice id is required.", nameof(voiceId));
        RequireKey();

        using var req = Authed(HttpMethod.Delete, $"{BaseUrl}/voices/{Uri.EscapeDataString(voiceId)}");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureOkAsync(resp, "delete voice", ct).ConfigureAwait(false);
    }

    private void RequireKey()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new VoiceProviderException(
                "ElevenLabs API key required to list or clone voices. Set Voxa:ElevenLabs:ApiKey.",
                missingApiKey: true);
    }

    private HttpRequestMessage Authed(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("xi-api-key", _options.ApiKey);
        return req;
    }

    private static async Task EnsureOkAsync(HttpResponseMessage resp, string action, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await SafeReadAsync(resp, ct).ConfigureAwait(false);
        throw new VoiceProviderException(
            $"ElevenLabs could not {action} ({(int)resp.StatusCode} {resp.ReasonPhrase}). {body}".TrimEnd());
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim(); }
        catch { return string.Empty; }
    }

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    // ElevenLabs categories: "premade" ships with the platform; everything else (cloned,
    // professional, generated) is a user/account voice.
    private static VoiceKind KindFromCategory(string? category)
        => string.Equals(category, "premade", StringComparison.OrdinalIgnoreCase)
            ? VoiceKind.Standard
            : VoiceKind.Cloned;
}
