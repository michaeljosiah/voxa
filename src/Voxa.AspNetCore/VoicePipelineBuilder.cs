using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.AspNetCore;

/// <summary>
/// Fluent accumulator for a Voxa voice pipeline. Created by
/// <see cref="MapVoxaVoiceExtensions"/>; the configured builder is invoked per
/// connection to materialise the actual pipeline.
///
/// <para>
/// The builder accumulates <em>factories</em>, not instances — Voxa's processors hold per-connection
/// state (channels, locks, internal tasks) so each WebSocket gets its own fresh chain.
/// </para>
/// </summary>
public sealed class VoicePipelineBuilder
{
    private readonly List<Func<HttpContext, FrameProcessor>> _processorFactories = new();
    private readonly List<string> _authPolicies = new();
    private readonly List<string> _corsPolicies = new();
    private Func<HttpContext, WebSocket, CancellationToken, ValueTask>? _helloReader;
    private Func<Frame, string?>? _customSerializer;

    // ── Internal accessors used by MapVoxaVoice ────────────────────────────

    internal IReadOnlyList<Func<HttpContext, FrameProcessor>> ProcessorFactories => _processorFactories;
    internal IReadOnlyList<string> AuthPolicies => _authPolicies;
    internal IReadOnlyList<string> CorsPolicies => _corsPolicies;
    internal Func<HttpContext, WebSocket, CancellationToken, ValueTask>? HelloReader => _helloReader;
    internal Func<Frame, string?>? CustomSerializer => _customSerializer;

    /// <summary>
    /// Set by <see cref="DefaultVoicePipelineComposer"/> when composing the default pipeline.
    /// When set, the handler injects a <c>SessionInfoFrame</c> immediately after pipeline start
    /// so clients receive the announced sample rates before audio flows.
    /// </summary>
    internal int? SessionInputSampleRate { get; set; }

    /// <inheritdoc cref="SessionInputSampleRate"/>
    internal int? SessionOutputSampleRate { get; set; }

    // ── Generic processor registration ─────────────────────────────────────

    /// <summary>
    /// Add a processor to the pipeline. The factory is invoked per WebSocket connection.
    /// Use this overload when the factory needs the request <see cref="HttpContext"/> (e.g. to
    /// resolve scoped DI services, read claims, inspect the parsed hello envelope).
    /// </summary>
    public VoicePipelineBuilder UseProcessor(Func<HttpContext, FrameProcessor> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _processorFactories.Add(factory);
        return this;
    }

    /// <summary>
    /// Add a processor to the pipeline. Convenience overload for stateless factories that don't
    /// need the <see cref="HttpContext"/>.
    /// </summary>
    public VoicePipelineBuilder UseProcessor(Func<FrameProcessor> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _processorFactories.Add(_ => factory());
        return this;
    }

    // ── Authorization / CORS ───────────────────────────────────────────────

    /// <summary>
    /// Apply authorization policies to the mapped endpoint. Equivalent to chaining
    /// <c>RouteHandlerBuilder.RequireAuthorization(...)</c> on the route in <see cref="MapVoxaVoiceExtensions"/>.
    /// </summary>
    public VoicePipelineBuilder RequireAuthorization(params string[] policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        _authPolicies.AddRange(policies);
        return this;
    }

    /// <summary>Apply CORS policies to the mapped endpoint.</summary>
    public VoicePipelineBuilder RequireCors(params string[] policies)
    {
        ArgumentNullException.ThrowIfNull(policies);
        _corsPolicies.AddRange(policies);
        return this;
    }

    // ── Hello envelope handling ────────────────────────────────────────────

    /// <summary>
    /// Register a typed hello-envelope reader. After the WebSocket upgrade the supplied
    /// <paramref name="reader"/> is invoked exactly once; the result lands in
    /// <c>HttpContext.Items[VoiceHello.HelloMetadataKey]</c> and in every
    /// <see cref="VoiceTurnContext.Metadata"/> under the same key. Hosts read it from the
    /// metadata bag inside processor factories or hook delegates.
    /// </summary>
    public VoicePipelineBuilder UseWebSocketHello<T>(
        Func<WebSocket, CancellationToken, ValueTask<T>> reader)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(reader);
        _helloReader = async (ctx, ws, ct) =>
        {
            var hello = await reader(ws, ct).ConfigureAwait(false);
            ctx.Items[VoiceHello.HelloMetadataKey] = hello;
        };
        return this;
    }

    // ── Custom frame serialization (forwarded to the sink) ─────────────────

    /// <summary>
    /// Register a custom frame serializer for the WebSocket sink. Returning a non-null string
    /// from <paramref name="serializer"/> sends that JSON as a text frame; returning null falls
    /// through to Voxa's built-in serialization. Hosts use this to emit custom frame types
    /// (e.g. AONIK <c>ThreadReadyFrame</c>) without subclassing the sink.
    /// </summary>
    public VoicePipelineBuilder UseCustomFrameSerializer(Func<Frame, string?> serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        _customSerializer = serializer;
        return this;
    }
}
