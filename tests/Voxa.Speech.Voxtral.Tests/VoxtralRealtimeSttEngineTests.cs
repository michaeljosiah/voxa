using Microsoft.Extensions.Logging.Abstractions;

namespace Voxa.Speech.Voxtral.Tests;

/// <summary>The streaming engine end-to-end against the in-process fake realtime server (no vLLM, GPU, or model).
/// vLLM's realtime API is one-shot per connection, so the engine buffers each utterance and transcribes it over a
/// fresh socket at speech-end: a test drives <c>OnUserStartedSpeakingAsync</c> → <c>WriteAudioAsync</c> →
/// <c>FlushAsync</c> per turn.</summary>
public class VoxtralRealtimeSttEngineTests
{
    [Fact]
    public async Task Handshakes_Streams_Audio_And_Emits_Interim_Then_Final()
    {
        await using var server = new MiniRealtimeServer(deltas: ["hello", " world"], doneText: "hello world");
        var options = new VoxtralOptions { ServerUrl = server.ServerUrl, Model = "test-model", Language = "en" };
        await using var engine = new VoxtralRealtimeSttEngine(options, NullLogger.Instance);

        await engine.StartAsync(CancellationToken.None);
        await engine.OnUserStartedSpeakingAsync();
        var pcm = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        await engine.WriteAudioAsync(pcm, CancellationToken.None);
        await engine.FlushAsync();                          // VAD speech-end → transcribe the buffered utterance

        var results = await ReadUntilFinalAsync(engine, TimeSpan.FromSeconds(10));

        Assert.Equal("test-model", server.Model);           // handshake reached the server
        Assert.Equal(pcm, server.ReceivedAudio);            // the buffered audio arrived as base64 appends
        Assert.Contains(results, r => !r.IsFinal && r.Text == "hello"); // running deltas → interims
        var final = Assert.Single(results, r => r.IsFinal);
        Assert.Equal("hello world", final.Text);            // done → one final
        Assert.Equal("en", final.Language);

        await engine.StopAsync();
    }

