using System.Runtime.InteropServices;
using System.Text.Json;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;

namespace Voxa.Transports.Telephony.Tests;

/// <summary>
/// End-to-end behavior of the shared telephony base over a fake WebSocket + fake codec (VTL-001 T1.7):
/// inbound media → <see cref="AudioRawFrame"/> at the announced rate; outbound audio → media; barge-in →
/// epoch purge + clear; stop / socket-close → <see cref="EndFrame"/> with no deadlock.
/// </summary>
public class TelephonyMediaStreamTests
{
    // Polling caps: the assertion returns the instant the condition holds, so the cap only bounds failure.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private const string StartMsg = "{\"t\":\"start\"}";
    private const string StopMsg = "{\"t\":\"stop\"}";

    private static string MediaMsg(byte[] muLaw) => $"{{\"t\":\"media\",\"b64\":\"{Convert.ToBase64String(muLaw)}\"}}";
    private static string DtmfMsg(string digit) => $"{{\"t\":\"dtmf\",\"d\":\"{digit}\"}}";

    private static byte[] OutboundPayload(string mediaJson)
    {
        using var doc = JsonDocument.Parse(mediaJson);
        return Convert.FromBase64String(doc.RootElement.GetProperty("b64").GetString()!);
    }

    private static AudioRawFrame Pcm(short value, int sampleRate, int samples)
    {
        var s = new short[samples];
        Array.Fill(s, value);
        var bytes = new byte[samples * sizeof(short)];
        MemoryMarshal.AsBytes<short>(s).CopyTo(bytes);
        return new AudioRawFrame(bytes, sampleRate, 1);
    }

    private static async Task<List<Frame>> DrainSinkAsync(PipelineSink sink, TimeSpan timeout)
    {
        var frames = new List<Frame>();
        using var cts = new CancellationTokenSource(timeout);
        try { await foreach (var f in sink.ReadAllAsync(cts.Token)) frames.Add(f); }
        catch (OperationCanceledException) { }
        return frames;
    }

    // ---- Inbound: source decodes wire media into AudioRawFrames ----

    [Fact]
    public async Task InboundMedia_Decodes_MuLaw_And_Resamples_To_Input_Rate()
    {
        var ws = new FakeWebSocket();
        var codec = new FakeMediaCodec(TelephonyMediaFormat.MuLaw8k);
        var source = new TelephonyMediaStreamSource(ws, codec, inputSampleRate: 16000);
        var sink = new PipelineSink();
        var pipeline = Pipeline.Build().Source(source).Sink(sink);

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();

        // 160 μ-law samples of 0x80 (max positive, decodes to +32124) = one 20 ms chunk @ 8 kHz.
        var chunk = new byte[160];
        Array.Fill(chunk, (byte)0x80);
        await ws.QueueIncomingTextAsync(StartMsg);
        await ws.QueueIncomingTextAsync(MediaMsg(chunk));
        await ws.QueueIncomingTextAsync(StopMsg);

        var frames = await DrainSinkAsync(sink, Timeout);

        var audio = frames.OfType<AudioRawFrame>().Single();
        Assert.Equal(16000, audio.SampleRate);
        Assert.Equal(1, audio.Channels);

        var pcm = MemoryMarshal.Cast<byte, short>(audio.Pcm.Span);
        // 8 k → 16 k ≈ doubles the sample count.
        Assert.InRange(pcm.Length, 316, 322);
        // A constant μ-law level decodes+resamples to a constant PCM level (±linear-interp boundary).
        Assert.Equal((short)32124, pcm[0]);
        Assert.Equal((short)32124, pcm[^1]);

        Assert.Contains(frames, f => f is EndFrame);
    }

