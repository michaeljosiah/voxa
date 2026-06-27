using Microsoft.Extensions.Logging.Abstractions;

namespace Voxa.Speech.Voxtral.Tests;

/// <summary>The streaming engine end-to-end against the in-process fake realtime server (no vLLM, GPU, or model).</summary>
public class VoxtralRealtimeSttEngineTests
{
    [Fact]
    public async Task Handshakes_Streams_Audio_And_Emits_Interim_Then_Final()
    {
        await using var server = new MiniRealtimeServer(deltas: ["hello", " world"], doneText: "hello world");
        var options = new VoxtralOptions { ServerUrl = server.ServerUrl, Model = "test-model", Language = "en" };
        await using var engine = new VoxtralRealtimeSttEngine(options, NullLogger.Instance);

        await engine.StartAsync(CancellationToken.None);
        var pcm = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        await engine.WriteAudioAsync(pcm, CancellationToken.None);
        await engine.FlushAsync(); // VAD speech-end → commit

        var results = await ReadUntilFinalAsync(engine, TimeSpan.FromSeconds(10));

        Assert.Equal("test-model", server.Model);          // handshake reached the server
        Assert.Equal(pcm, server.ReceivedAudio);           // audio arrived as base64 append
        Assert.Contains(results, r => !r.IsFinal && r.Text == "hello"); // running deltas → interims
        var final = Assert.Single(results, r => r.IsFinal);
        Assert.Equal("hello world", final.Text);           // done → one final
        Assert.Equal("en", final.Language);

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

    private sealed class RecordingServer : IVoxtralServer
    {
        public bool Disposed { get; private set; }
        public Task<Uri> StartAsync(CancellationToken ct) => Task.FromResult(new Uri("ws://127.0.0.1:1/v1/realtime"));
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
