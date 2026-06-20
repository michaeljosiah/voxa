using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Speech;

/// <summary>
/// Vendor-neutral TTS processor. On each <see cref="TextFrame"/> or
/// <see cref="LlmTextChunkFrame"/>, runs an <see cref="ITextToSpeechEngine"/> and emits a
/// <see cref="BotStartedSpeakingFrame"/>, the resulting <see cref="AudioRawFrame"/> chunks,
/// then a <see cref="BotStoppedSpeakingFrame"/>.
/// </summary>
public sealed class TextToSpeechProcessor : FrameProcessor
{
    private readonly Func<ITextToSpeechEngine> _engineFactory;
    private readonly int _outputSampleRate;
    private readonly ILogger _logger;
    private ITextToSpeechEngine? _engine;

    /// <summary>Construct with an existing engine instance (one-shot use).</summary>
    public TextToSpeechProcessor(ITextToSpeechEngine engine, int outputSampleRate = 24000, ILogger? logger = null)
        : this(() => engine, outputSampleRate, logger) { }

    /// <summary>Construct with a factory and the output sample rate vendor engines yield.</summary>
    public TextToSpeechProcessor(
        Func<ITextToSpeechEngine> engineFactory,
        int outputSampleRate = 24000,
        ILogger? logger = null)
        : base("TextToSpeech")
    {
        _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
        _outputSampleRate = outputSampleRate > 0 ? outputSampleRate : throw new ArgumentOutOfRangeException(nameof(outputSampleRate));
        _logger = logger ?? NullLogger.Instance;
    }

    protected override async ValueTask OnStartAsync(StartFrame frame, CancellationToken ct)
    {
        _engine = _engineFactory();
        await _engine.StartAsync(ct).ConfigureAwait(false);
    }

    protected override ValueTask OnEndAsync(EndFrame frame, CancellationToken ct) => DisposeEngineAsync();

    // Release the engine on the actual disposal path too (CQ-003): an abrupt teardown (client disconnect, no
    // EndFrame) would otherwise leak it. Idempotent (null-out), and runs after the loops stop so it never
    // races OnEndAsync — the graceful path calls it twice, the second a no-op.
    protected override ValueTask DisposeAsyncCore() => DisposeEngineAsync();

    private async ValueTask DisposeEngineAsync()
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
                    // Forward the text frame downstream BEFORE synthesizing so transports can
                    // render it as soon as it's available — the bot's text appears in the UI
                    // before the audio finishes streaming. Mirrors Pipecat's TTS pattern.
                    await PushFrameAsync(txt, ct).ConfigureAwait(false);
                    await SynthesizeAsync(txt.Text, ct).ConfigureAwait(false);
                    return;
                case LlmTextChunkFrame chunk when !string.IsNullOrWhiteSpace(chunk.Text):
                    await PushFrameAsync(chunk, ct).ConfigureAwait(false);
                    await SynthesizeAsync(chunk.Text, ct).ConfigureAwait(false);
                    return;
            }
        }

        // Forward everything else (Start/End, transcriptions, tool calls, …) downstream.
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
                // Engine memory is transient (pooled, valid only until the next MoveNext); the frame
                // needs its own copy with frame lifetime as it flows downstream asynchronously.
                await PushFrameAsync(new AudioRawFrame(pcm.ToArray(), _outputSampleRate, 1), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TextToSpeechProcessor: synthesis failed");
            await PushErrorAsync($"TTS engine failed: {ex.Message}", ex, ct).ConfigureAwait(false);
        }
        finally
        {
            await PushFrameAsync(new BotStoppedSpeakingFrame(), ct).ConfigureAwait(false);
        }
    }
}
