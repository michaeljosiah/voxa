namespace Voxa.Speech.Mistral;

/// <summary>One-line factory for Mistral TTS wrapped in a Voxa processor.</summary>
public static class Mistral
{
    /// <summary>Build a <see cref="TextToSpeechProcessor"/> backed by <see cref="MistralTextToSpeechEngine"/>.</summary>
    public static TextToSpeechProcessor Synthesis(MistralSpeechOptions options, HttpClient? httpClient = null)
        => new(() => new MistralTextToSpeechEngine(options, httpClient), options.OutputSampleRate);
}
