namespace Voxa.Studio.Services;

/// <summary>The pipeline role a provider identity can fill (VST-003 WS2). One identity may fill several.</summary>
public enum ProviderRole { Stt, Tts, Agent }

/// <summary>
/// One configurable field on a provider (VST-003 WS2). <paramref name="IsSecret"/> governs UI masking
/// (the value is stored either way); <paramref name="ConfigKey"/> is the <c>Voxa:X:Y</c> key written
/// into the live secrets configuration layer.
/// </summary>
public sealed record ProviderFieldDescriptor(
    string Name,
    string Label,
    string? Placeholder,
    bool IsSecret,
    string ConfigKey);

/// <summary>
/// A supported provider <em>identity</em> (VST-003 WS2): a single registry name + config section that
/// may fill several roles. <c>OpenAI</c> fills STT, TTS and the chat agent off one
/// <c>Voxa:OpenAI:ApiKey</c>; <c>Mistral</c> fills STT and TTS. <see cref="Name"/> matches the registry
/// descriptor name exactly so the Config dropdown filter and the registry stay in lock-step.
/// </summary>
public sealed record ProviderManifest(
    string Name,
    string DisplayName,
    ProviderRole[] Roles,
    string Description,
    bool IsLocal,
    string? DocsUrl,
    IReadOnlyList<ProviderFieldDescriptor> Fields)
{
    /// <summary>"STT · TTS · Agent" — the roles, for the row subtitle and flyout card.</summary>
    public string RolesLabel => string.Join(" · ", Roles.Select(RoleLabel));

    private static string RoleLabel(ProviderRole role) => role switch
    {
        ProviderRole.Stt => "STT",
        ProviderRole.Tts => "TTS",
        ProviderRole.Agent => "Agent",
        _ => role.ToString(),
    };
}
