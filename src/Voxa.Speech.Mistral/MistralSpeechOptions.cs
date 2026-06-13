namespace Voxa.Speech.Mistral;

/// <summary>
/// Configuration for Mistral's Voxtral-TTS audio API. The endpoint is OpenAI-compatible
/// (<c>/v1/audio/speech</c>), so override <see cref="ApiBaseUrl"/> if Mistral moves the route.
/// </summary>
public sealed record MistralSpeechOptions
{
    /// <summary>Mistral API key — sent as <c>Authorization: Bearer &lt;key&gt;</c>.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Base URL for the API. Defaults to Mistral's public endpoint.</summary>
    public string ApiBaseUrl { get; init; } = "https://api.mistral.ai/v1";

    /// <summary>TTS model id (Mistral's Voxtral family).</summary>
    public string Model { get; init; } = "voxtral-tts";

    /// <summary>Voice id — either a built-in voice or a cloned voice profile id.</summary>
    public string Voice { get; init; } = "alloy";

    /// <summary>Output sample rate for PCM.</summary>
    public int OutputSampleRate { get; init; } = 24000;

    /// <summary>Voxtral transcription model id (VVL-001 WS2) — used when Voxa:Stt is "Mistral".</summary>
    public string SttModel { get; init; } = "voxtral-mini-latest";

    /// <summary>PCM sample rate the STT engine receives and wraps as WAV before posting.</summary>
    public int InputSampleRate { get; init; } = 16000;

    /// <summary>Optional BCP-47 language hint for transcription; null lets Voxtral auto-detect.</summary>
    public string? SttLanguage { get; init; }

    /// <summary>
    /// Safety-backstop flush interval (seconds) for the buffered transcription path: if VAD never
    /// fires <c>UserStoppedSpeaking</c> (runaway monologue / VAD-less pipeline) the buffer is posted
    /// once it exceeds this span. The primary flush is <c>FlushAsync</c> at speech-end. 0 disables it.
    /// </summary>
    public double SttBufferSeconds { get; init; } = 30;
}
