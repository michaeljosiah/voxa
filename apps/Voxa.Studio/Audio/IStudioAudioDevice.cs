namespace Voxa.Studio.Audio;

/// <summary>One capture or render endpoint as shown in the device pickers.</summary>
public sealed record AudioEndpoint(string Id, string DisplayName, bool IsDefault)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// The only OS-specific surface in Voxa Studio (VST-001 WS1). v1 ships
/// <see cref="WasapiAudioDevice"/> (Windows); a macOS/Linux backend is a new implementation of
/// this interface — nothing above it changes.
///
/// <para>
/// Contract: the device layer owns ALL resampling. The pipeline announces its effective rates
/// (the same values a server's session envelope carries) and this layer adapts whatever the
/// hardware runs at. Pipeline-facing audio is always 16-bit PCM mono.
/// </para>
/// </summary>
public interface IStudioAudioDevice : IAsyncDisposable
{
    /// <summary>Available microphones. Empty when the platform backend is unavailable.</summary>
    IReadOnlyList<AudioEndpoint> CaptureEndpoints();

    /// <summary>Available speakers/outputs. Empty when the platform backend is unavailable.</summary>
    IReadOnlyList<AudioEndpoint> RenderEndpoints();

    /// <summary>
    /// Stream 20 ms PCM16 mono frames from <paramref name="microphone"/> at
    /// <paramref name="sampleRate"/> until cancelled. Each yielded buffer is caller-owned.
    /// </summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
        AudioEndpoint microphone, int sampleRate, CancellationToken ct);

    /// <summary>Open <paramref name="speaker"/> for PCM16 mono playback at <paramref name="sampleRate"/>.</summary>
    ValueTask StartRenderAsync(AudioEndpoint speaker, int sampleRate, CancellationToken ct);

    /// <summary>Queue synthesized audio for gap-free playback. Non-blocking.</summary>
    ValueTask RenderAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct);

    /// <summary>Barge-in: drop every queued-but-unplayed sample immediately.</summary>
    ValueTask FlushRenderAsync();
}
