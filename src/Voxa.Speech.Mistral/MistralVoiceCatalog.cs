using System.Net.Http.Headers;
using System.Text.Json;
using Voxa.Speech.Voices;

namespace Voxa.Speech.Mistral;

/// <summary>
/// Mistral voice catalog + cloning (VVL-001 WS2) over <c>/v1/audio/voices</c> — list (GET), create
/// from a reference sample (POST, multipart), delete (DELETE). Bearer-authenticated. A created
/// voice is addressed by name in <c>Voxa:Mistral:Voice</c> at synthesis time.
///
/// <para>Mistral's voice schema is newer and less stable than ElevenLabs', so the response is parsed
/// defensively: id is read from the first present of <c>id</c>/<c>voice_id</c>/<c>voice</c>/<c>name</c>,
/// and the list accepts either a <c>voices</c> array or a bare top-level array.</para>
/// </summary>
public sealed class MistralVoiceCatalog : IVoiceCatalogProvider, IVoiceCloneProvider
{
    private const string ProviderName = "Mistral";

    private readonly MistralSpeechOptions _options;
    private readonly HttpClient _http;

    public MistralVoiceCatalog(MistralSpeechOptions options, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _http = httpClient ?? VoxaHttp.Shared;
    }

    private string BaseUrl => _options.ApiBaseUrl.TrimEnd('/');

    public async Task<IReadOnlyList<ProviderVoice>> ListVoicesAsync(CancellationToken ct)
    {
        RequireKey();

        using var req = Authed(HttpMethod.Get, $"{BaseUrl}/audio/voices");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureOkAsync(resp, "list voices", ct).ConfigureAwait(false);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var root = doc.RootElement;
        var array = root.ValueKind == JsonValueKind.Array ? root
            : root.TryGetProperty("voices", out var v) && v.ValueKind == JsonValueKind.Array ? v
            : default;

        var voices = new List<ProviderVoice>();
        if (array.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in array.EnumerateArray())
            {
                var id = FirstStr(e, "id", "voice_id", "voice", "name");
                if (id is null) continue;
                voices.Add(new ProviderVoice(
                    Id: id,
                    DisplayName: FirstStr(e, "name", "display_name") ?? id,
                    ProviderName: ProviderName,
                    // Mistral built-ins (e.g. "alloy") are Standard; anything user-created is a clone.
                    Kind: BuiltInVoices.Contains(id) ? VoiceKind.Standard : VoiceKind.Cloned,
                    Language: FirstStr(e, "language", "lang"),
                    PreviewUrl: null,
                    Description: FirstStr(e, "description")));
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
            form.Add(part, "file", sample.FileName);
        }

        using var req = Authed(HttpMethod.Post, $"{BaseUrl}/audio/voices");
        req.Content = form;
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureOkAsync(resp, "clone voice", ct).ConfigureAwait(false);

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        // Mistral addresses voices by name; fall back to the requested name if no id field is present.
        var id = FirstStr(doc.RootElement, "id", "voice_id", "voice", "name") ?? request.Name;

        return new ProviderVoice(id, request.Name, ProviderName, VoiceKind.Cloned,
            request.Language, PreviewUrl: null, request.Description);
    }

    public async Task DeleteVoiceAsync(string voiceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(voiceId)) throw new ArgumentException("Voice id is required.", nameof(voiceId));
        RequireKey();

        using var req = Authed(HttpMethod.Delete, $"{BaseUrl}/audio/voices/{Uri.EscapeDataString(voiceId)}");
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        await EnsureOkAsync(resp, "delete voice", ct).ConfigureAwait(false);
    }

    // Mistral's stock OpenAI-compatible voice names — treated as Standard in the catalog.
    private static readonly HashSet<string> BuiltInVoices = new(StringComparer.OrdinalIgnoreCase)
        { "alloy", "echo", "fable", "onyx", "nova", "shimmer" };

    private void RequireKey()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new VoiceProviderException(
                "Mistral API key required to list or clone voices. Set Voxa:Mistral:ApiKey.",
                missingApiKey: true);
    }

    private HttpRequestMessage Authed(HttpMethod method, string url)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        return req;
    }

    private static async Task EnsureOkAsync(HttpResponseMessage resp, string action, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await SafeReadAsync(resp, ct).ConfigureAwait(false);
        throw new VoiceProviderException(
            $"Mistral could not {action} ({(int)resp.StatusCode} {resp.ReasonPhrase}). {body}".TrimEnd());
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return (await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim(); }
        catch { return string.Empty; }
    }

    private static string? FirstStr(JsonElement e, params string[] names)
    {
        foreach (var name in names)
            if (e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
                return p.GetString();
        return null;
    }
}
