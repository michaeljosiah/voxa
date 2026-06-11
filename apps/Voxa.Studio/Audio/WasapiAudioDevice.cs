using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Voxa.Studio.Audio;

/// <summary>
/// Windows (WASAPI shared-mode) implementation of <see cref="IStudioAudioDevice"/> via NAudio
/// (MIT). Capture converts whatever the endpoint mix format is (typically 32-bit float stereo
/// at 44.1/48 kHz) to PCM16 mono at the pipeline rate in 20 ms frames; render queues PCM16 mono
/// into a buffered provider and lets WASAPI's format pipeline take it to the device rate.
/// Exclusive-mode endpoints are out of scope (v1) — they show up but may fail to open; the
/// Talk view surfaces that error instead of crashing.
/// </summary>
public sealed class WasapiAudioDevice : IStudioAudioDevice
{
    private readonly MMDeviceEnumerator _enumerator = new();

    private WasapiOut? _output;
    private BufferedWaveProvider? _renderBuffer;

    public static bool IsSupported => OperatingSystem.IsWindows();

    public IReadOnlyList<AudioEndpoint> CaptureEndpoints() => Endpoints(DataFlow.Capture);
    public IReadOnlyList<AudioEndpoint> RenderEndpoints() => Endpoints(DataFlow.Render);

    private List<AudioEndpoint> Endpoints(DataFlow flow)
    {
        string? defaultId = null;
        try
        {
            using var def = _enumerator.GetDefaultAudioEndpoint(flow, Role.Communications);
            defaultId = def.ID;
        }
        catch (Exception) { /* no default endpoint (no devices at all) */ }

        var list = new List<AudioEndpoint>();
        foreach (var device in _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
                list.Add(new AudioEndpoint(device.ID, device.FriendlyName, device.ID == defaultId));
        }
        // Default first, then alphabetical — the picker's first entry is always a sane choice.
        return list.OrderByDescending(e => e.IsDefault).ThenBy(e => e.DisplayName).ToList();
    }

    // ── capture ──────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
        AudioEndpoint microphone, int sampleRate, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(microphone);

        using var device = _enumerator.GetDevice(microphone.Id);
        using var capture = new WasapiCapture(device, useEventSync: true, audioBufferMillisecondsLength: 20);

        var frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // a stalled pipeline drops mic audio, never deadlocks WASAPI
            SingleReader = true,
        });

        var converter = new CaptureConverter(capture.WaveFormat, sampleRate, frames.Writer);
        capture.DataAvailable += converter.OnDataAvailable;
        capture.RecordingStopped += (_, e) => frames.Writer.TryComplete(e.Exception);

        capture.StartRecording();
        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return frame;
        }
        finally
        {
            capture.StopRecording();
        }
    }

    /// <summary>Mix-format → PCM16-mono-at-pipeline-rate, framed to 20 ms. Runs on NAudio's capture thread.</summary>
    private sealed class CaptureConverter
    {
        private readonly WaveFormat _format;
        private readonly LinearResampler _resampler;
        private readonly ChannelWriter<byte[]> _writer;
        private readonly int _frameSamples;          // 20 ms at the pipeline rate
        private readonly List<short> _pending = new();
        private short[] _monoScratch = new short[4800];
        private short[] _outScratch = new short[4800];

        public CaptureConverter(WaveFormat format, int targetRate, ChannelWriter<byte[]> writer)
        {
            _format = format;
            _resampler = new LinearResampler(format.SampleRate, targetRate);
            _writer = writer;
            _frameSamples = targetRate / 50;
        }

        public void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            int monoCount = ToMono(e.Buffer.AsSpan(0, e.BytesRecorded));
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

        /// <summary>Average channels to mono shorts. Handles IEEE float and PCM16 mix formats.</summary>
        private int ToMono(ReadOnlySpan<byte> raw)
        {
            int channels = _format.Channels;
            if (_format.Encoding == WaveFormatEncoding.IeeeFloat && _format.BitsPerSample == 32)
            {
                int sampleSets = raw.Length / 4 / channels;
                EnsureMono(sampleSets);
                for (int s = 0; s < sampleSets; s++)
                {
                    float sum = 0;
                    for (int c = 0; c < channels; c++)
                        sum += BitConverter.ToSingle(raw.Slice((s * channels + c) * 4, 4));
                    _monoScratch[s] = (short)Math.Clamp(sum / channels * 32768f, short.MinValue, short.MaxValue);
                }
                return sampleSets;
            }

            if (_format.Encoding == WaveFormatEncoding.Pcm && _format.BitsPerSample == 16)
            {
                int sampleSets = raw.Length / 2 / channels;
                EnsureMono(sampleSets);
                for (int s = 0; s < sampleSets; s++)
                {
                    int sum = 0;
                    for (int c = 0; c < channels; c++)
                        sum += BitConverter.ToInt16(raw.Slice((s * channels + c) * 2, 2));
                    _monoScratch[s] = (short)(sum / channels);
                }
                return sampleSets;
            }

            return 0; // unsupported mix format — silence rather than noise
        }

        private void EnsureMono(int count)
        {
            if (_monoScratch.Length < count) _monoScratch = new short[count];
        }
    }

    // ── render ───────────────────────────────────────────────────────────────

    public ValueTask StartRenderAsync(AudioEndpoint speaker, int sampleRate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(speaker);
        StopRender();

        using var device = _enumerator.GetDevice(speaker.Id);
        _renderBuffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromMinutes(2), // long bot turns queue fully; barge-in clears
            DiscardOnBufferOverflow = true,
        };
        // 80 ms latency: glitch-resistant on a busy dev machine, irrelevant next to TTS latency.
        _output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 80);
        _output.Init(_renderBuffer); // WasapiOut inserts the format/rate conversion to the mix format
        _output.Play();
        return ValueTask.CompletedTask;
    }

    public ValueTask RenderAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        var buffer = _renderBuffer ?? throw new InvalidOperationException(
            "StartRenderAsync must be called before RenderAsync.");
        var segment = pcm.ToArray();
        buffer.AddSamples(segment, 0, segment.Length);
        return ValueTask.CompletedTask;
    }

    public ValueTask FlushRenderAsync()
    {
        _renderBuffer?.ClearBuffer();
        return ValueTask.CompletedTask;
    }

    private void StopRender()
    {
        try { _output?.Stop(); } catch { /* device unplugged mid-session */ }
        _output?.Dispose();
        _output = null;
        _renderBuffer = null;
    }

    public ValueTask DisposeAsync()
    {
        StopRender();
        _enumerator.Dispose();
        return ValueTask.CompletedTask;
    }
}
