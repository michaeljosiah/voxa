using System.Net.WebSockets;
using System.Text.Json;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;

namespace Voxa.Transports.WebSocket.Tests;

public class WebSocketAudioSinkTests
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(3);

    private static (PipelineRunner Runner, FakeWebSocket Ws, Pipeline Pipeline) Build()
    {
        var ws = new FakeWebSocket();
        var sink = new WebSocketAudioSink(ws);
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Sink(sink);
        return (new PipelineRunner(pipeline), ws, pipeline);
    }

    [Fact]
    public async Task AudioRawFrame_Sent_As_Binary()
    {
        var (runner, ws, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();

            var pcm = new byte[] { 0x10, 0x20, 0x30, 0x40 };
            await pipeline.Source.IngestAsync(new AudioRawFrame(pcm, 24000, 1));

            var binary = await ws.WaitForSentBinaryAsync(SendTimeout);
            Assert.NotNull(binary);
            Assert.Equal(pcm, binary);
        }
    }

    [Fact]
    public async Task LlmTextChunkFrame_Sent_As_Text_Json()
    {
        var (runner, ws, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();

            await pipeline.Source.IngestAsync(new LlmTextChunkFrame("Hello"));

            var text = await ws.WaitForSentTextAsync(s => s.Contains("\"text\""), SendTimeout);
            Assert.NotNull(text);
            using var doc = JsonDocument.Parse(text!);
            Assert.Equal("text", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal("Hello", doc.RootElement.GetProperty("text").GetString());
        }
    }

    [Fact]
    public async Task ToolCallRequestFrame_Sent_As_ToolCall_Envelope()
    {
        var (runner, ws, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();

            await pipeline.Source.IngestAsync(new ToolCallRequestFrame("c1", "ping", "{\"x\":1}"));

            var text = await ws.WaitForSentTextAsync(s => s.Contains("toolCall"), SendTimeout);
            Assert.NotNull(text);
            using var doc = JsonDocument.Parse(text!);
            Assert.Equal("c1", doc.RootElement.GetProperty("callId").GetString());
            Assert.Equal("ping", doc.RootElement.GetProperty("name").GetString());
        }
    }

    [Fact]
    public async Task BotStartedSpeakingFrame_Sent_As_Speaking_Envelope()
    {
        var (runner, ws, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();

            await pipeline.Source.IngestAsync(new BotStartedSpeakingFrame());

            var text = await ws.WaitForSentTextAsync(s => s.Contains("speaking"), SendTimeout);
            Assert.NotNull(text);
            using var doc = JsonDocument.Parse(text!);
            Assert.Equal("bot", doc.RootElement.GetProperty("who").GetString());
            Assert.True(doc.RootElement.GetProperty("started").GetBoolean());
        }
    }

    [Fact]
    public async Task EndFrame_Sent_As_End_Envelope_And_Completes_Runner()
    {
        var (runner, ws, _) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await runner.StopAsync(TimeSpan.FromSeconds(2));

            var text = await ws.WaitForSentTextAsync(s => s.Contains("\"end\""), SendTimeout);
            Assert.NotNull(text);
            await runner.WaitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }
}
