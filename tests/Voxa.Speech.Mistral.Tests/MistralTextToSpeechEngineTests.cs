using System.Net;
using System.Text.Json;
using Voxa.Speech.Mistral;

namespace Voxa.Speech.Mistral.Tests;

public class MistralTextToSpeechEngineTests
{
    private static MistralSpeechOptions MakeOptions() => new()
    {
        ApiKey = "mistral-test",
        Model = "voxtral-tts",
        Voice = "alloy",
    };

    private sealed record Captured(HttpMethod Method, Uri RequestUri, string AuthScheme, string AuthValue, string Body);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<Captured> Snapshots { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Snapshots.Add(new Captured(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.Scheme ?? "",
                request.Headers.Authorization?.Parameter ?? "",
                body));
            return Respond(request);
        }
    }

    [Fact]
    public async Task Posts_To_Audio_Speech_Endpoint_With_Bearer_Auth()
    {
        var pcmResponse = new byte[] { 0xC1, 0xC2, 0xC3 };
        var handler = new CapturingHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pcmResponse) },
        };

        var engine = new MistralTextToSpeechEngine(MakeOptions(), new HttpClient(handler));
        await engine.StartAsync(default);

        var chunks = new List<byte[]>();
        await foreach (var chunk in engine.SynthesizeAsync("bonjour", default))
            chunks.Add(chunk);

        var snap = Assert.Single(handler.Snapshots);
        Assert.Equal(HttpMethod.Post, snap.Method);
        Assert.EndsWith("/audio/speech", snap.RequestUri.ToString());
        Assert.Equal("Bearer", snap.AuthScheme);
        Assert.Equal("mistral-test", snap.AuthValue);

        using var doc = JsonDocument.Parse(snap.Body);
        Assert.Equal("voxtral-tts", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("alloy", doc.RootElement.GetProperty("voice").GetString());
        Assert.Equal("bonjour", doc.RootElement.GetProperty("input").GetString());
        Assert.Equal("pcm", doc.RootElement.GetProperty("response_format").GetString());

        Assert.Equal(pcmResponse, chunks.SelectMany(c => c).ToArray());
    }
}
