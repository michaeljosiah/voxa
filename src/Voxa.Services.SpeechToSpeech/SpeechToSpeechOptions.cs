namespace Voxa.Services.SpeechToSpeech;

/// <summary>
/// Configuration for <see cref="SpeechToSpeechProcessor"/> (VRT-005 WS2) — the session-level knobs applied on
/// start, peer to the cloud composites' options. Model/host-specific settings live on the concrete
/// <c>ISpeechToSpeechSession</c> the factory builds (deferred).
/// </summary>
public sealed record SpeechToSpeechOptions
{
    /// <summary>Voice / preset id applied via <c>SetVoiceAsync</c> on start. Empty leaves the model default.</summary>
    public string Voice { get; init; } = "";

    /// <summary>Optional system prompt / persona applied via <c>SetSystemPromptAsync</c> on start when set.</summary>
    public string? SystemPrompt { get; init; }
}
