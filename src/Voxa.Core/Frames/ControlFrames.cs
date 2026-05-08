namespace Voxa.Frames;

/// <summary>
/// Initial frame injected by <see cref="Pipelines.PipelineRunner"/> when a session starts.
/// Carries the negotiated audio configuration so processors can configure their codecs.
/// </summary>
public sealed record StartFrame(
    int? SampleRate = null,
    int? Channels = null) : ControlFrame;

/// <summary>
/// Graceful shutdown signal. When observed at the sink, the runner's <c>WaitAsync</c> completes.
/// Marked uninterruptible so it survives a mid-flight <see cref="InterruptionFrame"/>.
/// </summary>
public sealed record EndFrame : ControlFrame, IUninterruptible;

/// <summary>Optional keepalive — transports may emit periodically to detect dead connections.</summary>
public sealed record HeartbeatFrame : ControlFrame;
