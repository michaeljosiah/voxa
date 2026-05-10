using System.Text.Json.Serialization;

namespace Voxa.AspNetCore;

/// <summary>
/// Default <c>hello</c> envelope shape. Hosts that need additional fields define their own type
/// and register it via <see cref="VoicePipelineBuilder.UseWebSocketHello{T}"/>; the typed instance
/// lands in the per-connection metadata under <see cref="HelloMetadataKey"/>.
///
/// <para>
/// The base shape covers the fields any voice pipeline needs: which agent to talk to, an optional
/// thread/session id to resume, and the frontend-tool capability list.
/// </para>
/// </summary>
public class VoiceHello
{
    /// <summary>Constant key used to retrieve the parsed hello from <c>VoiceTurnContext.Metadata</c>.</summary>
    public const string HelloMetadataKey = "voxa.hello";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "hello";

    /// <summary>Logical agent identifier the host wants to talk to.</summary>
    [JsonPropertyName("agentId")]
    public string? AgentId { get; set; }

    /// <summary>Optional. Resume an existing thread/session by id.</summary>
    [JsonPropertyName("chatThreadId")]
    public string? ChatThreadId { get; set; }

    /// <summary>Optional list of frontend-tool names the client can render.</summary>
    [JsonPropertyName("frontendTools")]
    public List<string>? FrontendTools { get; set; }
}
