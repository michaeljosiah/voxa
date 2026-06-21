using System.Threading.Channels;
using NAudio.Wave;
using Voxa.Audio;   // shared LinearResampler (promoted from Studio in VTL-001)

namespace Voxa.Studio.Audio;

/// <summary>
/// Converts whatever WASAPI's shared-mode mix format delivers into PCM16 mono 20 ms frames at
/// the pipeline rate. Runs on NAudio's capture thread.
///
/// <para>
/// Real-world mix formats are rarely the plain encodings: most endpoints expose
/// <see cref="WaveFormatEncoding.Extensible"/> with an IEEE-float or PCM subformat GUID —
/// treating only <c>IeeeFloat</c>/<c>Pcm</c> as convertible would silently drop every buffer
/// on those devices and leave Studio deaf. Extensible subformats are resolved here, and an
/// actually-unconvertible format throws at construction so the Talk view shows a real error
/// instead of a microphone that never speaks.
/// </para>
/// </summary>
internal sealed class CaptureFormatConverter
{
    // WAVE_FORMAT_EXTENSIBLE subformat GUIDs (KSDATAFORMAT_SUBTYPE_*).
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private enum SampleFormat { Float32, Pcm16, Pcm24, Pcm32 }

    private readonly SampleFormat _sampleFormat;
    private readonly int _channels;
    private readonly LinearResampler _resampler;
    private readonly ChannelWriter<byte[]> _writer;
    private readonly int _frameSamples;          // 20 ms at the pipeline rate
    private readonly List<short> _pending = new();
    private short[] _monoScratch = new short[4800];
    private short[] _outScratch = new short[4800];

    public CaptureFormatConverter(WaveFormat format, int targetRate, ChannelWriter<byte[]> writer)
    {
        ArgumentNullException.ThrowIfNull(format);
        _sampleFormat = Resolve(format);
        _channels = format.Channels;
        _resampler = new LinearResampler(format.SampleRate, targetRate);
        _writer = writer;
        _frameSamples = targetRate / 50;
    }

    /// <summary>Map the device format (incl. Extensible subformats) to a decode mode, or throw loudly.</summary>
    private static SampleFormat Resolve(WaveFormat format)
    {
        bool isFloat = format.Encoding switch
        {
            WaveFormatEncoding.IeeeFloat => true,
            WaveFormatEncoding.Pcm => false,
            WaveFormatEncoding.Extensible when format is WaveFormatExtensible ext =>
                ext.SubFormat == SubtypeIeeeFloat ? true
                : ext.SubFormat == SubtypePcm ? false
                : throw Unsupported(format, $"extensible subformat {ext.SubFormat}"),
            _ => throw Unsupported(format, $"encoding {format.Encoding}"),
        };

        // Container bits decide the read; WAVE_FORMAT_EXTENSIBLE samples are left-aligned in
        // their container, so reading the full container preserves relative scale even when
        // ValidBitsPerSample is smaller (e.g. 24-in-32).
        return (isFloat, format.BitsPerSample) switch
        {
            (true, 32) => SampleFormat.Float32,
            (false, 16) => SampleFormat.Pcm16,
            (false, 24) => SampleFormat.Pcm24,
            (false, 32) => SampleFormat.Pcm32,
            _ => throw Unsupported(format, $"{(isFloat ? "float" : "PCM")} {format.BitsPerSample}-bit"),
        };
    }

    private static NotSupportedException Unsupported(WaveFormat format, string detail)
        => new($"This capture endpoint's mix format ({detail}, {format.SampleRate} Hz, " +
               $"{format.Channels} ch) is not supported by Voxa Studio's converter. " +
               "Switch the device to a shared-mode 16/24/32-bit format in the OS sound settings.");

    public void OnDataAvailable(object? sender, WaveInEventArgs e)
        => Convert(e.Buffer.AsSpan(0, e.BytesRecorded));

    /// <summary>Decode → mono-average → resample → frame; emits any completed 20 ms frames.</summary>
    internal void Convert(ReadOnlySpan<byte> raw)
    {
        int monoCount = ToMono(raw);
        if (monoCount == 0) return;

        int max = _resampler.MaxOutputSamples(monoCount);
        if (_outScratch.Length < max) _outScratch = new short[max];
        int written = _resampler.Process(_monoScratch.AsSpan(0, monoCount), _outScratch);

        for (int i = 0; i < written; i++) _pending.Add(_outScratch[i]);
        while (_pending.Count >= _frameSamples)
        {
            var frame = new byte[_frameSamples * 2];
            for (int i = 0; i < _frameSamples; i++)
            {
                frame[i * 2] = (byte)_pending[i];
                frame[i * 2 + 1] = (byte)(_pending[i] >> 8);
            }
            _pending.RemoveRange(0, _frameSamples);
            _writer.TryWrite(frame); // bounded DropOldest — never blocks the audio thread
        }
    }

    /// <summary>Average interleaved channels into mono shorts. Returns sample count written.</summary>
    private int ToMono(ReadOnlySpan<byte> raw)
    {
        int bytesPerSample = _sampleFormat == SampleFormat.Float32 || _sampleFormat == SampleFormat.Pcm32 ? 4
            : _sampleFormat == SampleFormat.Pcm24 ? 3 : 2;
        int sampleSets = raw.Length / bytesPerSample / _channels;
        if (_monoScratch.Length < sampleSets) _monoScratch = new short[sampleSets];

        for (int s = 0; s < sampleSets; s++)
        {
            double sum = 0;
            for (int c = 0; c < _channels; c++)
            {
                var sample = raw.Slice((s * _channels + c) * bytesPerSample, bytesPerSample);
                sum += _sampleFormat switch
                {
                    SampleFormat.Float32 => BitConverter.ToSingle(sample),
                    SampleFormat.Pcm16 => BitConverter.ToInt16(sample) / 32768.0,
                    SampleFormat.Pcm24 => (sample[2] << 24 | sample[1] << 16 | sample[0] << 8) / 2147483648.0,
                    SampleFormat.Pcm32 => BitConverter.ToInt32(sample) / 2147483648.0,
                    _ => 0,
                };
            }
            _monoScratch[s] = (short)Math.Clamp(sum / _channels * 32768.0, short.MinValue, short.MaxValue);
        }

        return sampleSets;
    }
}
