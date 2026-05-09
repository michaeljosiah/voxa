using Voxa.Speech;

namespace Voxa.Speech.Abstractions.Tests;

public class PcmStreamReaderTests
{
    /// <summary>Stream that returns its data in pre-scripted reads, simulating arbitrary HTTP/TCP boundaries.</summary>
    private sealed class ChunkedStream : Stream
    {
        private readonly Queue<byte[]> _chunks;
        private byte[]? _current;
        private int _currentPos;

        public ChunkedStream(IEnumerable<byte[]> chunks) { _chunks = new(chunks); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_current is null || _currentPos >= _current.Length)
            {
                if (_chunks.Count == 0) return 0;
                _current = _chunks.Dequeue();
                _currentPos = 0;
            }
            int available = _current.Length - _currentPos;
            int take = Math.Min(count, available);
            Array.Copy(_current, _currentPos, buffer, offset, take);
            _currentPos += take;
            return take;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromResult(Read(buffer, offset, count));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<byte[]> ReadAllAsync(Stream s, int chunkSize)
    {
        var ms = new MemoryStream();
        await foreach (var c in PcmStreamReader.ReadEvenChunksAsync(s, chunkSize, default))
            await ms.WriteAsync(c);
        return ms.ToArray();
    }

    [Fact]
    public async Task Even_Single_Chunk_Yields_Same_Bytes()
    {
        var input = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        using var stream = new ChunkedStream(new[] { input });
        var output = await ReadAllAsync(stream, chunkSize: 1024);
        Assert.Equal(input, output);
    }

    [Fact]
    public async Task Odd_Chunks_Are_Carried_Forward_So_Stream_Stays_Aligned()
    {
        // The whole stream is bytes 1..10 (even total = 10 bytes = 5 samples).
        // We split it into HTTP "chunks" with odd boundaries: 3, 4, 3 bytes.
        // Without carry: yields would be 2-byte (drop 1) + 4-byte + 2-byte (drop 1) = 8 bytes,
        //   AND every yielded chunk after the first would be misaligned by 1 byte.
        // With carry: yields total 10 bytes, byte-for-byte equal to input.
        var stream = new ChunkedStream(new[]
        {
            new byte[] { 1, 2, 3 },
            new byte[] { 4, 5, 6, 7 },
            new byte[] { 8, 9, 10 },
        });
        var output = await ReadAllAsync(stream, chunkSize: 1024);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 }, output);
    }

    [Fact]
    public async Task Odd_Total_Length_Drops_Final_Orphan_Byte()
    {
        // Total = 7 bytes. After carry handling, 6 bytes yield (3 samples), 1 byte discarded.
        var stream = new ChunkedStream(new[] { new byte[] { 1, 2, 3, 4, 5, 6, 7 } });
        var output = await ReadAllAsync(stream, chunkSize: 1024);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, output);
    }

    [Fact]
    public async Task Each_Yielded_Chunk_Is_Even_Length()
    {
        var stream = new ChunkedStream(new[]
        {
            new byte[] { 1, 2, 3 },           // odd
            new byte[] { 4, 5 },              // odd boundary continuation
            new byte[] { 6, 7, 8, 9, 10 },    // odd
        });
        await foreach (var chunk in PcmStreamReader.ReadEvenChunksAsync(stream, chunkSize: 1024, default))
        {
            Assert.Equal(0, chunk.Length & 1);
        }
    }

    [Fact]
    public async Task ChunkSize_Caps_Each_Read_So_Output_Is_Multiple_Chunks_For_Long_Streams()
    {
        var input = Enumerable.Range(0, 20).Select(i => (byte)i).ToArray();
        var stream = new ChunkedStream(new[] { input });
        var chunks = new List<byte[]>();
        await foreach (var c in PcmStreamReader.ReadEvenChunksAsync(stream, chunkSize: 6, default))
            chunks.Add(c);
        Assert.True(chunks.Count >= 3);
        Assert.Equal(input, chunks.SelectMany(c => c).ToArray());
    }
}
