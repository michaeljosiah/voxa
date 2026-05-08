using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Voxa.Frames;
using Voxa.Processors;
using Voxa.Services.AzureSpeech.Engines;

namespace Voxa.Services.AzureSpeech;

/// <summary>
/// Streaming speech-to-text processor. Pipes inbound <see cref="AudioRawFrame"/>s into an
/// <see cref="ISpeechToTextEngine"/> and emits <see cref="TranscriptionFrame"/>s — interim and
/// final — for downstream consumers (typically a <c>MicrosoftAgentsProcessor</c>).
/// </summary>
public sealed class AzureSpeechSttProcessor : FrameProcessor
{
    private readonly Func<ISpeechToTextEngine> _engineFactory;
    private readonly ILogger<AzureSpeechSttProcessor> _logger;
    private ISpeechToTextEngine? _engine;
    private Task? _readLoop;

    /// <summary>Construct with a default <see cref="AzureSpeechToTextEngine"/> built from <paramref name="options"/>.</summary>
    public AzureSpeechSttProcessor(AzureSpeechOptions options, ILogger<AzureSpeechSttProcessor>? logger = null)
        : this(() => new AzureSpeechToTextEngine(options), logger) { }

    /// <summary>Construct with a custom engine factory — useful for tests.</summary>
    public AzureSpeechSttProcessor(Func<ISpeechToTextEngine> engineFactory, ILogger<AzureSpeechSttProcessor>? logger = null)
        : base("AzureSpeechStt")
    {
        _engineFactory = engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
        _logger = logger ?? NullLogger<AzureSpeechSttProcessor>.Instance;
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
        if (_engine is not null && frame is AudioRawFrame audio)
        {
            // Audio is consumed by STT; transcriptions are emitted by the read loop instead.
            await _engine.WriteAudioAsync(audio.Pcm, ct).ConfigureAwait(false);
            return;
        }

        // Forward control + non-audio frames so Start/End/etc. reach the sink and the runner can complete.
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
            _logger.LogError(ex, "AzureSpeechSttProcessor: STT engine read loop failed");
            await PushErrorAsync($"STT engine failed: {ex.Message}", ex, ct).ConfigureAwait(false);
        }
    }
}
