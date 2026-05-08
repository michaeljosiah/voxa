using System.Text.Json;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Testing.Processors;

namespace Voxa.Services.AzureVoiceLive.Tests;

public class AzureVoiceLiveProcessorTests
{
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
            await Task.Delay(80);

            Assert.NotEmpty(h.Transport.SentEvents);
            var first = h.Transport.SentEvents[0];
            using var doc = JsonDocument.Parse(first);
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
            await Task.Delay(50);

            var pcm = new byte[] { 0x01, 0x02, 0x03, 0x04 };
            await h.Pipeline.Source.IngestAsync(new AudioRawFrame(pcm, 24000, 1));

            await Task.Delay(80);
            var appendEvent = h.Transport.SentEvents.FirstOrDefault(e => e.Contains("input_audio_buffer.append"));
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
            await Task.Delay(50);

            var pcm = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50, 0x60 };
            var b64 = Convert.ToBase64String(pcm);
            var evt = $"{{\"type\":\"response.audio.delta\",\"delta\":\"{b64}\"}}";
            await h.Transport.QueueServerEventAsync(evt);

            await h.Captured.WaitForAsync(2, TimeSpan.FromSeconds(2));
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
            await Task.Delay(50);

            // Bot starts speaking → BotStartedSpeakingFrame
            await h.Transport.QueueServerEventAsync("{\"type\":\"response.created\",\"response\":{\"id\":\"r1\"}}");
            await h.Captured.WaitForAsync(2, TimeSpan.FromSeconds(2));
            await Task.Delay(40);

            // User speaks over the bot → User start + Interruption
            await h.Transport.QueueServerEventAsync("{\"type\":\"input_audio_buffer.speech_started\"}");
            await Task.Delay(120);

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
            await Task.Delay(50);

            var evt = "{\"type\":\"response.function_call_arguments.done\"," +
                      "\"call_id\":\"call_abc\"," +
                      "\"name\":\"get_weather\"," +
                      "\"arguments\":\"{\\\"city\\\":\\\"Lagos\\\"}\"}";
            await h.Transport.QueueServerEventAsync(evt);

            await h.Captured.WaitForAsync(2, TimeSpan.FromSeconds(2));
            var call = h.Captured.Captured.OfType<ToolCallRequestFrame>().FirstOrDefault();
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
            await Task.Delay(50);

            await h.Transport.QueueServerEventAsync(
                "{\"type\":\"error\",\"error\":{\"message\":\"upstream blew up\",\"code\":\"x\"}}");

            var ex = await Assert.ThrowsAsync<PipelineFailedException>(
                async () => await h.Runner.WaitAsync().WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Contains("upstream blew up", ex.Message);
        }
    }

    [Fact]
    public async Task ToolCallResultFrame_Sends_Output_Then_ResponseCreate()
    {
        var h = BuildPipeline();
        await using (h.Runner)
        {
            await h.Runner.StartAsync();
            await Task.Delay(50);

            await h.Pipeline.Source.IngestAsync(new ToolCallResultFrame("call_abc", "{\"temp\":\"30\"}"));
            await Task.Delay(100);

            var sent = h.Transport.SentEvents;
            Assert.Contains(sent, e => e.Contains("conversation.item.create") && e.Contains("call_abc"));
            Assert.Contains(sent, e => e.Contains("\"response.create\""));
        }
    }
}
