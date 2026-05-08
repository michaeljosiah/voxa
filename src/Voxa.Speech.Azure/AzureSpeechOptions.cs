namespace Voxa.Speech.Azure;

/// <summary>
/// Configuration shared by the Azure Speech STT and TTS engines.
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

    /// <summary>Sample rate of audio fed into STT. Voice Live default is 24 kHz; Azure Speech accepts 16 kHz natively.</summary>
    public int InputSampleRate { get; init; } = 16000;

    /// <summary>Channel count of audio fed into STT.</summary>
    public int InputChannels { get; init; } = 1;

    /// <summary>Sample rate of audio emitted by TTS.</summary>
    public int OutputSampleRate { get; init; } = 24000;
}
