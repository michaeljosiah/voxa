using System.Net;
using Voxa.Speech.OpenAI;

namespace Voxa.Speech.OpenAI.Tests;

public class OpenAIWhisperEngineTests
{
    private static OpenAISpeechOptions MakeOptions() => new()
    {
        ApiKey = "sk-test",
        SttModel = "whisper-1",
        SttBufferSeconds = 0.05,    // small for fast test flushing
        InputSampleRate = 16000,
    };

    [Fact]
    public async Task Posts_Buffered_Audio_To_Transcriptions_Endpoint_As_Multipart()
    {
        var handler = new MockHttpMessageHandler
        {
            Respond = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"text\":\"hello world\"}", System.Text.Encoding.UTF8, "application/json"),
            },
        };

        var engine = new OpenAIWhisperEngine(MakeOptions(), new HttpClient(handler));
        await engine.StartAsync(default);

        // Write enough audio that the buffered flush will trigger.
        var pcm = new byte[16000 * 2 / 5]; // 0.2s of 16kHz 16-bit
        await engine.WriteAudioAsync(pcm, default);

        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        TranscriptionResult? result = null;
        await foreach (var t in engine.ReadTranscriptsAsync(ct.Token))
        {
            result = t;
            break;
        }

        await engine.StopAsync();

        Assert.NotNull(result);
        Assert.Equal("hello world", result!.Text);
        Assert.True(result.IsFinal);

        Assert.NotEmpty(handler.Captured);
        var captured = handler.Captured[0];
        Assert.Equal(HttpMethod.Post, captured.Method);
        Assert.EndsWith("/audio/transcriptions", captured.RequestUri.ToString());
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal(typeof(MultipartFormDataContent), captured.ContentType);
    }
}
