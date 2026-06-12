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

        // Throws NotSupportedException for a genuinely unconvertible mix format — a loud Talk
        // start error beats a microphone that silently never produces a frame.
        var converter = new CaptureFormatConverter(capture.WaveFormat, sampleRate, frames.Writer);
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