    [Fact]
    public async Task InboundMedia_Passthrough_When_Wire_Rate_Equals_Input_Rate()
    {
        var ws = new FakeWebSocket();
        var codec = new FakeMediaCodec(TelephonyMediaFormat.MuLaw8k);
        var source = new TelephonyMediaStreamSource(ws, codec, inputSampleRate: 8000); // no resample
        var sink = new PipelineSink();
        var pipeline = Pipeline.Build().Source(source).Sink(sink);

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();

        var chunk = new byte[160];
        Array.Fill(chunk, (byte)0x80);
        await ws.QueueIncomingTextAsync(MediaMsg(chunk));
        await ws.QueueIncomingTextAsync(StopMsg);

        var frames = await DrainSinkAsync(sink, Timeout);
        var pcm = MemoryMarshal.Cast<byte, short>(frames.OfType<AudioRawFrame>().Single().Pcm.Span);
        Assert.Equal(160, pcm.Length); // 1:1, no resample
        Assert.Equal((short)32124, pcm[0]);
    }

    [Fact]
    public async Task InboundMedia_Reassembles_A_Fragmented_Message()
    {
        // A JSON message split across two ReceiveAsync calls must be reassembled by the read loop's
        // pooled accumulator (slow path) before the codec parses it.
        var ws = new FakeWebSocket();
        var source = new TelephonyMediaStreamSource(ws, new FakeMediaCodec(TelephonyMediaFormat.MuLaw8k), inputSampleRate: 8000);
        var sink = new PipelineSink();
        var pipeline = Pipeline.Build().Source(source).Sink(sink);

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();

        var chunk = new byte[160];
        Array.Fill(chunk, (byte)0x80);
        var media = MediaMsg(chunk);
        var half = media.Length / 2;
        await ws.QueueIncomingTextAsync(media[..half], endOfMessage: false);
        await ws.QueueIncomingTextAsync(media[half..], endOfMessage: true);
        await ws.QueueIncomingTextAsync(StopMsg);

        var frames = await DrainSinkAsync(sink, Timeout);
        var pcm = MemoryMarshal.Cast<byte, short>(frames.OfType<AudioRawFrame>().Single().Pcm.Span);
        Assert.Equal(160, pcm.Length);
        Assert.Equal((short)32124, pcm[0]);
    }

    [Fact]
    public async Task InboundDtmf_Is_Routed_To_Hook()
    {
        var ws = new FakeWebSocket();
        var codec = new FakeMediaCodec();
        string? captured = null;
        var source = new TelephonyMediaStreamSource(ws, codec, inputSampleRate: 16000, onDtmf: d => captured = d);
        var sink = new PipelineSink();
        var pipeline = Pipeline.Build().Source(source).Sink(sink);

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();

        await ws.QueueIncomingTextAsync(StartMsg);
        await ws.QueueIncomingTextAsync(DtmfMsg("5"));
        await ws.QueueIncomingTextAsync(StopMsg);

        await DrainSinkAsync(sink, Timeout);
        Assert.Equal("5", captured);
    }

    // ---- Lifecycle: stop / socket close end the pipeline ----

    [Fact]
    public async Task StopEvent_Completes_Runner()
    {
        var ws = new FakeWebSocket();
        var source = new TelephonyMediaStreamSource(ws, new FakeMediaCodec(), inputSampleRate: 16000);
        var pipeline = Pipeline.Build().Source(source).Sink(new PipelineSink());

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();
        await ws.QueueIncomingTextAsync(StartMsg);
        await ws.QueueIncomingTextAsync(StopMsg);

        await runner.WaitAsync().WaitAsync(Timeout); // EndFrame reached the sink — no throw
    }

    [Fact]
    public async Task SocketClose_Completes_Runner()
    {
        var ws = new FakeWebSocket();
        var source = new TelephonyMediaStreamSource(ws, new FakeMediaCodec(), inputSampleRate: 16000);
        var pipeline = Pipeline.Build().Source(source).Sink(new PipelineSink());

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();
        await ws.QueueIncomingTextAsync(StartMsg);
        await ws.QueueCloseAsync();

        await runner.WaitAsync().WaitAsync(Timeout);
    }

    // ---- Outbound: sink resamples + encodes bot audio into media ----

