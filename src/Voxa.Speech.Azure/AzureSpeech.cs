namespace Voxa.Speech.Azure;

/// <summary>One-line factories for Azure Speech engines wrapped in Voxa processors.</summary>
public static class AzureSpeech
{
    /// <summary>
    /// Build a <see cref="SpeechToTextProcessor"/> backed by <see cref="AzureSpeechToTextEngine"/>.
    /// </summary>
    public static SpeechToTextProcessor StreamingTranscription(AzureSpeechOptions options)
        => new(() => new AzureSpeechToTextEngine(options));

    /// <summary>
    /// Build a <see cref="TextToSpeechProcessor"/> backed by <see cref="AzureTextToSpeechEngine"/>.
    /// </summary>
    public static TextToSpeechProcessor Synthesis(AzureSpeechOptions options)
        => new(() => new AzureTextToSpeechEngine(options), options.OutputSampleRate);
}
