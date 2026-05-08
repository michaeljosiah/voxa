using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Voxa.Frames;
using Voxa.Processors;
using Voxa.Services.AzureSpeech.Engines;

namespace Voxa.Services.AzureSpeech;

/// <summary>
/// Streaming text-to-speech processor. On each <see cref="TextFrame"/> or
/// <see cref="LlmTextChunkFrame"/>, runs the <see cref="ITextToSpeechEngine"/> and emits a
/// <see cref="BotStartedSpeakingFrame"/>, the resulting <see cref="AudioRawFrame"/> chunks, then
/// a <see cref="BotStoppedSpeakingFrame"/>.
/// </summary>
public sealed class AzureSpeechTtsProcessor : FrameProcessor
{
    private readonly Func<ITextToSpeechEngine> _engineFactory;
    private readonly int _outputSampleRate;
    private readonly ILogger<AzureSpeechTtsProcessor> _logger;
    private ITextToSpeechEngine? _engine;

    /// <summary>Construct with a default <see cref="AzureTextToSpeechEngine"/> built from <paramref name="options"/>.</summary>
    public AzureSpeechTtsProcessor(AzureSpeechOptions options, ILogger<AzureSpeechTtsProcessor>? logger = null)
        : this(() => new AzureTextToSpeechEngine(options), options.OutputSampleRate, logger) { }

    /// <summary>Construct with a custom engine factory — useful for tests.</summary>
    public AzureSpeechTtsProcessor(
        Func<ITextToSpeechEngine> engineFactory,
        int outputSampleRate = 24000,
        ILogger<AzureSpeechTtsProcessor>? logger = null)
        : base("AzureSpeechTts")
    {
        _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
        _outputSampleRate = outputSampleRate > 0 ? outputSampleRate : throw new ArgumentOutOfRangeException(nameof(outputSampleRate));
        _logger = logger ?? NullLogger<AzureSpeechTtsProcessor>.Instance;
    }

    protected override async ValueTask OnStartAsync(StartFrame frame, CancellationToken ct)
    {
        _engine = _engineFactory();
        await _engine.StartAsync(ct).ConfigureAwait(false);
    }

    protected override async ValueTask OnEndAsync(EndFrame frame, CancellationToken ct)
    {
        if (_engine is not null)
        {
            await _engine.DisposeAsync().ConfigureAwait(false);
            _engine = null;
        }
    }

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        if (_engine is not null)
        {
            switch (frame)
            {
                case TextFrame txt when !string.IsNullOrWhiteSpace(txt.Text):
                    await SynthesizeAsync(txt.Text, ct).ConfigureAwait(false);
                    return;
                case LlmTextChunkFrame chunk when !string.IsNullOrWhiteSpace(chunk.Text):
                    await SynthesizeAsync(chunk.Text, ct).ConfigureAwait(false);
                    return;
            }
        }

        // Forward everything else (StartFrame, EndFrame, transcriptions, tool calls, …) downstream.
        await PushFrameAsync(frame, ct).ConfigureAwait(false);
    }

    private async Task SynthesizeAsync(string text, CancellationToken ct)
    {
        if (_engine is null) return;

        await PushFrameAsync(new BotStartedSpeakingFrame(), ct).ConfigureAwait(false);
        try
        {
            await foreach (var pcm in _engine.SynthesizeAsync(text, ct).ConfigureAwait(false))
            {
                await PushFrameAsync(new AudioRawFrame(pcm, _outputSampleRate, 1), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AzureSpeechTtsProcessor: synthesis failed");
            await PushErrorAsync($"TTS engine failed: {ex.Message}", ex, ct).ConfigureAwait(false);
        }
        finally
        {
            await PushFrameAsync(new BotStoppedSpeakingFrame(), ct).ConfigureAwait(false);
        }
    }
}
