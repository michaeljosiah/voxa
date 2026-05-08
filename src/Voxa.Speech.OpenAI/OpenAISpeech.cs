namespace Voxa.Speech.OpenAI;

/// <summary>One-line factories for OpenAI engines wrapped in Voxa processors.</summary>
public static class OpenAISpeech
{
    /// <summary>Build a <see cref="SpeechToTextProcessor"/> backed by <see cref="OpenAIWhisperEngine"/>.</summary>
    public static SpeechToTextProcessor StreamingTranscription(OpenAISpeechOptions options, HttpClient? httpClient = null)
        => new(() => new OpenAIWhisperEngine(options, httpClient));

    /// <summary>Build a <see cref="TextToSpeechProcessor"/> backed by <see cref="OpenAITextToSpeechEngine"/>.</summary>
    public static TextToSpeechProcessor Synthesis(OpenAISpeechOptions options, HttpClient? httpClient = null)
        => new(() => new OpenAITextToSpeechEngine(options, httpClient), options.OutputSampleRate);
}