    [Fact] // buffered audio is sent in order with nothing dropped — no preroll loss, no tail truncation
    public async Task All_Buffered_Audio_Reaches_The_Server_In_Order()
    {
        await using var server = new MiniRealtimeServer(deltas: ["ok"], doneText: "ok");
        var options = new VoxtralOptions { ServerUrl = server.ServerUrl, Model = "test-model" };
        await using var engine = new VoxtralRealtimeSttEngine(options, NullLogger.Instance);
        await engine.StartAsync(CancellationToken.None);

        await engine.OnUserStartedSpeakingAsync();
        await engine.WriteAudioAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);   // "preroll"
        await engine.WriteAudioAsync(new byte[] { 4, 5, 6 }, CancellationToken.None);
        await engine.WriteAudioAsync(new byte[] { 7, 8, 9 }, CancellationToken.None);   // "tail"
        await engine.FlushAsync();
        await ReadUntilFinalAsync(engine, TimeSpan.FromSeconds(10));

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, server.ReceivedAudio);

        await engine.StopAsync();
    }

    [Fact] // audio buffered outside an utterance (no speech-start) is discarded — only bracketed speech is sent
    public async Task Audio_Outside_An_Utterance_Is_Not_Sent()
    {
        await using var server = new MiniRealtimeServer(deltas: ["x"], doneText: "x");
        var options = new VoxtralOptions { ServerUrl = server.ServerUrl, Model = "test-model" };
        await using var engine = new VoxtralRealtimeSttEngine(options, NullLogger.Instance);
        await engine.StartAsync(CancellationToken.None);

        await engine.WriteAudioAsync(new byte[] { 99 }, CancellationToken.None); // before any speech-start → dropped
        await engine.OnUserStartedSpeakingAsync();
        await engine.WriteAudioAsync(new byte[] { 1, 2 }, CancellationToken.None);
        await engine.FlushAsync();
        await ReadUntilFinalAsync(engine, TimeSpan.FromSeconds(10));

        Assert.Equal(new byte[] { 1, 2 }, server.ReceivedAudio); // the pre-speech byte never reached the server

        await engine.StopAsync();
    }

    [Fact] // codex P2: the configured language hint and realtime delay must reach the server, not be silently dropped
    public async Task Handshake_Sends_The_Configured_Language_And_Delay()
    {
        await using var server = new MiniRealtimeServer(deltas: ["hi"], doneText: "hi");
        var options = new VoxtralOptions { ServerUrl = server.ServerUrl, Model = "test-model", Language = "fr", DelayMs = 320 };
        await using var engine = new VoxtralRealtimeSttEngine(options, NullLogger.Instance);

        await engine.StartAsync(CancellationToken.None);
        await engine.OnUserStartedSpeakingAsync();
        await engine.WriteAudioAsync(new byte[] { 1, 2 }, CancellationToken.None);
        await engine.FlushAsync();
        await ReadUntilFinalAsync(engine, TimeSpan.FromSeconds(10));

        Assert.Equal("test-model", server.Model);
        Assert.Equal("fr", server.Language);
        Assert.Equal(320, server.Delay);

        await engine.StopAsync();
    }

    [Fact] // each utterance is its own one-shot connection — every turn finalizes
    public async Task Each_Utterance_Reconnects_And_Finalizes()
    {
        await using var server = new MiniRealtimeServer(deltas: ["hi"], doneText: "hi there");
        var options = new VoxtralOptions { ServerUrl = server.ServerUrl, Model = "test-model" };
        await using var engine = new VoxtralRealtimeSttEngine(options, NullLogger.Instance);
        await engine.StartAsync(CancellationToken.None);

        for (var utterance = 0; utterance < 3; utterance++)
        {
            await engine.OnUserStartedSpeakingAsync();
            await engine.WriteAudioAsync(new byte[] { 1, 2 }, CancellationToken.None);
            await engine.FlushAsync();
            var results = await ReadUntilFinalAsync(engine, TimeSpan.FromSeconds(10));
            Assert.Contains(results, r => r.IsFinal && r.Text == "hi there"); // every utterance finalizes
        }

        await engine.StopAsync();
    }

    [Fact] // a slow (in-flight) done is still delivered — FlushAsync awaits the utterance's final
    public async Task A_Delayed_Done_Is_Still_Delivered()
    {
        await using var server = new MiniRealtimeServer(deltas: ["c"], doneText: "committed", doneDelayMs: 150);
        var options = new VoxtralOptions { ServerUrl = server.ServerUrl, Model = "test-model" };
        await using var engine = new VoxtralRealtimeSttEngine(options, NullLogger.Instance);
        await engine.StartAsync(CancellationToken.None);

        await engine.OnUserStartedSpeakingAsync();
        await engine.WriteAudioAsync(new byte[] { 1, 2 }, CancellationToken.None);
        await engine.FlushAsync();

        var results = await ReadUntilFinalAsync(engine, TimeSpan.FromSeconds(10));
        Assert.Contains(results, r => r.IsFinal && r.Text == "committed");

        await engine.StopAsync();
    }

    [Fact] // an abrupt stop with un-flushed audio (disconnect mid-utterance) still transcribes the tail
    public async Task StopAsync_Transcribes_A_Tail_Utterance_That_Was_Not_Flushed()
    {
        await using var server = new MiniRealtimeServer(deltas: ["tail"], doneText: "tail end");
        var options = new VoxtralOptions { ServerUrl = server.ServerUrl, Model = "test-model" };
        await using var engine = new VoxtralRealtimeSttEngine(options, NullLogger.Instance);
        await engine.StartAsync(CancellationToken.None);

        await engine.OnUserStartedSpeakingAsync();
        await engine.WriteAudioAsync(new byte[] { 1, 2 }, CancellationToken.None); // audio, but NO FlushAsync
        await engine.StopAsync();   // transcribes the tail before completing

        Assert.Contains(await DrainFinalsAsync(engine), t => t.Text == "tail end");
    }

    [Fact] // back-to-back utterances each deliver their final, even when dones lag
    public async Task Back_To_Back_Utterances_Both_Finalize_When_Dones_Lag()
    {
        await using var server = new MiniRealtimeServer(deltas: ["u"], doneText: "utterance", doneDelayMs: 120);
        var options = new VoxtralOptions { ServerUrl = server.ServerUrl, Model = "test-model" };
        await using var engine = new VoxtralRealtimeSttEngine(options, NullLogger.Instance);
        await engine.StartAsync(CancellationToken.None);

        await engine.OnUserStartedSpeakingAsync();
        await engine.WriteAudioAsync(new byte[] { 1, 2 }, CancellationToken.None);
        await engine.FlushAsync();
        await engine.OnUserStartedSpeakingAsync();
        await engine.WriteAudioAsync(new byte[] { 3, 4 }, CancellationToken.None);
        await engine.FlushAsync();
        await engine.StopAsync();

        Assert.Equal(2, (await DrainFinalsAsync(engine)).Count(t => t.Text == "utterance"));
    }

    [Fact] // codex P2: a server error frame must fault the transcript stream, not hang Talk with no error
    public async Task Server_Error_Frame_Faults_The_Transcript_Stream()
    {
        await using var server = new MiniRealtimeServer(deltas: [], doneText: "", error: "bad model name");
        var options = new VoxtralOptions { ServerUrl = server.ServerUrl, Model = "does-not-exist" };
        await using var engine = new VoxtralRealtimeSttEngine(options, NullLogger.Instance);
        await engine.StartAsync(CancellationToken.None);

        await engine.OnUserStartedSpeakingAsync();
        await engine.WriteAudioAsync(new byte[] { 1, 2 }, CancellationToken.None);
        await engine.FlushAsync(); // connects, the server rejects the session

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var ex = await Assert.ThrowsAsync<VoxaModelUnavailableException>(async () =>
        {
            await foreach (var _ in engine.ReadTranscriptsAsync(cts.Token)) { }
        });
        Assert.Contains("bad model name", ex.Message);

        await engine.StopAsync();
    }

    [Fact]
    public async Task DisposeAsync_Tears_Down_The_Server_It_Owns()
    {
        var fake = new RecordingServer();
        await using (new VoxtralRealtimeSttEngine(new VoxtralOptions(), fake, NullLogger.Instance))
        {
            // Never started — disposing the engine must still dispose the (managed-mode) server.
        }
        Assert.True(fake.Disposed);
    }

    private static async Task<List<TranscriptionResult>> ReadUntilFinalAsync(
        VoxtralRealtimeSttEngine engine, TimeSpan timeout)
    {
        var results = new List<TranscriptionResult>();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var r in engine.ReadTranscriptsAsync(cts.Token))
            {
                results.Add(r);
                if (r.IsFinal) break;
            }
        }
        catch (OperationCanceledException) { /* timed out — return what we have for a clear assertion failure */ }
        return results;
    }

    // Drain everything from the (StopAsync-completed) transcript channel.
    private static async Task<List<TranscriptionResult>> DrainFinalsAsync(VoxtralRealtimeSttEngine engine)
    {
        var finals = new List<TranscriptionResult>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var r in engine.ReadTranscriptsAsync(cts.Token))
            if (r.IsFinal) finals.Add(r);
        return finals;
    }

    private sealed class RecordingServer : IVoxtralServer
    {
        public bool Disposed { get; private set; }
        public Task<Uri> StartAsync(CancellationToken ct) => Task.FromResult(new Uri("ws://127.0.0.1:1/v1/realtime"));
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
