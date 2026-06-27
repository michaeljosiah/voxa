using Microsoft.Extensions.Logging.Abstractions;

namespace Voxa.Speech.Voxtral.Tests;

/// <summary>The streaming engine end-to-end against the in-process fake realtime server (no vLLM, GPU, or model).
/// vLLM's realtime API is one-shot per connection, so the engine opens a socket per utterance: a test drives
/// <c>OnUserStartedSpeakingAsync</c> (open) → <c>WriteAudioAsync</c> → <c>FlushAsync</c> (final commit) per turn.</summary>
public class VoxtralRealtimeSttEngineTests
{
    [Fact]
    public async Task Handshakes_Streams_Audio_And_Emits_Interim_Then_Final()
    {
        await using var server = new MiniRealtimeServer(deltas: ["hello", " world"], doneText: "hello world");
        var options = new VoxtralOptions { ServerUrl = server.ServerUrl, Model = "test-model", Language = "en" };
        await using var engine = new VoxtralRealtimeSttEngine(options, NullLogger.Instance);

        await engine.StartAsync(CancellationToken.None);
        await engine.OnUserStartedSpeakingAsync();          // opens this utterance's connection
        var pcm = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        await engine.WriteAudioAsync(pcm, CancellationToken.None);
        await engine.FlushAsync();                          // VAD speech-end → final commit

        var results = await ReadUntilFinalAsync(engine, TimeSpan.FromSeconds(10));

        Assert.Equal("test-model", server.Model);           // handshake reached the server
        Assert.Equal(pcm, server.ReceivedAudio);            // audio arrived as base64 append
        Assert.Contains(results, r => !r.IsFinal && r.Text == "hello"); // running deltas → interims
        var final = Assert.Single(results, r => r.IsFinal);
        Assert.Equal("hello world", final.Text);            // done → one final
        Assert.Equal("en", final.Language);

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

    [Fact] // codex P1: append (data loop) and the final commit (system loop) overlap — concurrent ws sends must serialize
    public async Task Concurrent_Append_And_Commit_Do_Not_Drop_The_Final()
    {
        await using var server = new MiniRealtimeServer(deltas: ["x"], doneText: "x done");
        var options = new VoxtralOptions { ServerUrl = server.ServerUrl, Model = "test-model" };
        await using var engine = new VoxtralRealtimeSttEngine(options, NullLogger.Instance);
        await engine.StartAsync(CancellationToken.None);
        await engine.OnUserStartedSpeakingAsync();

        var pcm = new byte[4096];
        await engine.WriteAudioAsync(pcm, CancellationToken.None);   // buffer audio so the commit has something to finalize
        // Overlap a second append with the commit — without a send lock the concurrent ClientWebSocket sends throw
        // InvalidOperationException, the catch swallows it, and the dropped commit would mean no transcription.done.
        await Task.WhenAll(
            engine.WriteAudioAsync(pcm, CancellationToken.None).AsTask(),
            engine.FlushAsync());

        var results = await ReadUntilFinalAsync(engine, TimeSpan.FromSeconds(10));
        Assert.Contains(results, r => r.IsFinal && r.Text == "x done");

        await engine.StopAsync();
    }

    [Fact] // each utterance is its own connection (one-shot per connection) — every turn finalizes
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

    [Fact] // codex P2: a clean stop right after flush still drains the committed utterance's in-flight done
    public async Task StopAsync_Drains_A_Committed_Utterance_Whose_Done_Is_Still_In_Flight()
    {
        // The done is delayed so it is still in flight when StopAsync runs.
        await using var server = new MiniRealtimeServer(deltas: ["c"], doneText: "committed", doneDelayMs: 150);
        var options = new VoxtralOptions { ServerUrl = server.ServerUrl, Model = "test-model" };
        await using var engine = new VoxtralRealtimeSttEngine(options, NullLogger.Instance);
        await engine.StartAsync(CancellationToken.None);

        await engine.OnUserStartedSpeakingAsync();
        await engine.WriteAudioAsync(new byte[] { 1, 2 }, CancellationToken.None);
        await engine.FlushAsync();   // final commit; the done is delayed and not yet read
        await engine.StopAsync();    // must wait for the in-flight done before closing

        Assert.Contains(await DrainFinalsAsync(engine), t => t.Text == "committed");
    }

    [Fact] // codex P2: a stop with un-flushed audio (abrupt disconnect mid-utterance) still drains the tail transcript
    public async Task StopAsync_Drains_The_Tail_Transcript_When_Audio_Was_Not_Flushed()
    {
        await using var server = new MiniRealtimeServer(deltas: ["tail"], doneText: "tail end");
        var options = new VoxtralOptions { ServerUrl = server.ServerUrl, Model = "test-model" };
        await using var engine = new VoxtralRealtimeSttEngine(options, NullLogger.Instance);
        await engine.StartAsync(CancellationToken.None);

        await engine.OnUserStartedSpeakingAsync();
        await engine.WriteAudioAsync(new byte[] { 1, 2 }, CancellationToken.None); // audio, but NO FlushAsync
        await engine.StopAsync();   // flushes the tail (final:true) and drains the done before closing

        Assert.Contains(await DrainFinalsAsync(engine), t => t.Text == "tail end");
    }

    [Fact] // back-to-back utterances each deliver their final even when dones lag: next-start and stop both drain
    public async Task Back_To_Back_Utterances_Both_Finalize_When_Dones_Lag()
    {
        await using var server = new MiniRealtimeServer(deltas: ["u"], doneText: "utterance", doneDelayMs: 120);
        var options = new VoxtralOptions { ServerUrl = server.ServerUrl, Model = "test-model" };
        await using var engine = new VoxtralRealtimeSttEngine(options, NullLogger.Instance);
        await engine.StartAsync(CancellationToken.None);

        await engine.OnUserStartedSpeakingAsync();
        await engine.WriteAudioAsync(new byte[] { 1, 2 }, CancellationToken.None);
        await engine.FlushAsync();                          // utterance 1: done still in flight
        await engine.OnUserStartedSpeakingAsync();          // drains utterance 1 before opening utterance 2
        await engine.WriteAudioAsync(new byte[] { 3, 4 }, CancellationToken.None);
        await engine.FlushAsync();                          // utterance 2: done still in flight
        await engine.StopAsync();                           // drains utterance 2

        Assert.Equal(2, (await DrainFinalsAsync(engine)).Count(t => t.Text == "utterance"));
    }

    [Fact] // codex P2: a server error frame must fault the transcript stream, not hang Talk with no error
    public async Task Server_Error_Frame_Faults_The_Transcript_Stream()
    {
        await using var server = new MiniRealtimeServer(deltas: [], doneText: "", error: "bad model name");
        var options = new VoxtralOptions { ServerUrl = server.ServerUrl, Model = "does-not-exist" };
        await using var engine = new VoxtralRealtimeSttEngine(options, NullLogger.Instance);
        await engine.StartAsync(CancellationToken.None);
        await engine.OnUserStartedSpeakingAsync();          // opens the connection; the server rejects it

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
