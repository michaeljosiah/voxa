using Voxa.Frames;
using Voxa.Processors;
using Voxa.Testing.Audio;

namespace Voxa.Testing.Processors;

/// <summary>
/// Pipeline source that reads a 16-bit PCM WAV file and emits it as a stream of
/// <see cref="AudioRawFrame"/>s, followed by an <see cref="EndFrame"/>. Use as the head
/// of a test pipeline to drive offline integration tests without a live audio source.
/// </summary>
public sealed class WavFileSourceProcessor : PipelineSource
{
    private readonly string _path;
    private readonly int _frameDurationMs;

    /// <param name="path">Path to a 16-bit PCM WAV file.</param>
    /// <param name="frameDurationMs">Size of each emitted audio chunk, in milliseconds. Defaults to 20ms.</param>
    public WavFileSourceProcessor(string path, int frameDurationMs = 20)
        : base("WavFileSource")
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        _frameDurationMs = frameDurationMs > 0 ? frameDurationMs : throw new ArgumentOutOfRangeException(nameof(frameDurationMs));
    }

    protected override ValueTask OnStartAsync(StartFrame frame, CancellationToken ct)
    {
        _ = Task.Run(() => StreamWavAsync(ct), ct);
        return ValueTask.CompletedTask;
    }

    private async Task StreamWavAsync(CancellationToken ct)
    {
        try
        {
            var wav = WavFile.Read(_path);
            int bytesPerSecond = wav.SampleRate * wav.Channels * 2;
            int chunkBytes = Math.Max(2, (bytesPerSecond * _frameDurationMs) / 1000);

            for (int offset = 0; offset < wav.Pcm.Length; offset += chunkBytes)
            {
                ct.ThrowIfCancellationRequested();
                int len = Math.Min(chunkBytes, wav.Pcm.Length - offset);
                var chunk = new ReadOnlyMemory<byte>(wav.Pcm, offset, len);
                await IngestAsync(new AudioRawFrame(chunk, wav.SampleRate, wav.Channels), ct).ConfigureAwait(false);
            }

            await IngestAsync(new EndFrame(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }
}