    [Fact]
    public async Task OutboundAudio_Is_Resampled_Encoded_And_Sent_As_Media()
    {
        var ws = new FakeWebSocket();
        var codec = new FakeMediaCodec(TelephonyMediaFormat.MuLaw8k); // addressable
        var sink = new TelephonyMediaStreamSink(ws, codec, outputSampleRate: 16000);
        var pipeline = Pipeline.Build().Source(new PipelineSource()).Sink(sink);

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();

        // 320 samples @ 16 k of +32124 → resample to ~160 @ 8 k → μ-law 0x80.
        await pipeline.Source.IngestAsync(Pcm(32124, 16000, 320));

        var media = await ws.WaitForSentTextAsync(s => s.Contains("\"media\""), Timeout);
        Assert.NotNull(media);

        var payload = OutboundPayload(media!);
        Assert.InRange(payload.Length, 158, 162);          // 16 k → 8 k halves the count
        Assert.All(payload, b => Assert.Equal((byte)0x80, b)); // +32124 encodes to 0x80
    }

    [Fact]
    public async Task OutboundAudio_Skipped_When_Codec_Not_Addressable()
    {
        var ws = new FakeWebSocket();
        var codec = new FakeMediaCodec(addressable: false); // no stream id captured → BuildMedia returns null
        var sink = new TelephonyMediaStreamSink(ws, codec, outputSampleRate: 16000);
        var pipeline = Pipeline.Build().Source(new PipelineSource()).Sink(sink);

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();
        await pipeline.Source.IngestAsync(Pcm(32124, 16000, 320));
        await Task.Delay(150);

        Assert.Equal(0, ws.CountSentText(s => s.Contains("\"media\"")));
    }

    // ---- Barge-in: epoch purge + clear ----

    [Fact]
    public async Task Interruption_PurgesQueuedAudio_AndSendsClear()
    {
        var ws = new FakeWebSocket();
        var codec = new FakeMediaCodec(TelephonyMediaFormat.MuLaw8k);
        var sink = new TelephonyMediaStreamSink(ws, codec, outputSampleRate: 16000);
        var pipeline = Pipeline.Build().Source(new PipelineSource()).Sink(sink);

        await using var runner = new PipelineRunner(pipeline);
        ws.BlockSends();                 // park the writer so bot audio piles up in the queue
        await runner.StartAsync();

        // 20 stale silence chunks (PCM 0 → μ-law 0xFF).
        for (int i = 0; i < 20; i++)
            await pipeline.Source.IngestAsync(Pcm(0, 16000, 320));
        await Task.Delay(60);

        await pipeline.Source.IngestAsync(new InterruptionFrame()); // bumps epoch (system channel)
        await Task.Delay(40);

        await pipeline.Source.IngestAsync(Pcm(32124, 16000, 320));  // post-interruption (new epoch) → μ-law 0x80
        ws.ReleaseSends();

        var clear = await ws.WaitForSentTextAsync(s => s.Contains("\"clear\""), Timeout);
        Assert.NotNull(clear);

        // The post-interruption chunk (contains 0x80) must survive...
        await ws.WaitForSentTextAsync(s => s.Contains("\"media\"") && OutboundPayload(s).Contains((byte)0x80), Timeout);
        await Task.Delay(80);

        var media = ws.SentTextAsString.Where(s => s.Contains("\"media\"")).Select(OutboundPayload).ToList();
        Assert.Contains(media, p => Array.IndexOf(p, (byte)0x80) >= 0);                  // post survived
        // ...and at most one pre-interruption (all-0xFF) chunk may have been in-flight before the purge.
        Assert.True(media.Count(p => Array.TrueForAll(p, b => b == 0xFF)) <= 1,
            "more than one pre-interruption (silence) chunk survived the purge");
    }

    [Fact]
    public async Task CallerHangup_DoesNotDeadlock_Runner()
    {
        var ws = new FakeWebSocket();
        var sink = new TelephonyMediaStreamSink(ws, new FakeMediaCodec(), outputSampleRate: 16000);
        var pipeline = Pipeline.Build().Source(new PipelineSource()).Sink(sink);

        await using var runner = new PipelineRunner(pipeline);
        await runner.StartAsync();

        // Caller hangs up: the socket leaves Open BEFORE the EndFrame reaches the sink. The sink must
        // still complete its outbound channel (the WebSocketAudioSink anti-deadlock rule) so the runner
        // ends gracefully rather than burning the full StopAsync grace and cancelling.
        ws.Abort();
        await runner.StopAsync(TimeSpan.FromSeconds(2));
        await runner.WaitAsync().WaitAsync(Timeout);
    }
}
