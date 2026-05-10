using Voxa.Speech;

namespace Voxa.AspNetCore;

/// <summary>
/// Convenience fluent methods for the speech processors that ship with Voxa
/// (<see cref="SpeechToTextProcessor"/>, <see cref="TextToSpeechProcessor"/>,
/// <see cref="SentenceAggregator"/>). These accept factories so each WebSocket connection
/// gets a fresh processor instance — Voxa processors hold per-connection state and aren't safe
/// to share across pipelines.
/// </summary>
public static class SpeechBuilderExtensions
{
    /// <summary>
    /// Add a speech-to-text stage to the pipeline. The factory is invoked per WebSocket
    /// connection, e.g. <c>() =&gt; OpenAISpeech.StreamingTranscription(opts)</c>.
    /// </summary>
    public static VoicePipelineBuilder UseSpeechToText(
        this VoicePipelineBuilder builder,
        Func<SpeechToTextProcessor> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return builder.UseProcessor(() => factory());
    }

    /// <summary>
    /// Add a text-to-speech stage to the pipeline. The factory is invoked per WebSocket
    /// connection, e.g. <c>() =&gt; OpenAISpeech.Synthesis(opts)</c>.
    /// </summary>
    public static VoicePipelineBuilder UseTextToSpeech(
        this VoicePipelineBuilder builder,
        Func<TextToSpeechProcessor> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return builder.UseProcessor(() => factory());
    }

    /// <summary>
    /// Add a sentence aggregator that gathers <c>LlmTextChunkFrame</c>s into sentence-sized
    /// <c>TextFrame</c>s for downstream TTS / normalization. Idiomatically placed between the
    /// agent stage and TTS.
    /// </summary>
    public static VoicePipelineBuilder UseSentenceAggregator(this VoicePipelineBuilder builder)
        => builder.UseProcessor(() => new SentenceAggregator());

    /// <summary>
    /// Add a transcription filter that drops STT hallucinations on near-silent audio
    /// (configured exact + substring blocklists, default focused on Whisper artifacts).
    /// </summary>
    public static VoicePipelineBuilder UseTranscriptionFilter(this VoicePipelineBuilder builder)
        => builder.UseProcessor(() => new TranscriptionFilter());

    /// <summary>
    /// Add a silence gate (energy-based VAD) for pre-STT audio gating. Use this when running
    /// against an STT engine that doesn't have its own VAD.
    /// </summary>
    public static VoicePipelineBuilder UseSilenceGate(this VoicePipelineBuilder builder)
        => builder.UseProcessor(() => new SilenceGateProcessor());
}
