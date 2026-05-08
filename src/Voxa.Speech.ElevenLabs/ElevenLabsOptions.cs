namespace Voxa.Speech.ElevenLabs;

/// <summary>Configuration for the ElevenLabs TTS REST API.</summary>
public sealed record ElevenLabsOptions
{
    /// <summary>API key — sent as <c>xi-api-key</c> header.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Voice id from the ElevenLabs library or a cloned voice.</summary>
    public required string VoiceId { get; init; }

    /// <summary>Base URL for the API. Defaults to ElevenLabs public endpoint; override for regional endpoints.</summary>
    public string ApiBaseUrl { get; init; } = "https://api.elevenlabs.io/v1";

    /// <summary>Model id. Defaults to <c>eleven_multilingual_v2</c>.</summary>
    public string ModelId { get; init; } = "eleven_multilingual_v2";

    /// <summary>Output sample rate for PCM. ElevenLabs supports 8/16/22.05/24/44.1/48 kHz at 16-bit.</summary>
    public int OutputSampleRate { get; init; } = 24000;

    /// <summary>Optional voice settings. Null uses the voice's defaults.</summary>
    public ElevenLabsVoiceSettings? VoiceSettings { get; init; }
}

/// <summary>ElevenLabs voice tuning parameters.</summary>
public sealed record ElevenLabsVoiceSettings
{
    public double? Stability { get; init; }
    public double? SimilarityBoost { get; init; }
    public double? Style { get; init; }
    public double? Speed { get; init; }
    public bool? UseSpeakerBoost { get; init; }
}
