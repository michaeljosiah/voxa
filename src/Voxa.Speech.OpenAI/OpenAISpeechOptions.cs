namespace Voxa.Speech.OpenAI;

/// <summary>
/// Configuration for OpenAI's Whisper STT and TTS REST APIs. Override <see cref="ApiBaseUrl"/>
/// to point at an OpenAI-compatible endpoint (Azure OpenAI, local proxy, etc.).
/// </summary>
public sealed record OpenAISpeechOptions
{
    /// <summary>OpenAI API key — sent as <c>Authorization: Bearer &lt;key&gt;</c>.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Base URL for the API. Defaults to OpenAI public endpoint.</summary>
    public string ApiBaseUrl { get; init; } = "https://api.openai.com/v1";

    /// <summary>TTS model. <c>tts-1</c>, <c>tts-1-hd</c>, or <c>gpt-4o-mini-tts</c>.</summary>
    public string TtsModel { get; init; } = "tts-1";

    /// <summary>TTS voice. <c>alloy</c>, <c>echo</c>, <c>fable</c>, <c>onyx</c>, <c>nova</c>, <c>shimmer</c>.</summary>
    public string TtsVoice { get; init; } = "alloy";

    /// <summary>STT model. Use <c>whisper-1</c> for the standard transcription endpoint.</summary>
    public string SttModel { get; init; } = "whisper-1";

    /// <summary>Optional BCP-47 language hint for STT. Null lets Whisper auto-detect.</summary>
    public string? SttLanguage { get; init; }

    /// <summary>How many seconds of audio to buffer before posting one Whisper batch. Trade-off: latency vs. cost-per-call. Default 3s.</summary>
    public double SttBufferSeconds { get; init; } = 1.5;

    /// <summary>Sample rate of audio fed into STT (must match <see cref="Voxa.Frames.AudioRawFrame"/> input).</summary>
    public int InputSampleRate { get; init; } = 16000;

    /// <summary>Sample rate of audio emitted by TTS. OpenAI's PCM output is 24 kHz.</summary>
    public int OutputSampleRate { get; init; } = 24000;
}
