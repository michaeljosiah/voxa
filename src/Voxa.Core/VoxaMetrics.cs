using System.Diagnostics.Metrics;

namespace Voxa;

/// <summary>
/// Process-wide <see cref="Meter"/> for Voxa pipeline performance counters. Subscribe with
/// OpenTelemetry (<c>builder.AddMeter(VoxaMetrics.MeterName)</c>) or a raw
/// <see cref="MeterListener"/> to observe latency and backpressure in production.
/// </summary>
public static class VoxaMetrics
{
    /// <summary>Meter name to register with OpenTelemetry or a <see cref="MeterListener"/>.</summary>
    public const string MeterName = "Voxa";

    /// <summary>The shared meter all Voxa counters are created from.</summary>
    public static readonly Meter Meter = new(MeterName, "1.0");

    /// <summary>
    /// Voice-to-voice latency: from the moment user speech ends (<c>UserStoppedSpeakingFrame</c>
    /// observed at the sink) to the first outbound bot audio byte sent on the wire. The single
    /// number that the performance work targets.
    /// </summary>
    public static readonly Histogram<double> TurnTtfbMs =
        Meter.CreateHistogram<double>("voxa.turn.ttfb", unit: "ms",
            description: "Voice-to-voice latency: user stopped speaking to first bot audio sent");

    /// <summary>Outbound send-queue depth sampled when a frame is enqueued at the sink.</summary>
    public static readonly Histogram<int> SinkQueueDepth =
        Meter.CreateHistogram<int>("voxa.sink.queue_depth", unit: "{frames}",
            description: "Depth of the WebSocket sink's outbound queue at enqueue time");
}
