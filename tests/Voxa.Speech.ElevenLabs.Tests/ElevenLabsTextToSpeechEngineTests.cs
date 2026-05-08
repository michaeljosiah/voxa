using System.Net;
using System.Text.Json;
using Voxa.Speech.ElevenLabs;

namespace Voxa.Speech.ElevenLabs.Tests;

public class ElevenLabsTextToSpeechEngineTests
{
    private static ElevenLabsOptions MakeOptions() => new()
    {
        ApiKey = "el-test",
        VoiceId = "21m00Tcm4TlvDq8ikWAM",
        ModelId = "eleven_multilingual_v2",
        OutputSampleRate = 24000,
    };

    private sealed record Captured(HttpMethod Method, Uri RequestUri, string XiApiKey, string Body);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<Captured> Snapshots { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var apiKey = request.Headers.TryGetValues("xi-api-key", out var v) ? string.Join(",", v) : "";
            Snapshots.Add(new Captured(request.Method, request.RequestUri!, apiKey, body));
            return Respond(request);
        }
    }

    [Fact]
    public async Task Posts_To_Voice_Specific_Stream_Endpoint_With_xi_api_key_Header()
    {
        var pcmResponse = new byte[] { 0xA1, 0xA2, 0xA3, 0xA4 };
        var handler = new CapturingHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(pcmResponse),
            },
        };

        var engine = new ElevenLabsTextToSpeechEngine(MakeOptions(), new HttpClient(handler));
        await engine.StartAsync(default);

        var chunks = new List<byte[]>();
        await foreach (var chunk in engine.SynthesizeAsync("hi there", default))
            chunks.Add(chunk);

        var snap = Assert.Single(handler.Snapshots);
        Assert.Equal(HttpMethod.Post, snap.Method);
        var url = snap.RequestUri.ToString();
        Assert.Contains("/text-to-speech/21m00Tcm4TlvDq8ikWAM/stream", url);
        Assert.Contains("output_format=pcm_24000", url);
        Assert.Equal("el-test", snap.XiApiKey);

        using var doc = JsonDocument.Parse(snap.Body);
        Assert.Equal("hi there", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal("eleven_multilingual_v2", doc.RootElement.GetProperty("model_id").GetString());

        Assert.Equal(pcmResponse, chunks.SelectMany(c => c).ToArray());
    }
}
