namespace Voxa.Speech.ElevenLabs;

/// <summary>One-line factory for ElevenLabs TTS wrapped in a Voxa processor.</summary>
public static class ElevenLabs
{
    /// <summary>Build a <see cref="TextToSpeechProcessor"/> backed by <see cref="ElevenLabsTextToSpeechEngine"/>.</summary>
    public static TextToSpeechProcessor Synthesis(ElevenLabsOptions options, HttpClient? httpClient = null)
        => new(() => new ElevenLabsTextToSpeechEngine(options, httpClient), options.OutputSampleRate);
}
