using System.Runtime.CompilerServices;

namespace Voxa.Studio.Audio;

/// <summary>
/// No-op backend used on platforms without an audio implementation yet (macOS/Linux — see the
/// VST-001 risk register) and in headless tests. Everything except live Talk capture/playback
/// still works: Voice Lab synthesis, the Models view, and the Config composer are audio-free.
/// </summary>
public sealed class NullAudioDevice : IStudioAudioDevice
{
    public IReadOnlyList<AudioEndpoint> CaptureEndpoints() => [];
    public IReadOnlyList<AudioEndpoint> RenderEndpoints() => [];

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
        AudioEndpoint microphone, int sampleRate, [EnumeratorCancellation] CancellationToken ct)
    {
        // No frames, ever — parks until the session stops.
        await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        yield break;
    }

    public ValueTask StartRenderAsync(AudioEndpoint speaker, int sampleRate, CancellationToken ct)
        => ValueTask.CompletedTask;

    public ValueTask RenderAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
        => ValueTask.CompletedTask;

    public ValueTask FlushRenderAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Platform selection for the audio backend.</summary>
public static class StudioAudioDevice
{
    /// <summary>WASAPI on Windows; the null backend elsewhere (UI runs, live audio doesn't).</summary>
    public static IStudioAudioDevice CreatePlatformDefault()
        => OperatingSystem.IsWindows() ? new WasapiAudioDevice() : new NullAudioDevice();
}
