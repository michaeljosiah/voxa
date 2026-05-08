using System.Net.WebSockets;
using System.Text.Json;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;

namespace Voxa.Transports.WebSocket.Tests;

public class WebSocketAudioSinkTests
{
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
            await Task.Delay(80);

            var binary = ws.Sent.FirstOrDefault(s => s.Type == WebSocketMessageType.Binary);
            Assert.NotEqual(default, binary);
            Assert.Equal(pcm, binary.Data);
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
            await Task.Delay(80);

            var text = ws.SentTextAsString.FirstOrDefault(s => s.Contains("\"text\""));
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
            await Task.Delay(80);

            var text = ws.SentTextAsString.FirstOrDefault(s => s.Contains("toolCall"));
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
            await Task.Delay(80);

            var text = ws.SentTextAsString.FirstOrDefault(s => s.Contains("speaking"));
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

            var text = ws.SentTextAsString.FirstOrDefault(s => s.Contains("\"end\""));
            Assert.NotNull(text);
            await runner.WaitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }
}
