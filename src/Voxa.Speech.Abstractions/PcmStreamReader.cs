using System.Runtime.CompilerServices;

namespace Voxa.Speech;

/// <summary>
/// Reads a raw 16-bit PCM stream and yields chunks whose byte counts are always even.
/// HTTP/TCP boundaries can split a 16-bit sample across reads; consumers (a JS
/// <c>Int16Array</c>, e.g.) misinterpret odd-byte messages and end up byte-shifted from then
/// on — sounds like clicks / static. This reader buffers a single trailing byte across reads
/// so every yielded chunk respects the 2-byte sample boundary.
///
/// <para>Used by every streaming TTS engine in the <c>Voxa.Speech.*</c> packages. Public so
/// any third-party engine implementer can opt in.</para>
/// </summary>
public static class PcmStreamReader
{
    /// <summary>
    /// Read <paramref name="stream"/> end-to-end and yield chunks each ≤ <paramref name="chunkSize"/>+1
    /// bytes in length, all with even byte counts. A single trailing odd byte at end-of-stream is
    /// discarded (it's an incomplete sample).
    ///
    /// <para>
    /// Allocation-free: read and output buffers are rented from the shared <c>ArrayPool&lt;byte&gt;</c>
    /// once and reused. Each yielded <see cref="ReadOnlyMemory{T}"/> is only valid until the next
    /// <c>MoveNextAsync</c> — copy it if you need to keep it.
    /// </para>
    /// </summary>
    public static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadEvenChunksAsync(
        Stream stream,
        int chunkSize,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (chunkSize < 2) throw new ArgumentOutOfRangeException(nameof(chunkSize), "chunkSize must be at least 2.");

        var readBuf = System.Buffers.ArrayPool<byte>.Shared.Rent(chunkSize);
        var outBuf = System.Buffers.ArrayPool<byte>.Shared.Rent(chunkSize + 1);  // room for a prepended carry byte
        bool hasCarry = false;
        byte carry = 0;
        int read;

        try
        {
            while ((read = await stream.ReadAsync(readBuf.AsMemory(0, chunkSize), ct).ConfigureAwait(false)) > 0)
            {
                int totalAvailable = read + (hasCarry ? 1 : 0);
                int yieldSize = totalAvailable & ~1;       // round down to even
                int leftover = totalAvailable - yieldSize; // 0 or 1

                if (yieldSize > 0)
                {
                    int destIdx = 0;
                    if (hasCarry)
                    {
                        outBuf[0] = carry;
                        destIdx = 1;
                    }
                    Buffer.BlockCopy(readBuf, 0, outBuf, destIdx, yieldSize - destIdx);
                    yield return outBuf.AsMemory(0, yieldSize);   // valid until next MoveNextAsync
                }

                if (leftover == 1)
                {
                    carry = readBuf[read - 1];
                    hasCarry = true;
                }
                else
                {
                    hasCarry = false;
                }
            }
            // Any final orphan byte is an incomplete sample — discard it.
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(readBuf);
            System.Buffers.ArrayPool<byte>.Shared.Return(outBuf);
        }
    }
}
