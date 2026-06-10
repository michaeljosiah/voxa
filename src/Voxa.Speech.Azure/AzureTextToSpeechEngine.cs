using System.Runtime.CompilerServices;
using Microsoft.CognitiveServices.Speech;

namespace Voxa.Speech.Azure;

/// <summary>
/// <see cref="ITextToSpeechEngine"/> implementation backed by the Microsoft Cognitive Services
/// Speech SDK. Synthesises to raw 24 kHz 16-bit mono PCM and streams the audio incrementally via
/// <see cref="AudioDataStream"/> as the service produces it — first chunk in ~100–200 ms instead
/// of after the whole utterance is synthesized.
/// </summary>
public sealed class AzureTextToSpeechEngine : ITextToSpeechEngine
{
    private const int ChunkSize = 8 * 1024;

    private readonly AzureSpeechOptions _options;
    private SpeechConfig? _speechConfig;
    private SpeechSynthesizer? _synthesizer;

    public AzureTextToSpeechEngine(AzureSpeechOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task StartAsync(CancellationToken ct)
    {
        _speechConfig = SpeechConfig.FromSubscription(_options.SubscriptionKey, _options.Region);
        _speechConfig.SpeechSynthesisVoiceName = _options.Voice;
        _speechConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Raw24Khz16BitMonoPcm);

        // Pass null AudioConfig — we don't want the SDK rendering to a speaker; we want the bytes.
        _synthesizer = new SpeechSynthesizer(_speechConfig, audioConfig: null);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_synthesizer is null) yield break;
        if (string.IsNullOrWhiteSpace(text)) yield break;

        // StartSpeakingTextAsync returns as soon as synthesis STARTS; AudioDataStream then yields
        // audio incrementally instead of buffering the whole utterance first.
        using var result = await _synthesizer.StartSpeakingTextAsync(text).ConfigureAwait(false);
        if (result.Reason != ResultReason.SynthesizingAudioStarted) yield break;

        using var audioStream = AudioDataStream.FromResult(result);
        var buffer = new byte[ChunkSize];   // reused; consumer copies into its frame (valid-until-MoveNext)
        bool completed = false;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                // ReadData blocks the calling thread until data is available or synthesis ends;
                // hop to the thread pool so we don't block the TTS processor's data loop.
                uint read = await Task.Run(() => audioStream.ReadData(buffer), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    completed = true;
                    yield break;
                }
                yield return buffer.AsMemory(0, (int)read);
            }
        }
        finally
        {
            // On cancellation (barge-in), tell the service to stop synthesizing — best-effort.
            if (!completed)
            {
                try { await _synthesizer.StopSpeakingAsync().ConfigureAwait(false); }
                catch { /* best-effort */ }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _synthesizer?.Dispose();
        return ValueTask.CompletedTask;
    }
}
