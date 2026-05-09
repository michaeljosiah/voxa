using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Voxa.Frames;
using Voxa.Processors;

namespace Voxa.Speech;

/// <summary>
/// Vendor-neutral STT processor. Pipes inbound <see cref="AudioRawFrame"/>s into an
/// <see cref="ISpeechToTextEngine"/> and emits <see cref="TranscriptionFrame"/>s for downstream.
/// Pair with a vendor engine from <c>Voxa.Speech.Azure</c>, <c>Voxa.Speech.OpenAI</c>, etc.
/// </summary>
public sealed class SpeechToTextProcessor : FrameProcessor
{
    private readonly Func<ISpeechToTextEngine> _engineFactory;
    private readonly ILogger _logger;
    private ISpeechToTextEngine? _engine;
    private Task? _readLoop;

    /// <summary>Construct with an existing engine instance (one-shot use).</summary>
    public SpeechToTextProcessor(ISpeechToTextEngine engine, ILogger? logger = null)
        : this(() => engine, logger) { }

    /// <summary>Construct with a factory — engine is created on Start, disposed on End.</summary>
    public SpeechToTextProcessor(Func<ISpeechToTextEngine> engineFactory, ILogger? logger = null)
        : base("SpeechToText")
    {
        _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
        _logger = logger ?? NullLogger.Instance;
    }

    protected override async ValueTask OnStartAsync(StartFrame frame, CancellationToken ct)
    {
        _engine = _engineFactory();
        await _engine.StartAsync(ct).ConfigureAwait(false);
        _readLoop = Task.Run(() => ReadLoopAsync(ct));
    }

    protected override async ValueTask OnEndAsync(EndFrame frame, CancellationToken ct)
    {
        if (_engine is not null)
        {
            try { await _engine.StopAsync().ConfigureAwait(false); } catch { /* best-effort */ }
            await _engine.DisposeAsync().ConfigureAwait(false);
            _engine = null;
        }

        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { }
            _readLoop = null;
        }
    }

    protected override async ValueTask ProcessFrameAsync(Frame frame, CancellationToken ct)
    {
        if (_engine is not null)
        {
            if (frame is AudioRawFrame audio)
            {
                // Audio is consumed by STT; transcriptions come back via the read loop.
                await _engine.WriteAudioAsync(audio.Pcm, ct).ConfigureAwait(false);
                return;
            }

            if (frame is UserStoppedSpeakingFrame)
            {
                // Speech-end signal — drain whatever the batch engine has buffered immediately
                // instead of waiting for its periodic timer. The frame still flows downstream
                // (transports may want to surface it).
                try { await _engine.FlushAsync().ConfigureAwait(false); }
                catch (Exception ex) { _logger.LogWarning(ex, "SpeechToTextProcessor: engine FlushAsync threw"); }
            }
        }

        // Forward control + non-audio frames so Start/End/speaking events reach the sink.
        await PushFrameAsync(frame, ct).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        if (_engine is null) return;
        try
        {
            await foreach (var t in _engine.ReadTranscriptsAsync(ct).ConfigureAwait(false))
            {
                await PushFrameAsync(new TranscriptionFrame(t.Text, t.IsFinal, t.Language), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SpeechToTextProcessor: STT engine read loop failed");
            await PushErrorAsync($"STT engine failed: {ex.Message}", ex, ct).ConfigureAwait(false);
        }
    }
}
