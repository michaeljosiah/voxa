using System.Collections.Immutable;
using NUlid;

namespace Voxa.Frames;

/// <summary>
/// Direction of frame travel through the pipeline. Most frames flow downstream
/// (source → sink). Errors and certain control signals travel upstream.
/// </summary>
public enum FrameDirection
{
    Downstream,
    Upstream,
}

/// <summary>
/// Base type for all pipeline frames. Records — equal by value, cheap to clone via <c>with</c>.
/// </summary>
public abstract record Frame
{
    // Stored as the ULID struct (no heap allocation), generated once per frame and copied by `with`.
    // The 26-char string is materialized only on read — and the only production reader is tracing — so the
    // hot path no longer allocates a string per frame, keeping the data loop allocation-free (CQ-004).
    private readonly Ulid _id = Ulid.NewUlid();

    /// <summary>Lexicographically sortable unique id (ULID, time-prefixed). Materialized on read; set with a valid ULID.</summary>
    public string Id
    {
        get => _id.ToString();
        init => _id = Ulid.Parse(value);
    }

    /// <summary>Direction of travel through the pipeline.</summary>
    public FrameDirection Direction { get; init; } = FrameDirection.Downstream;

    /// <summary>Monotonic timestamp in microseconds. Stamped at pipeline ingress; zero until then.</summary>
    public long PtsMicros { get; init; }

    /// <summary>Free-form metadata bag. Use sparingly — keep typed payload on concrete frames.</summary>
    public ImmutableDictionary<string, object?> Metadata { get; init; } = ImmutableDictionary<string, object?>.Empty;
}

/// <summary>Frames carrying domain payload (audio, text, tool calls). Ordered with the data stream.</summary>
public abstract record DataFrame : Frame;

/// <summary>Frames carrying ordered control signals (start, end, heartbeat). Travel with the data stream.</summary>
public abstract record ControlFrame : Frame;

/// <summary>
/// Frames carrying high-priority signals (interruption, errors, speaking events). Routed through a
/// separate priority channel so they preempt the data stream.
/// </summary>
public abstract record SystemFrame : Frame;

/// <summary>
/// Marker for frames that must NOT be discarded when an <see cref="InterruptionFrame"/>
/// flushes the in-flight data stream. Useful for finalization frames like <see cref="EndFrame"/>.
/// </summary>
public interface IUninterruptible { }
