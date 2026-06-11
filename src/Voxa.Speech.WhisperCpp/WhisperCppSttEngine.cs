using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Whisper.net;

namespace Voxa.Speech.WhisperCpp;

/// <summary>
/// Local speech-to-text over whisper.cpp (VLS-001 WS1). Whisper is an utterance transcriber, not
/// a streaming recognizer — which matches Voxa's pipeline shape exactly: the VAD gates audio so
/// only speech reaches this engine, and <see cref="SpeechToTextProcessor"/> calls
/// <see cref="FlushAsync"/> on <c>UserStoppedSpeakingFrame</c>. The engine buffers PCM between
/// flushes and transcribes once per utterance, emitting final-only results (no interims in v1).
///
/// <para>
/// Model weights load once per process: <see cref="WhisperFactory"/> instances are cached per
/// model path (~75–490 MB each); the per-connection state is just a cheap
/// <see cref="WhisperProcessor"/>. Transcription runs on worker threads chained one-after-another
/// (whisper.cpp is CPU-bound native code) so <see cref="FlushAsync"/> returns immediately and the
/// pipeline data loop never blocks on inference.
/// </para>
/// </summary>
public sealed class WhisperCppSttEngine : ISpeechToTextEngine
{
    /// <summary>Whisper models are trained on 16 kHz mono — a hard requirement, not a default.</summary>
    public const int RequiredSampleRate = 16000;

    /// <summary>Whisper's context window. Longer buffers are transcribed in 30 s slices.</summary>
    private const int MaxBufferedSamples = 30 * RequiredSampleRate;

    /// <summary>Flushes below ~0.3 s are VAD blips — transcribing them invites hallucinated text.</summary>
    private const int MinUtteranceSamples = (int)(0.3 * RequiredSampleRate);

    // One factory (= one copy of the model weights) per model path, process-wide. Lazy so two
    // connections racing on first use don't both pay the load.
    private static readonly ConcurrentDictionary<string, Lazy<WhisperFactory>> Factories = new();

    private readonly WhisperCppOptions _options;
    private readonly VoxaModelCache? _cache;
    private readonly ILogger _logger;
    private readonly Func<float[], CancellationToken, Task<string>>? _transcriberSeam;

    private readonly Channel<TranscriptionResult> _transcripts =
        Channel.CreateUnbounded<TranscriptionResult>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();

    private float[] _buffer = new float[RequiredSampleRate * 4]; // 4 s initial; grows ×2
    private int _buffered;
    private Task _transcriptions = Task.CompletedTask; // chained — serialized by construction
    private WhisperProcessor? _processor;
    private bool _stopped;

    /// <summary>Create an engine that resolves its model through <paramref name="cache"/> on Start.</summary>
    public WhisperCppSttEngine(WhisperCppOptions options, VoxaModelCache cache, ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Test seam: bypasses model resolution and whisper.cpp entirely; <paramref name="transcriber"/>
    /// receives exactly the float samples a real transcription would.
    /// </summary>
    internal WhisperCppSttEngine(
        WhisperCppOptions options,
        Func<float[], CancellationToken, Task<string>> transcriber,
        ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _transcriberSeam = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
        _logger = logger ?? NullLogger.Instance;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_transcriberSeam is not null || _processor is not null) return;

        string modelPath;
        if (!string.IsNullOrEmpty(_options.ModelPath))
        {
            modelPath = _options.ModelPath;
            if (!File.Exists(modelPath))
                throw new VoxaModelUnavailableException(
                    $"Voxa:WhisperCpp:ModelPath is set to '{modelPath}' but no file exists there.");
        }
        else
        {
            if (!WhisperCppModelCatalog.TryGet(_options.Model, out var artifact))
                throw new VoxaModelUnavailableException(
                    $"Unknown Voxa:WhisperCpp:Model '{_options.Model}'. Known models: " +
                    $"{string.Join(", ", WhisperCppModelCatalog.KnownModels)}. " +
                    "Or set Voxa:WhisperCpp:ModelPath to a GGML file of your own.");
            // Normally a cache hit — the startup guard's eager warm-up already resolved it.
            modelPath = await _cache!.ResolveAsync(artifact, ct).ConfigureAwait(false);
        }

        var factory = Factories.GetOrAdd(
            Path.GetFullPath(modelPath),
            p => new Lazy<WhisperFactory>(() => WhisperFactory.FromPath(p))).Value;

        var builder = factory.CreateBuilder()
            .WithThreads(_options.Threads ?? Math.Min(4, Environment.ProcessorCount));
        builder = _options.AutoDetectLanguage
            ? builder.WithLanguageDetection()
            : builder.WithLanguage(_options.Language!);
        if (_options.Translate) builder = builder.WithTranslate();

        _processor = builder.Build();
        _logger.LogInformation(
            "WhisperCpp STT ready: model {Model}, language {Language}",
            Path.GetFileName(modelPath), _options.AutoDetectLanguage ? "(auto)" : _options.Language);
    }

