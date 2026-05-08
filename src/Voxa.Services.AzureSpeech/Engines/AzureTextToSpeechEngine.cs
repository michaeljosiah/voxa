using System.Runtime.CompilerServices;
using Microsoft.CognitiveServices.Speech;

namespace Voxa.Services.AzureSpeech.Engines;

/// <summary>
/// Default <see cref="ITextToSpeechEngine"/> implementation backed by the Microsoft Cognitive
/// Services Speech SDK. Synthesizes to raw 24 kHz 16-bit mono PCM by default and yields the
/// audio in fixed-size chunks so consumers can stream to a transport while synthesis continues.
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

        // Pass null AudioConfig — we don't want the SDK rendering to a speaker, we want the bytes.
        _synthesizer = new SpeechSynthesizer(_speechConfig, audioConfig: null);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<byte[]> SynthesizeAsync(
        string text,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (_synthesizer is null) yield break;
        if (string.IsNullOrWhiteSpace(text)) yield break;

        using var result = await _synthesizer.SpeakTextAsync(text).ConfigureAwait(false);
        if (result.Reason != ResultReason.SynthesizingAudioCompleted) yield break;

        var data = result.AudioData;
        if (data.Length == 0) yield break;

        for (int i = 0; i < data.Length; i += ChunkSize)
        {
            ct.ThrowIfCancellationRequested();
            int len = Math.Min(ChunkSize, data.Length - i);
            var chunk = new byte[len];
            Array.Copy(data, i, chunk, 0, len);
            yield return chunk;
        }
    }

    public ValueTask DisposeAsync()
    {
        _synthesizer?.Dispose();
        return ValueTask.CompletedTask;
    }
}
