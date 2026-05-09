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
    /// Read <paramref name="stream"/> end-to-end and yield byte arrays each ≤ <paramref name="chunkSize"/>
    /// in length, all with even byte counts. A single trailing odd byte at end-of-stream is discarded
    /// (it's an incomplete sample).
    /// </summary>
    public static async IAsyncEnumerable<byte[]> ReadEvenChunksAsync(
        Stream stream,
        int chunkSize,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (chunkSize < 2) throw new ArgumentOutOfRangeException(nameof(chunkSize), "chunkSize must be at least 2.");

        var buffer = new byte[chunkSize];
        bool hasCarry = false;
        byte carry = 0;
        int read;

        while ((read = await stream.ReadAsync(buffer.AsMemory(0, chunkSize), ct).ConfigureAwait(false)) > 0)
        {
            int totalAvailable = read + (hasCarry ? 1 : 0);
            int yieldSize = totalAvailable & ~1;       // round down to even
            int leftover = totalAvailable - yieldSize; // 0 or 1

            if (yieldSize > 0)
            {
                var chunk = new byte[yieldSize];
                int destIdx = 0;
                if (hasCarry)
                {
                    chunk[0] = carry;
                    destIdx = 1;
                }
                Buffer.BlockCopy(buffer, 0, chunk, destIdx, yieldSize - destIdx);
                yield return chunk;
            }

            if (leftover == 1)
            {
                carry = buffer[read - 1];
                hasCarry = true;
            }
            else
            {
                hasCarry = false;
            }
        }
        // Any final orphan byte is an incomplete sample — discard it.
    }
}
