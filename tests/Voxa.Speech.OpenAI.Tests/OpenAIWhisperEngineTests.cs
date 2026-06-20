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

    [Fact]
    public async Task FlushAsync_Sends_Single_Whisper_Request_For_Buffered_Audio()
    {
        // The contract upstream depends on: when SpeechToTextProcessor sees UserStoppedSpeakingFrame
        // it calls FlushAsync(), which must send EXACTLY ONE Whisper request for everything buffered
        // since the last flush. No mid-utterance fragmentation.
        var handler = new MockHttpMessageHandler
        {
            Respond = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"text\":\"hello world\"}", System.Text.Encoding.UTF8, "application/json"),
            },
        };

        var options = new OpenAISpeechOptions
        {
            ApiKey = "sk-test",
            SttModel = "whisper-1",
            SttBufferSeconds = 30.0,    // long enough that the safety timer never fires in the test
            InputSampleRate = 16000,
        };
        var engine = new OpenAIWhisperEngine(options, new HttpClient(handler));
        await engine.StartAsync(default);

        // Simulate VAD-gated audio arriving over a few hundred ms — multiple WriteAudioAsync calls.
        var pcm = new byte[16000 * 2 / 5]; // 0.2s
        await engine.WriteAudioAsync(pcm, default);
        await engine.WriteAudioAsync(pcm, default);
        await engine.WriteAudioAsync(pcm, default);

        // Give the periodic timer a few ticks worth of wall time. With the new behaviour it should
        // see buffer < threshold (we have 0.6s buffered, threshold is 30s) and skip every tick.
        await Task.Delay(150);
        Assert.Empty(handler.Captured);

        // Now upstream fires UserStoppedSpeakingFrame -> FlushAsync().
        await engine.FlushAsync();

        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        TranscriptionResult? result = null;
        await foreach (var t in engine.ReadTranscriptsAsync(ct.Token))
        {
            result = t;
            break;
        }
        await engine.StopAsync();

        Assert.NotNull(result);
        Assert.Equal("hello world", result!.Text);
        // Most important assertion: ONE request, not three.
        Assert.Single(handler.Captured);
    }

    [Fact]
    public async Task Periodic_Flush_Acts_As_Backstop_For_Runaway_Buffers()
    {
        // If VAD never fires for some reason, we don't want the buffer to grow unbounded.
        // The timer kicks in once the buffer exceeds SttBufferSeconds worth of audio.
        var handler = new MockHttpMessageHandler
        {
            Respond = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"text\":\"backstop fired\"}", System.Text.Encoding.UTF8, "application/json"),
            },
        };

        var options = new OpenAISpeechOptions
        {
            ApiKey = "sk-test",
            SttModel = "whisper-1",
            SttBufferSeconds = 0.1,    // tiny backstop so the test doesn't sit around
            InputSampleRate = 16000,
        };
        var engine = new OpenAIWhisperEngine(options, new HttpClient(handler));
        await engine.StartAsync(default);

        // Buffer enough to exceed the 0.1s threshold (0.1s @ 16kHz @ 16-bit = 3200 bytes).
        var pcm = new byte[16000];   // 0.5s — well above the threshold
        await engine.WriteAudioAsync(pcm, default);

        var ct = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        TranscriptionResult? result = null;
        await foreach (var t in engine.ReadTranscriptsAsync(ct.Token))
        {
            result = t;
            break;
        }
        await engine.StopAsync();

        Assert.NotNull(result);
        Assert.Equal("backstop fired", result!.Text);
    }

    /// <summary>Blocks in SendAsync until its cancellation token fires — lets the test observe whether the
    /// engine threads a real (session) token into the HTTP call.</summary>
    private sealed class BlockingHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool WasCancelled;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { WasCancelled = true; throw; }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Flush_Cancels_The_In_Flight_Request_When_The_Session_Is_Torn_Down()
    {
        // CQ-008: an aborted session must cancel the in-flight transcription, not block up to HttpClient.Timeout
        // (~100 s). The engine threads its session token (linked to the StartAsync ct) into SendAsync.
        var handler = new BlockingHandler();
        var options = new OpenAISpeechOptions
        {
            ApiKey = "sk-test", SttModel = "whisper-1",
            SttBufferSeconds = 30.0, // backstop timer won't fire during the test
            InputSampleRate = 16000,
        };
        await using var engine = new OpenAIWhisperEngine(options, new HttpClient(handler));
        using var session = new CancellationTokenSource();
        await engine.StartAsync(session.Token);
        await engine.WriteAudioAsync(new byte[16000], default);

        var flush = engine.FlushAsync();                                  // in-flight; handler blocks on its ct
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));    // the request reached SendAsync

        session.Cancel();                                                 // simulate pipeline teardown

        // Must complete promptly (cancelled) instead of hanging on the ~100 s HttpClient timeout.
        await flush.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(handler.WasCancelled);
    }
}
