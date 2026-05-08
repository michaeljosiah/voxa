using System.Diagnostics;
using System.Reflection;

namespace Voxa;

/// <summary>
/// Public <see cref="ActivitySource"/> for the Voxa pipeline. Subscribe via OpenTelemetry to
/// trace per-frame and per-processor activity:
/// <code>
/// services.AddOpenTelemetry()
///     .WithTracing(t => t.AddSource(VoxaActivities.SourceName).AddOtlpExporter());
/// </code>
/// </summary>
public static class VoxaActivities
{
    /// <summary>The <see cref="ActivitySource.Name"/> Voxa publishes activities under.</summary>
    public const string SourceName = "Voxa";

    /// <summary>Singleton <see cref="ActivitySource"/>. Voxa.Observability and consumers may emit on this.</summary>
    public static readonly ActivitySource Source = new(
        SourceName,
        typeof(VoxaActivities).Assembly.GetName().Version?.ToString() ?? "0.0.0");
}
