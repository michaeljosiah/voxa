namespace Voxa.Processors;

/// <summary>
/// Per-turn payload handed to <see cref="IAgentTurnDriver.RunTurnAsync"/>. Carries the user's
/// transcribed input, a turn id (for telemetry/correlation), the gateway for frontend-tool
/// round-trips, an emitter for out-of-band frame pushes, and an open metadata bag for
/// host-specific state (e.g. AONIK puts <c>Hello</c> + <c>ClaimsPrincipal</c> here).
///
/// <para>
/// Constructed by <see cref="AgentLoopProcessor"/> per turn. Hosts never construct this directly.
/// </para>
/// </summary>
public sealed class VoiceTurnContext
{
    /// <summary>Stable id for this turn — survives re-runs on tool results.</summary>
    public required string TurnId { get; init; }

    /// <summary>The user's transcribed text that triggered this turn.</summary>
    public required string UserText { get; init; }

    /// <summary>Frontend-tool back-channel — call <c>AwaitToolResultAsync(callId)</c> after yielding a request.</summary>
    public required IFrontendToolGateway FrontendTools { get; init; }

    /// <summary>Out-of-band frame emitter for host-specific frames (e.g. AONIK <c>ThreadReadyFrame</c>).</summary>
    public required IFrameEmitter Emitter { get; init; }

    /// <summary>
    /// Open metadata bag. Keys are case-sensitive. Hosts populate via
    /// <see cref="AgentLoopProcessor"/>'s constructor or per-turn setup; turn drivers read with
    /// <see cref="GetMetadata{T}"/>.
    /// </summary>
    public IDictionary<string, object?> Metadata { get; init; } = new Dictionary<string, object?>();

    /// <summary>Convenience typed accessor; returns null when the key is missing or wrong-typed.</summary>
    public T? GetMetadata<T>(string key) where T : class
        => Metadata.TryGetValue(key, out var value) ? value as T : null;
}
