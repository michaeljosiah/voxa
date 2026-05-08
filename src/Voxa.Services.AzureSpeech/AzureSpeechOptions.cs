namespace Voxa.Services.AzureSpeech;

/// <summary>
/// Configuration shared by <see cref="AzureSpeechSttProcessor"/> and
/// <see cref="AzureSpeechTtsProcessor"/>. Use the same instance for both when running them
/// in the same pipeline against the same Azure Speech resource.
/// </summary>
public sealed record AzureSpeechOptions
{
    /// <summary>Subscription key for the Azure Speech resource.</summary>
    public required string SubscriptionKey { get; init; }

    /// <summary>Azure region of the Speech resource (e.g. <c>eastus</c>, <c>northeurope</c>).</summary>
    public required string Region { get; init; }

    /// <summary>Locale for STT recognition. Defaults to en-US.</summary>
    public string RecognitionLanguage { get; init; } = "en-US";

    /// <summary>Voice id for TTS output. Defaults to a neutral neural voice.</summary>
    public string Voice { get; init; } = "en-US-JennyNeural";

    /// <summary>Sample rate of audio fed into STT. Voice Live default is 24 kHz; Azure Speech accepts 16 kHz native.</summary>
    public int InputSampleRate { get; init; } = 16000;

    /// <summary>Channel count of audio fed into STT.</summary>
    public int InputChannels { get; init; } = 1;

    /// <summary>Sample rate of audio emitted by TTS. Stays 24 kHz to match the rest of the Voxa default chain.</summary>
    public int OutputSampleRate { get; init; } = 24000;
}
