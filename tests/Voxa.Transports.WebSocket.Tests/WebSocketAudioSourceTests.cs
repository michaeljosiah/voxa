using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Testing.Processors;

namespace Voxa.Transports.WebSocket.Tests;

public class WebSocketAudioSourceTests
{
    private static (PipelineRunner Runner, FakeWebSocket Ws, CapturingProcessor Captured) Build()
    {
        var ws = new FakeWebSocket();
        var source = new WebSocketAudioSource(ws);
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(source)
            .Then(captured)
            .Sink(new PipelineSink());
        return (new PipelineRunner(pipeline), ws, captured);
    }

    [Fact]
    public async Task Binary_Frame_Becomes_AudioRawFrame()
    {
        var (runner, ws, captured) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            var pcm = new byte[] { 0x01, 0x02, 0x03, 0x04 };
            await ws.QueueIncomingBinaryAsync(pcm);

            await captured.WaitForAsync(2, TimeSpan.FromSeconds(2));
            var audio = captured.Captured.OfType<AudioRawFrame>().FirstOrDefault();
            Assert.NotNull(audio);
            Assert.Equal(pcm, audio!.Pcm.ToArray());
            Assert.Equal(24000, audio.SampleRate);
            Assert.Equal(1, audio.Channels);
        }
    }

    [Fact]
    public async Task Text_End_Becomes_EndFrame()
    {
        var (runner, ws, _) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await ws.QueueIncomingTextAsync("{\"type\":\"end\"}");

            await runner.WaitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Text_ToolResult_Becomes_ToolCallResultFrame()
    {
        var (runner, ws, captured) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await ws.QueueIncomingTextAsync(
                "{\"type\":\"toolResult\",\"callId\":\"c1\",\"resultJson\":\"{\\\"ok\\\":true}\"}");

            await captured.WaitForAsync(2, TimeSpan.FromSeconds(2));
            var tool = captured.Captured.OfType<ToolCallResultFrame>().FirstOrDefault();
            Assert.NotNull(tool);
            Assert.Equal("c1", tool!.CallId);
        }
    }

    [Fact]
    public async Task Unknown_Text_Type_Is_Dropped_Without_Failing()
    {
        var (runner, ws, captured) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            await ws.QueueIncomingTextAsync("{\"type\":\"unknown\"}");
            await Task.Delay(60);

            // Only the StartFrame should be present; the unknown text was dropped.
            Assert.DoesNotContain(captured.Captured, f => f is TextFrame);
            Assert.DoesNotContain(captured.Captured, f => f is ErrorFrame);
        }
    }
}
