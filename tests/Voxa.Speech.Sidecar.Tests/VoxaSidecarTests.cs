using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Voxa.Speech.Sidecar.Tests;

/// <summary>
/// Covers the parts that need no sidecar process: the wire protocol framing, request encoding, the
/// engine's streaming of a channel's PCM, and the honest "no sidecar configured" guard (VVL-002 ships
/// no frozen binary). Real synthesis is exercised manually against a built sidecar.
/// </summary>
public class VoxaSidecarTests
{
    private static MemoryStream Response(int sampleRate, params byte[][] frames)
    {
        var ms = new MemoryStream();
        ms.Write(Encoding.UTF8.GetBytes($"{{\"sample_rate\":{sampleRate}}}\n"));
        var len = new byte[4];
        foreach (var f in frames)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)f.Length);
            ms.Write(len);
            ms.Write(f);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(len, 0); // terminator
        ms.Write(len);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public async Task Protocol_Reads_Header_Then_Frames_To_The_Terminator()
    {
        using var stream = Response(24000, [1, 2, 3, 4], [5, 6]);
        var collected = new List<byte>();
        await foreach (var chunk in SidecarProtocol.ReadResponseAsync(stream, CancellationToken.None))
            collected.AddRange(chunk.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, collected);
    }

    [Fact]
    public async Task Protocol_Error_Header_Throws()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"error\":\"model missing\"}\n"));
        await Assert.ThrowsAsync<VoxaModelUnavailableException>(async () =>
        {
            await foreach (var _ in SidecarProtocol.ReadResponseAsync(stream, CancellationToken.None)) { }
        });
    }

    [Fact]
    public async Task Protocol_Realigns_After_An_Error_For_The_Next_Request()
    {
        // Codex P2: the sidecar writes a terminator after its error header. The reader must drain it so a
        // second request on the same long-lived stream reads cleanly instead of NUL-desyncing.
        using var stream = new MemoryStream();
        stream.Write(Encoding.UTF8.GetBytes("{\"error\":\"boom\"}\n"));
        WriteLen(stream, 0);                                                 // request 1 terminator
        stream.Write(Encoding.UTF8.GetBytes("{\"sample_rate\":24000}\n"));   // request 2
        WriteLen(stream, 2); stream.Write(new byte[] { 7, 8 }); WriteLen(stream, 0);
        stream.Position = 0;

        await Assert.ThrowsAsync<VoxaModelUnavailableException>(async () =>
        {
            await foreach (var _ in SidecarProtocol.ReadResponseAsync(stream, CancellationToken.None)) { }
        });

        var collected = new List<byte>();
        await foreach (var chunk in SidecarProtocol.ReadResponseAsync(stream, CancellationToken.None))
            collected.AddRange(chunk.ToArray());
        Assert.Equal(new byte[] { 7, 8 }, collected); // the second response, read after realigning
    }

    private static void WriteLen(Stream stream, uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    [Fact]
    public void EncodeRequest_Is_A_SnakeCase_Json_Line()
    {
        var line = Encoding.UTF8.GetString(
            SidecarProtocol.EncodeRequest(new SidecarRequest("hi", "default", "en", 24000)));
        Assert.EndsWith("\n", line);
        Assert.Contains("\"sample_rate\":24000", line);
        Assert.Contains("\"text\":\"hi\"", line);
    }

    [Fact]
    public async Task Engine_Streams_The_Channel_Pcm()
    {
        await using var engine = new SidecarTtsEngine(new SidecarOptions(), new FakeChannel());
        await engine.StartAsync(CancellationToken.None);
        var bytes = new List<byte>();
        await foreach (var chunk in engine.SynthesizeAsync("hello", CancellationToken.None))
            bytes.AddRange(chunk.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes);
    }

    [Fact]
    public async Task Unconfigured_Sidecar_Fails_With_Guidance()
    {
        await using var engine = new SidecarTtsEngine(new SidecarOptions()); // real channel, no path set
        var ex = await Assert.ThrowsAsync<VoxaModelUnavailableException>(
            () => engine.StartAsync(CancellationToken.None));
        Assert.Contains("ExecutablePath", ex.Message);
        Assert.Contains("PythonScript", ex.Message);
    }

    [Fact]
    public void Descriptor_Requires_A_Sidecar_Path()
    {
        var root = new ConfigurationBuilder().Build().GetSection("Voxa");
        var error = Assert.Single(SidecarDescriptors.Tts.Validate(root));
        Assert.Contains("ExecutablePath", error);
        Assert.Contains("PythonScript", error);
    }

    private sealed class FakeChannel : ISidecarChannel
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
            SidecarRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            yield return new byte[] { 1, 2 };
            await Task.Yield();
            yield return new byte[] { 3, 4 };
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
