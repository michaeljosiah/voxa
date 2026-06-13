using System.Net;
using System.Text;
using Voxa.Speech.ElevenLabs;
using Voxa.Speech.Voices;

namespace Voxa.Speech.ElevenLabs.Tests;

/// <summary>
/// VVL-001 WS1: the ElevenLabs catalog/clone capability — payload mapping, multipart clone, and
/// the typed key-required result (never a raw HttpRequestException). All offline via a fake handler.
/// </summary>
public class ElevenLabsVoiceCatalogTests
{
    private static ElevenLabsOptions Keyed() => new()
    {
        ApiKey = "el-test",
        VoiceId = string.Empty,   // catalog/clone need no VoiceId
        ApiBaseUrl = "https://api.elevenlabs.io/v1",
    };

    private sealed record Captured(HttpMethod Method, Uri Uri, string XiApiKey, byte[] Body, string? ContentType);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<Captured> Snapshots { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            var key = request.Headers.TryGetValues("xi-api-key", out var v) ? string.Join(",", v) : "";
            Snapshots.Add(new Captured(request.Method, request.RequestUri!, key, body, request.Content?.Headers.ContentType?.MediaType));
            return Respond(request);
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    [Fact] // WS1-A1
    public async Task ListVoicesAsync_Maps_Standard_And_Cloned_Voices()
    {
        var handler = new CapturingHandler
        {
            Respond = _ => Json("""
            {
              "voices": [
                { "voice_id": "premade1", "name": "Rachel", "category": "premade", "preview_url": "https://x/p.mp3" },
                { "voice_id": "clone1",   "name": "My Voice", "category": "cloned", "description": "me" }
              ]
            }
            """),
        };
        var catalog = new ElevenLabsVoiceCatalog(Keyed(), new HttpClient(handler));

        var voices = await catalog.ListVoicesAsync(default);

        var snap = Assert.Single(handler.Snapshots);
        Assert.Equal(HttpMethod.Get, snap.Method);
        Assert.EndsWith("/voices", snap.Uri.ToString());
        Assert.Equal("el-test", snap.XiApiKey);

        Assert.Collection(voices,
            v => { Assert.Equal("premade1", v.Id); Assert.Equal(VoiceKind.Standard, v.Kind); Assert.Equal("ElevenLabs", v.ProviderName); Assert.Equal("https://x/p.mp3", v.PreviewUrl); },
            v => { Assert.Equal("clone1", v.Id); Assert.Equal("My Voice", v.DisplayName); Assert.Equal(VoiceKind.Cloned, v.Kind); Assert.Equal("me", v.Description); });
    }

    [Fact] // WS1-A2
    public async Task CreateVoiceAsync_Posts_Multipart_With_Samples_And_Name_Then_Parses_Id()
    {
        var handler = new CapturingHandler { Respond = _ => Json("""{ "voice_id": "new-clone-id" }""") };
        var catalog = new ElevenLabsVoiceCatalog(Keyed(), new HttpClient(handler));

        var sample = new VoiceSample("ref.wav", new byte[] { 1, 2, 3, 4 });
        var voice = await catalog.CreateVoiceAsync(new VoiceCloneRequest("Aria", [sample], Description: "test"), default);

        var snap = Assert.Single(handler.Snapshots);
        Assert.Equal(HttpMethod.Post, snap.Method);
        Assert.EndsWith("/voices/add", snap.Uri.ToString());
        Assert.Equal("el-test", snap.XiApiKey);
        Assert.StartsWith("multipart/form-data", snap.ContentType);

        // The multipart body carries the name field, the sample file, its bytes, and the description.
        var bodyText = Encoding.Latin1.GetString(snap.Body);
        Assert.Contains("Content-Disposition: form-data", bodyText);
        Assert.Contains("name=name", bodyText.Replace("\"", ""));   // quoting varies by runtime
        Assert.Contains("Aria", bodyText);
        Assert.Contains("ref.wav", bodyText);
        Assert.Contains("\x01\x02\x03\x04", bodyText);
        Assert.Contains("test", bodyText);                          // description part

        Assert.Equal("new-clone-id", voice.Id);
        Assert.Equal("Aria", voice.DisplayName);
        Assert.Equal(VoiceKind.Cloned, voice.Kind);
    }

    [Fact] // WS1-A2 (key only in the header, never in the body)
    public async Task CreateVoiceAsync_Never_Puts_The_Key_In_The_Body()
    {
        var handler = new CapturingHandler { Respond = _ => Json("""{ "voice_id": "x" }""") };
        var catalog = new ElevenLabsVoiceCatalog(Keyed(), new HttpClient(handler));

        await catalog.CreateVoiceAsync(new VoiceCloneRequest("V", [new VoiceSample("a.wav", new byte[] { 9 })]), default);

        var snap = Assert.Single(handler.Snapshots);
        Assert.DoesNotContain("el-test", Encoding.Latin1.GetString(snap.Body));
    }

    [Fact] // WS1-A3
    public async Task A_Blank_Key_Yields_A_Typed_Missing_Key_Result_Not_An_Http_Exception()
    {
        var handler = new CapturingHandler();   // must never be hit
        var blank = Keyed() with { ApiKey = string.Empty };
        var catalog = new ElevenLabsVoiceCatalog(blank, new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<VoiceProviderException>(() => catalog.ListVoicesAsync(default));
        Assert.True(ex.MissingApiKey);
        Assert.Empty(handler.Snapshots);   // short-circuited before any request
    }

    [Fact] // WS1-A3 (plan-gated clone surfaces readably, not as HttpRequestException)
    public async Task A_Provider_Rejection_Surfaces_As_A_Readable_VoiceProviderException()
    {
        var handler = new CapturingHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{ "detail": "can_not_use_instant_voice_cloning" }"""),
            },
        };
        var catalog = new ElevenLabsVoiceCatalog(Keyed(), new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<VoiceProviderException>(() =>
            catalog.CreateVoiceAsync(new VoiceCloneRequest("V", [new VoiceSample("a.wav", new byte[] { 1 })]), default));
        Assert.False(ex.MissingApiKey);
        Assert.Contains("instant_voice_cloning", ex.Message);
    }
}
