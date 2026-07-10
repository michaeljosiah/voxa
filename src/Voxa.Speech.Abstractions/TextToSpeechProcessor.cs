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

    /// <summary>The sentence being synthesized right now — cancelled from the SYSTEM loop on
    /// barge-in while the data loop is mid-enumeration (same benign-race pattern as
    /// <c>FrameProcessor._currentFrameCts</c>).</summary>
    private volatile CancellationTokenSource? _synthCts;

    /// <summary>Barge-in mute: after the user talks over the bot, this turn's remaining text is
    /// stale — drop it (not spoken, not rendered) until the next <see cref="LlmTurnStartedFrame"/>
    /// opens a fresh turn. Without this, text queued behind the interruption re-synthesizes into
    /// the sink's NEW epoch and the interrupted answer audibly resumes.</summary>
    private volatile bool _muted;

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
        switch (frame)
        {
            // Barge-in (VRT-002 WS2). These arrive on the SYSTEM loop, concurrent with a synthesis
            // running on the data loop — abort the sentence mid-flight and mute the turn's stale
            // tail. InterruptionFrame is the loop's explicit barge-in; a bare UserStartedSpeaking
            // covers the turn-tail case (turn already ended, last sentence still synthesizing),
            // matching the sink's pre-existing epoch bump on the same frame.
            case InterruptionFrame:
            case UserStartedSpeakingFrame:
                _muted = true;
                AbortInFlightSynthesis();
                break; // still forwarded below

            case LlmTurnStartedFrame:
                _muted = false; // a fresh turn's text is not the stale tail
                break;

            case TranscriptionFrame { IsFinal: true } final when !string.IsNullOrWhiteSpace(final.Text):
                // Data-ordered unmute for chains without turn-lifecycle frames: the barge-in
                // utterance's final always queues BEHIND the stale chunks and AHEAD of any new
                // response text, so reopening here can't leak the cancelled turn. (By the time the
                // final arrives — an utterance later — a cancelled driver has long stopped yielding.)
                _muted = false;
                break;
        }

        if (_engine is not null)
        {
            switch (frame)
            {
                case TextFrame txt when !string.IsNullOrWhiteSpace(txt.Text):
                    if (_muted) return; // stale text from an interrupted turn — not rendered, not spoken
                    // Forward the text frame downstream BEFORE synthesizing so transports can
                    // render it as soon as it's available — the bot's text appears in the UI
                    // before the audio finishes streaming. Mirrors Pipecat's TTS pattern.
                    await PushFrameAsync(txt, ct).ConfigureAwait(false);
                    await SynthesizeAsync(txt.Text, ct).ConfigureAwait(false);
                    return;
                case LlmTextChunkFrame chunk when !string.IsNullOrWhiteSpace(chunk.Text):
                    if (_muted) return;
                    await PushFrameAsync(chunk, ct).ConfigureAwait(false);
                    await SynthesizeAsync(chunk.Text, ct).ConfigureAwait(false);
                    return;
            }
        }

        // Forward everything else (Start/End, transcriptions, tool calls, …) downstream.
        await PushFrameAsync(frame, ct).ConfigureAwait(false);
    }

    private void AbortInFlightSynthesis()
    {
        var inFlight = _synthCts;
        if (inFlight is null) return;
        try { inFlight.Cancel(); }
        catch (ObjectDisposedException) { /* the sentence finished as we looked */ }
    }

    private async Task SynthesizeAsync(string text, CancellationToken ct)
    {
        if (_engine is null) return;

        // Linked so barge-in (system loop) can abort THIS sentence without touching the frame token.
        using var synthCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _synthCts = synthCts;
        await PushFrameAsync(new BotStartedSpeakingFrame(), ct).ConfigureAwait(false);
        try
        {
            await foreach (var pcm in _engine.SynthesizeAsync(text, synthCts.Token).ConfigureAwait(false))
            {
                // Engine memory is transient (pooled, valid only until the next MoveNext); the frame
                // needs its own copy with frame lifetime as it flows downstream asynchronously.
                await PushFrameAsync(new AudioRawFrame(pcm.ToArray(), _outputSampleRate, 1), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* barge-in abort or shutdown — stop cleanly either way */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TextToSpeechProcessor: synthesis failed");
            await PushErrorAsync($"TTS engine failed: {ex.Message}", ex, ct).ConfigureAwait(false);
        }
        finally
        {
            _synthCts = null;
            await PushFrameAsync(new BotStoppedSpeakingFrame(), ct).ConfigureAwait(false);
        }
    }
}
