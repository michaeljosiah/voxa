using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Testing.Processors;

namespace Voxa.Transports.WebSocket.Tests;

/// <summary>
/// Covers the single-copy / pooled-accumulator receive path (VPS-001 WS2): fast path for
/// whole-message receives, slow path for messages spanning multiple receives, and that the
/// binary/text accumulator state never leaks across messages.
/// </summary>
public class WebSocketAudioSourceFragmentationTests
{
    private static (PipelineRunner Runner, FakeWebSocket Ws, CapturingProcessor Captured) Build()
    {
        var ws = new FakeWebSocket();
        var source = new WebSocketAudioSource(ws);
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build().Source(source).Then(captured).Sink(new PipelineSink());
        return (new PipelineRunner(pipeline), ws, captured);
    }

    [Fact]
    public async Task Binary_FragmentedAcrossThreeReceives_Reassembles()
    {
        var (runner, ws, captured) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            // Three partial receives, only the last with EndOfMessage = true.
            await ws.QueueIncomingBinaryAsync(new byte[] { 1, 2, 3 }, endOfMessage: false);
            await ws.QueueIncomingBinaryAsync(new byte[] { 4, 5, 6 }, endOfMessage: false);
            await ws.QueueIncomingBinaryAsync(new byte[] { 7, 8 }, endOfMessage: true);

            await captured.WaitForAsync(2, TimeSpan.FromSeconds(2));
            var audio = captured.Captured.OfType<AudioRawFrame>().FirstOrDefault();
            Assert.NotNull(audio);
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, audio!.Pcm.ToArray());
        }
    }

    [Fact]
    public async Task Text_FragmentedAcrossReceives_Parses()
    {
        var (runner, ws, captured) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            const string json = "{\"type\":\"toolResult\",\"callId\":\"c9\",\"resultJson\":\"{}\"}";
            int mid = json.Length / 2;
            await ws.QueueIncomingTextAsync(json[..mid], endOfMessage: false);
            await ws.QueueIncomingTextAsync(json[mid..], endOfMessage: true);

            await captured.WaitForAsync(2, TimeSpan.FromSeconds(2));
            var tool = captured.Captured.OfType<ToolCallResultFrame>().FirstOrDefault();
            Assert.NotNull(tool);
            Assert.Equal("c9", tool!.CallId);
        }
    }

    [Fact]
    public async Task InterleavedTextThenBinary_BothArrive_NoAccumulatorLeak()
    {
        var (runner, ws, captured) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);

            // A fragmented binary message, then a whole text message, then a whole binary message.
            await ws.QueueIncomingBinaryAsync(new byte[] { 10, 20 }, endOfMessage: false);
            await ws.QueueIncomingBinaryAsync(new byte[] { 30, 40 }, endOfMessage: true);
            await ws.QueueIncomingTextAsync("{\"type\":\"text\",\"text\":\"hi\"}", endOfMessage: true);
            await ws.QueueIncomingBinaryAsync(new byte[] { 50, 60 }, endOfMessage: true);

            await captured.WaitForAsync(4, TimeSpan.FromSeconds(2));

            var audios = captured.Captured.OfType<AudioRawFrame>().ToList();
            Assert.Equal(2, audios.Count);
            Assert.Equal(new byte[] { 10, 20, 30, 40 }, audios[0].Pcm.ToArray());   // reassembled, no stale prefix
            Assert.Equal(new byte[] { 50, 60 }, audios[1].Pcm.ToArray());           // fast path, clean
            Assert.Contains(captured.Captured.OfType<TextFrame>(), t => t.Text == "hi");
        }
    }
}
