using Voxa.Frames;
using Voxa.Processors;
using Voxa.Testing.Audio;

namespace Voxa.Testing.Processors;

/// <summary>
/// Pipeline sink that accumulates incoming <see cref="AudioRawFrame"/>s and writes them to
/// a WAV file when an <see cref="EndFrame"/> is observed. Use as the tail of a test pipeline
/// to capture pipeline output for golden-file comparisons.
/// </summary>
public sealed class WavFileSinkProcessor : PipelineSink
{
    private readonly string _path;
    private readonly List<byte> _buffer = new();
    private int _sampleRate;
    private int _channels;

    public WavFileSinkProcessor(string path) : base("WavFileSink")
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        if (frame is AudioRawFrame audio)
        {
            if (_sampleRate == 0) { _sampleRate = audio.SampleRate; _channels = audio.Channels; }
            _buffer.AddRange(audio.Pcm.ToArray());
        }
        else if (frame is EndFrame && _sampleRate > 0)
        {
            WavFile.Write(_path, _buffer.ToArray(), _sampleRate, _channels);
        }

        await base.ProcessFrameAsync(frame, ct).ConfigureAwait(false);
    }
}
