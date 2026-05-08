using System.Net;
using System.Text.Json;
using Voxa.Speech.OpenAI;

namespace Voxa.Speech.OpenAI.Tests;

public class OpenAITextToSpeechEngineTests
{
    private static OpenAISpeechOptions MakeOptions() => new()
    {
        ApiKey = "sk-test",
        TtsModel = "tts-1",
        TtsVoice = "alloy",
    };

    [Fact]
    public async Task Posts_To_Audio_Speech_Endpoint_With_Bearer_Auth()
    {
        var pcmResponse = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50 };
        var handler = new MockHttpMessageHandler
        {
            Respond = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(pcmResponse),
            },
        };

        var engine = new OpenAITextToSpeechEngine(MakeOptions(), new HttpClient(handler));
        await engine.StartAsync(default);

        var chunks = new List<byte[]>();
        await foreach (var chunk in engine.SynthesizeAsync("hello", default))
            chunks.Add(chunk);

        var captured = Assert.Single(handler.Captured);
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.EndsWith("/audio/speech", captured.RequestUri.ToString());
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal("sk-test", captured.Headers.Authorization?.Parameter);

        Assert.NotNull(captured.BodyAsString);
        using var doc = JsonDocument.Parse(captured.BodyAsString!);
        Assert.Equal("tts-1", doc.RootElement.GetProperty("model").GetString());
        Assert.Equal("alloy", doc.RootElement.GetProperty("voice").GetString());
        Assert.Equal("hello", doc.RootElement.GetProperty("input").GetString());
        Assert.Equal("pcm", doc.RootElement.GetProperty("response_format").GetString());

        Assert.Equal(pcmResponse, chunks.SelectMany(c => c).ToArray());
    }

    [Fact]
    public async Task Empty_Text_Does_Not_Hit_The_Endpoint()
    {
        var handler = new MockHttpMessageHandler();
        var engine = new OpenAITextToSpeechEngine(MakeOptions(), new HttpClient(handler));
        await engine.StartAsync(default);

        await foreach (var _ in engine.SynthesizeAsync("   ", default))
        {
        }

        Assert.Empty(handler.Captured);
    }
}
