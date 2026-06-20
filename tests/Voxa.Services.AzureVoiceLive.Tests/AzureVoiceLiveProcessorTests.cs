using System.Text.Json;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Testing.Processors;

namespace Voxa.Services.AzureVoiceLive.Tests;

public class AzureVoiceLiveProcessorTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(3);

    private static AzureVoiceLiveOptions MakeOptions() => new()
    {
        Endpoint = new Uri("wss://test.example.com/voice-live"),
        ApiKey = "test-key",
        Model = "gpt-realtime-mini",
        Voice = "alloy",
        Instructions = "Be helpful and brief.",
    };

    private sealed record Harness(
        PipelineRunner Runner,
        ScriptedRealtimeApiTransport Transport,
        CapturingProcessor Captured,
        Pipeline Pipeline);

    private static Harness BuildPipeline(AzureVoiceLiveOptions? options = null)
    {
        var transport = new ScriptedRealtimeApiTransport();
        var processor = new AzureVoiceLiveProcessor(options ?? MakeOptions(), () => transport);
        var captured = new CapturingProcessor("after-voice-live");

        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(processor)
            .Then(captured)
            .Sink(new PipelineSink());

        var runner = new PipelineRunner(pipeline);
        return new Harness(runner, transport, captured, pipeline);
    }

    [Fact]
    public async Task Sends_SessionUpdate_On_Connect()
    {
        var h = BuildPipeline();
        await using (h.Runner)
        {
            await h.Runner.StartAsync();

            var sessionUpdate = await h.Transport.WaitForSentEventAsync(s => s.Contains("session.update"), WaitTimeout);
            Assert.NotNull(sessionUpdate);

            using var doc = JsonDocument.Parse(sessionUpdate!);
            Assert.Equal("session.update", doc.RootElement.GetProperty("type").GetString());
            var session = doc.RootElement.GetProperty("session");
            Assert.Equal("Be helpful and brief.", session.GetProperty("instructions").GetString());
            Assert.Equal("alloy", session.GetProperty("voice").GetString());
            Assert.Equal("server_vad", session.GetProperty("turn_detection").GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task Forwards_AudioRawFrame_As_Buffer_Append()
    {
        var h = BuildPipeline();
        await using (h.Runner)
        {
            await h.Runner.StartAsync();
            await h.Transport.WaitForSentEventAsync(s => s.Contains("session.update"), WaitTimeout);

            var pcm = new byte[] { 0x01, 0x02, 0x03, 0x04 };
            await h.Pipeline.Source.IngestAsync(new AudioRawFrame(pcm, 24000, 1));

            var appendEvent = await h.Transport.WaitForSentEventAsync(s => s.Contains("input_audio_buffer.append"), WaitTimeout);
            Assert.NotNull(appendEvent);
            using var doc = JsonDocument.Parse(appendEvent!);
            var b64 = doc.RootElement.GetProperty("audio").GetString();
            Assert.Equal(Convert.ToBase64String(pcm), b64);
        }
    }

    [Fact]
    public async Task Translates_Server_Audio_Delta_To_AudioRawFrame()
    {
        var h = BuildPipeline();
        await using (h.Runner)
        {
            await h.Runner.StartAsync();
            await h.Transport.WaitForSentEventAsync(s => s.Contains("session.update"), WaitTimeout);

            var pcm = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50, 0x60 };
            var b64 = Convert.ToBase64String(pcm);
            await h.Transport.QueueServerEventAsync($"{{\"type\":\"response.audio.delta\",\"delta\":\"{b64}\"}}");

            await h.Captured.WaitForAsync(2, WaitTimeout);
            var audio = h.Captured.Captured.OfType<AudioRawFrame>().FirstOrDefault();
            Assert.NotNull(audio);
            Assert.Equal(pcm, audio!.Pcm.ToArray());
            Assert.Equal(24000, audio.SampleRate);
        }
    }

    [Fact]
    public async Task Speech_Started_While_Bot_Speaking_Emits_InterruptionFrame()
    {
        var h = BuildPipeline();
        await using (h.Runner)
        {
            await h.Runner.StartAsync();
            await h.Transport.WaitForSentEventAsync(s => s.Contains("session.update"), WaitTimeout);

            // Bot starts speaking → BotStartedSpeakingFrame
            await h.Transport.QueueServerEventAsync("{\"type\":\"response.created\",\"response\":{\"id\":\"r1\"}}");
            await WaitForCapturedAsync<BotStartedSpeakingFrame>(h.Captured, WaitTimeout);

            // User speaks over the bot → User start + Interruption
            await h.Transport.QueueServerEventAsync("{\"type\":\"input_audio_buffer.speech_started\"}");
            await WaitForCapturedAsync<InterruptionFrame>(h.Captured, WaitTimeout);

            Assert.Contains(h.Captured.Captured, f => f is BotStartedSpeakingFrame);
            Assert.Contains(h.Captured.Captured, f => f is UserStartedSpeakingFrame);
            Assert.Contains(h.Captured.Captured, f => f is InterruptionFrame);
        }
    }

    [Fact]
    public async Task Translates_Function_Call_Arguments_Done_To_ToolCallRequestFrame()
    {
        var h = BuildPipeline();
        await using (h.Runner)
        {
            await h.Runner.StartAsync();
            await h.Transport.WaitForSentEventAsync(s => s.Contains("session.update"), WaitTimeout);

            var evt = "{\"type\":\"response.function_call_arguments.done\"," +
                      "\"call_id\":\"call_abc\"," +
                      "\"name\":\"get_weather\"," +
                      "\"arguments\":\"{\\\"city\\\":\\\"Lagos\\\"}\"}";
            await h.Transport.QueueServerEventAsync(evt);

            var call = await WaitForCapturedAsync<ToolCallRequestFrame>(h.Captured, WaitTimeout);
            Assert.NotNull(call);
            Assert.Equal("call_abc", call!.CallId);
            Assert.Equal("get_weather", call.Name);
            Assert.Contains("Lagos", call.ArgumentsJson);
        }
    }

    [Fact]
    public async Task Server_Error_Surfaces_As_Pipeline_Failed_Exception()
    {
        var h = BuildPipeline();
        await using (h.Runner)
        {
            await h.Runner.StartAsync();
            await h.Transport.WaitForSentEventAsync(s => s.Contains("session.update"), WaitTimeout);

            await h.Transport.QueueServerEventAsync(
                "{\"type\":\"error\",\"error\":{\"message\":\"upstream blew up\",\"code\":\"x\"}}");

            var ex = await Assert.ThrowsAsync<PipelineFailedException>(
                async () => await h.Runner.WaitAsync().WaitAsync(WaitTimeout));
            Assert.Contains("upstream blew up", ex.Message);
        }
    }

    [Fact]
    public async Task ToolCallResultFrame_Sends_Output_Then_ResponseCreate()
    {
        var transport = new ScriptedRealtimeApiTransport();
        var processor = new AzureVoiceLiveProcessor(MakeOptions(), () => transport);

        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(processor)
            .Sink(new PipelineSink());

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();
        await transport.WaitForSentEventAsync(s => s.Contains("session.update"), WaitTimeout);

        await pipeline.Source.IngestAsync(new ToolCallResultFrame("call_abc", "{\"temp\":\"30\"}"));

        var output = await transport.WaitForSentEventAsync(s => s.Contains("conversation.item.create") && s.Contains("call_abc"), WaitTimeout);
        Assert.NotNull(output);
        var responseCreate = await transport.WaitForSentEventAsync(s => s.Contains("\"response.create\""), WaitTimeout);
        Assert.NotNull(responseCreate);
    }

    [Fact]
    public async Task Disposing_Without_An_EndFrame_Releases_The_Transport()
    {
        // CQ-002: a client disconnect disposes the runner WITHOUT an EndFrame. The transport (which owns a
        // ClientWebSocket + SemaphoreSlim) must still be released — via DisposeAsyncCore, not only OnEndAsync.
        var h = BuildPipeline();
        await h.Runner.StartAsync();
        await h.Transport.WaitForSentEventAsync(s => s.Contains("session.update"), WaitTimeout); // connected

        await h.Runner.DisposeAsync(); // abrupt: no EndFrame is ever injected

        Assert.True(h.Transport.DisposeCount >= 1); // transport released on disposal, not leaked
        Assert.False(h.Transport.Connected);
    }

    private static async Task<T?> WaitForCapturedAsync<T>(CapturingProcessor captured, TimeSpan timeout) where T : Frame
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var match = captured.Captured.OfType<T>().FirstOrDefault();
            if (match is not null) return match;
            await Task.Delay(10);
        }
        return null;
    }
}
