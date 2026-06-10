using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;

namespace Voxa.Transports.WebSocket.Tests;

/// <summary>
/// Barge-in purge behavior of the queued sink (VPS-001 WS4): on an interruption, audio queued
/// before the interruption is dropped; the interruption envelope is sent; non-audio is never
/// purged; and a graceful end drains the queue.
/// </summary>
public class WebSocketAudioSinkPurgeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private static (PipelineRunner Runner, FakeWebSocket Ws, Pipeline Pipeline) Build()
    {
        var ws = new FakeWebSocket();
        var sink = new WebSocketAudioSink(ws);
        var pipeline = Pipeline.Build().Source(new PipelineSource()).Sink(sink);
        return (new PipelineRunner(pipeline), ws, pipeline);
    }

    private static AudioRawFrame Audio(byte tag) => new(new byte[] { tag, 0 }, 24000, 1);

    [Fact]
    public async Task Interruption_PurgesQueuedAudio_ButSendsInterruptionAndPostAudio()
    {
        var (runner, ws, pipeline) = Build();
        await using (runner)
        {
            ws.BlockSends();                 // park the writer so audio piles up in the queue
            await runner.StartAsync();

            // 20 stale audio chunks, tags 100..119.
            for (byte i = 0; i < 20; i++)
                await pipeline.Source.IngestAsync(Audio((byte)(100 + i)));
            await Task.Delay(60);            // let them enqueue; writer is blocked on the first send

            // Interruption travels on the system channel and bumps the epoch immediately.
            await pipeline.Source.IngestAsync(new InterruptionFrame());
            await Task.Delay(40);

            // A fresh post-interruption audio chunk (new epoch) — must survive.
            await pipeline.Source.IngestAsync(Audio(200));

            ws.ReleaseSends();               // let the writer drain

            // The interruption envelope must reach the client.
            var interruption = await ws.WaitForSentTextAsync(s => s.Contains("interruption"), Timeout);
            Assert.NotNull(interruption);

            // The post-interruption chunk must arrive...
            await ws.WaitForSentBinaryAsync(Timeout);
            await Task.Delay(80);
            var sentBinary = ws.SentBinary;

            // ...and none of the stale tags 101..119 may appear (tag 100 may already have been
            // dequeued/in-flight before the epoch bump — at most that single chunk is allowed).
            Assert.Contains(sentBinary, b => b[0] == 200);
            Assert.DoesNotContain(sentBinary, b => b[0] >= 101 && b[0] <= 119);
            Assert.True(sentBinary.Count(b => b[0] >= 100 && b[0] <= 119) <= 1,
                "more than one pre-interruption audio chunk survived the purge");
        }
    }

    [Fact]
    public async Task NonAudio_IsNeverPurged()
    {
        var (runner, ws, pipeline) = Build();
        await using (runner)
        {
            ws.BlockSends();
            await runner.StartAsync();

            await pipeline.Source.IngestAsync(new TranscriptionFrame("before interruption", IsFinal: true));
            await Task.Delay(40);
            await pipeline.Source.IngestAsync(new InterruptionFrame());
            ws.ReleaseSends();

            // The transcription queued before the interruption must still be delivered.
            var transcript = await ws.WaitForSentTextAsync(s => s.Contains("before interruption"), Timeout);
            Assert.NotNull(transcript);
        }
    }

    [Fact]
    public async Task EndFrame_AfterSocketLeftOpen_StillCompletesRunner()
    {
        var (runner, ws, _) = Build();
        await using (runner)
        {
            await runner.StartAsync();

            // Client disconnect: the socket leaves Open BEFORE the EndFrame reaches the sink.
            // Regression (VPS-001 review): the sink skipped EnqueueFrameAsync for non-Open
            // sockets, so the outbound channel was never completed and `await _writerTask`
            // deadlocked the data loop — StopAsync burned its full grace period and WaitAsync
            // ended cancelled instead of gracefully.
            ws.Abort();

            await runner.StopAsync(TimeSpan.FromSeconds(2));
            await runner.WaitAsync().WaitAsync(Timeout);   // must complete gracefully, not cancel
        }
    }

    [Fact]
    public async Task EndFrame_DrainsQueue_BeforeRunnerCompletes()
    {
        var (runner, ws, _) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await runner.StopAsync(TimeSpan.FromSeconds(2));

            var end = await ws.WaitForSentTextAsync(s => s.Contains("\"end\""), Timeout);
            Assert.NotNull(end);
            await runner.WaitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }
}
