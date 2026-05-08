namespace Voxa.Transports.WebSocket;

/// <summary>
/// Audio configuration for <see cref="WebSocketAudioSource"/>. Determines how incoming binary
/// frames are tagged with sample rate and channel count.
/// </summary>
public sealed record WebSocketAudioOptions
{
    /// <summary>Sample rate of incoming audio binary frames. Defaults to 24 kHz (Voice Live default).</summary>
    public int InputSampleRate { get; init; } = 24000;

    /// <summary>Channel count of incoming audio. Defaults to 1 (mono).</summary>
    public int Channels { get; init; } = 1;

    /// <summary>Buffer size for the WebSocket read loop. Larger values reduce loop iterations on big frames.</summary>
    public int ReadBufferSize { get; init; } = 16 * 1024;
}
