using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Voxa.Speech.Sidecar;

/// <summary>One synthesis request sent to the sidecar (serialized as a single JSON line on its stdin).</summary>
public sealed record SidecarRequest(string Text, string? Voice, string? Language, int SampleRate, string Mode = "speak");

internal sealed class SidecarResponseHeader
{
    [JsonPropertyName("sample_rate")] public int SampleRate { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>
/// The Voxa ↔ sidecar stdio wire protocol (VVL-002). <b>Request:</b> one JSON line on the sidecar's
/// stdin. <b>Response</b> on stdout: one JSON header line (<c>{"sample_rate":N}</c> or
/// <c>{"error":"…"}</c>) then a sequence of length-prefixed PCM16 frames
/// (<c>[uint32 little-endian length][that many bytes]</c>), ended by a zero-length frame. Kept tiny and
/// framework-free so both ends — .NET here, Python in <c>sidecar/</c> — implement it trivially, and so
/// the framing is unit-testable over a <see cref="MemoryStream"/> without spawning a process.
/// </summary>
internal static class SidecarProtocol
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static byte[] EncodeRequest(SidecarRequest request)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, Json) + "\n");

    /// <summary>Read the header line then yield each PCM frame until the zero-length terminator.</summary>
    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadResponseAsync(
        Stream stdout, [EnumeratorCancellation] CancellationToken ct)
    {
        var headerLine = await ReadLineAsync(stdout, ct).ConfigureAwait(false)
            ?? throw new VoxaModelUnavailableException("Voxa TTS sidecar closed its output before sending a response header.");

        var header = JsonSerializer.Deserialize<SidecarResponseHeader>(headerLine, Json);
        if (header?.Error is { Length: > 0 } error)
            throw new VoxaModelUnavailableException($"Voxa TTS sidecar reported an error: {error}");

        var lengthBuffer = new byte[4];
        while (true)
        {
            await ReadExactlyAsync(stdout, lengthBuffer, ct).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(lengthBuffer);
            if (length == 0) yield break; // end-of-utterance sentinel
            var frame = new byte[length];
            await ReadExactlyAsync(stdout, frame, ct).ConfigureAwait(false);
            yield return frame;
        }
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0) return sb.Length == 0 ? null : sb.ToString();
            if (one[0] == (byte)'\n') return sb.ToString();
            if (one[0] != (byte)'\r') sb.Append((char)one[0]); // header is ASCII JSON
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (n == 0)
                throw new VoxaModelUnavailableException(
                    "Voxa TTS sidecar closed its output mid-frame (the process likely crashed — check its stderr log).");
            offset += n;
        }
    }
}