    public ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        if (pcm.IsEmpty) return ValueTask.CompletedTask;

        lock (_gate)
        {
            if (_stopped) return ValueTask.CompletedTask;

            AppendPcm16(pcm.Span);

            // A 30 s monologue between VAD stops is rare but legal: transcribe the full window as
            // its own final segment and keep buffering — never drop audio silently.
            if (_buffered >= MaxBufferedSamples)
            {
                _logger.LogWarning(
                    "WhisperCpp STT: utterance exceeded the 30 s whisper context window; transcribing the buffered window and continuing");
                QueueTranscriptionLocked();
            }
        }
        return ValueTask.CompletedTask;
    }

    public Task FlushAsync()
    {
        lock (_gate)
        {
            if (!_stopped) QueueTranscriptionLocked();
        }
        // Intentionally does NOT await the transcription: SpeechToTextProcessor calls this inline
        // on its data loop, and inference takes RTF×duration. Results arrive via the channel.
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync(CancellationToken ct)
        => _transcripts.Reader.ReadAllAsync(ct);

    public async Task StopAsync()
    {
        Task pending;
        lock (_gate)
        {
            if (_stopped) return;
            QueueTranscriptionLocked(); // implicit final flush
            _stopped = true;
            pending = _transcriptions;
        }
        try { await pending.ConfigureAwait(false); } catch { /* logged in the worker */ }
        _transcripts.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifetime.Cancel();
        if (_processor is not null)
        {
            await _processor.DisposeAsync().ConfigureAwait(false);
            _processor = null;
        }
        _lifetime.Dispose();
        // The WhisperFactory stays in the process-wide cache deliberately: it holds the model
        // weights shared by every other connection on this host.
    }

    // ── internals ───────────────────────────────────────────────────────────

    private void AppendPcm16(ReadOnlySpan<byte> pcm)
    {
        var samples = pcm.Length / 2;
        if (_buffered + samples > _buffer.Length)
        {
            var newSize = Math.Max(_buffer.Length * 2, _buffered + samples);
            Array.Resize(ref _buffer, newSize);
        }
        for (int i = 0; i < samples; i++)
        {
            _buffer[_buffered + i] =
                BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2)) / 32768f;
        }
        _buffered += samples;
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private void QueueTranscriptionLocked()
    {
        if (_buffered < MinUtteranceSamples) return; // VAD blip — keep buffering, don't hallucinate

        var samples = new float[_buffered];
        Array.Copy(_buffer, samples, _buffered);
        _buffered = 0;

        // Chain onto the previous transcription: utterances stay ordered and inference is
        // serialized without holding any lock during the (long) native call.
        var previous = _transcriptions;
        var ct = _lifetime.Token;
        _transcriptions = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            await TranscribeAndEmitAsync(samples, ct).ConfigureAwait(false);
        }, CancellationToken.None);
    }

    private async Task TranscribeAndEmitAsync(float[] samples, CancellationToken ct)
    {
        try
        {
            string text;
            if (_transcriberSeam is not null)
            {
                text = await _transcriberSeam(samples, ct).ConfigureAwait(false);
            }
            else if (_processor is not null)
            {
                var sb = new StringBuilder();
                await foreach (var segment in _processor.ProcessAsync(samples, ct).ConfigureAwait(false))
                    sb.Append(segment.Text);
                text = sb.ToString();
            }
            else
            {
                return; // StartAsync never ran — nothing to transcribe with
            }

            text = text.Trim();
            if (text.Length == 0) return;

            await _transcripts.Writer
                .WriteAsync(new TranscriptionResult(text, IsFinal: true,
                    Language: _options.AutoDetectLanguage ? null : _options.Language), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* engine shutting down */ }
        catch (ChannelClosedException) { /* stopped while emitting */ }
        catch (Exception ex)
        {
            // Per-utterance failure must not kill the session: log and keep the engine alive for
            // the next utterance (parity with cloud engines' per-request failure handling).
            _logger.LogError(ex, "WhisperCpp STT: transcription failed for a {Seconds:F1}s utterance",
                samples.Length / (double)RequiredSampleRate);
        }
    }
}
