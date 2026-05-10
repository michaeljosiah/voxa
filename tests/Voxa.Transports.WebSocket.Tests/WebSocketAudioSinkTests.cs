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

    [Fact]
    public async Task StatusFrame_Sent_As_Status_Envelope()
    {
        var (runner, ws, pipeline) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new StatusFrame("Checking your spending..."));

            var text = await ws.WaitForSentTextAsync(s => s.Contains("\"status\""), SendTimeout);
            Assert.NotNull(text);
            using var doc = JsonDocument.Parse(text!);
            Assert.Equal("status", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal("Checking your spending...", doc.RootElement.GetProperty("message").GetString());
        }
    }

    // ── Custom serializer hook (host-supplied frame types) ─────────────────

    /// <summary>
    /// Host-defined frame type — example of what AONIK does for its <c>ThreadReadyFrame</c>.
    /// Voxa.Core knows nothing about it; the sink learns to serialize it via a custom hook.
    /// </summary>
    private sealed record HostThreadReadyFrame(string ChatThreadId, bool IsNew) : DataFrame;

    private static (PipelineRunner Runner, FakeWebSocket Ws, Pipeline Pipeline) BuildWithCustomSerializer(
        Func<Frame, string?> customSerializer)
    {
        var ws = new FakeWebSocket();
        var sink = new WebSocketAudioSink(ws, customSerializer);
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Sink(sink);
        return (new PipelineRunner(pipeline), ws, pipeline);
    }

    [Fact]
    public async Task Custom_Serializer_Sends_Host_Frame_As_Text_Envelope()
    {
        var (runner, ws, pipeline) = BuildWithCustomSerializer(frame =>
            frame is HostThreadReadyFrame ready
                ? JsonSerializer.Serialize(new
                {
                    type = "threadReady",
                    chatThreadId = ready.ChatThreadId,
                    isNew = ready.IsNew,
                })
                : null);

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new HostThreadReadyFrame("abc123", IsNew: true));

            var text = await ws.WaitForSentTextAsync(s => s.Contains("threadReady"), SendTimeout);
            Assert.NotNull(text);

            using var doc = JsonDocument.Parse(text!);
            Assert.Equal("threadReady", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal("abc123", doc.RootElement.GetProperty("chatThreadId").GetString());
            Assert.True(doc.RootElement.GetProperty("isNew").GetBoolean());
        }
    }

    [Fact]
    public async Task Custom_Serializer_Returning_Null_Falls_Through_To_Builtin()
    {
        // Verify Voxa's built-in frame switch still handles standard frames when the host's
        // serializer doesn't claim them.
        var (runner, ws, pipeline) = BuildWithCustomSerializer(_ => null);

        await using (runner)
        {
            await runner.StartAsync();
            await pipeline.Source.IngestAsync(new TextFrame("standard text"));

            var text = await ws.WaitForSentTextAsync(s => s.Contains("\"text\""), SendTimeout);
            Assert.NotNull(text);
            using var doc = JsonDocument.Parse(text!);
            Assert.Equal("text", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal("standard text", doc.RootElement.GetProperty("text").GetString());
        }
    }

    [Fact]
    public async Task Custom_Serializer_Interleaved_With_Builtin_Frames_All_Arrive()
    {
        // Smoke test: interleave host frames and standard frames; verify all arrive (proves the
        // hook flows through the same _sendLock as the built-in path).
        var (runner, ws, pipeline) = BuildWithCustomSerializer(frame =>
            frame is HostThreadReadyFrame ready
                ? $"{{\"type\":\"threadReady\",\"chatThreadId\":\"{ready.ChatThreadId}\",\"isNew\":{(ready.IsNew ? "true" : "false")}}}"
                : null);

        await using (runner)
        {
            await runner.StartAsync();
            for (int i = 0; i < 10; i++)
            {
                await pipeline.Source.IngestAsync(new HostThreadReadyFrame($"thread-{i}", i % 2 == 0));
                await pipeline.Source.IngestAsync(new TextFrame($"chunk-{i}"));
            }

            var lastHost = await ws.WaitForSentTextAsync(s => s.Contains("thread-9"), SendTimeout);
            Assert.NotNull(lastHost);

            var allText = ws.SentTextAsString;
            Assert.Equal(20, allText.Count(s => s.Contains("threadReady") || s.Contains("\"text\"")));
        }
    }
}
