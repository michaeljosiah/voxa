using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Voxa.Audio.Onnx;

namespace Voxa.Speech.Kokoro;

/// <summary>
/// Local high-quality text-to-speech over Kokoro-82M on ONNX Runtime, in-process (VLS-001 WS3).
/// Per-sentence synthesis — <c>SentenceAggregator</c> upstream guarantees that unit — with the
/// 24 kHz PCM yielded in small chunks so the sink's pacing and barge-in flush behave identically
/// to cloud TTS. Only phonemization leaves the process (a stateless per-sentence espeak-ng CLI
/// call); there is no long-lived child process and no orphan surface at all.
///
/// <para>
/// Model weights load once per process through the shared <see cref="OnnxModelHost"/>: the
/// <see cref="InferenceSession"/> is cached per <c>(model path, device)</c> on the host's process-wide cache
/// (so cpu and a GPU device are distinct entries), with a shared semaphore capping parallel runs
/// (<see cref="KokoroOptions.MaxConcurrentSyntheses"/>) so synthesis can't starve the audio pipeline's cores.
/// <see cref="KokoroOptions.Device"/> selects the execution provider; an explicit GPU device whose runtime
/// isn't loaded fails loud at <see cref="StartAsync"/> per the host's device contract.
/// </para>
/// </summary>
public sealed class KokoroTtsEngine : ITextToSpeechEngine
{
    /// <summary>~4096 samples per yielded chunk (8 KiB of PCM16) — barge-in stops between chunks.</summary>
    private const int ChunkBytes = 4096 * 2;

    private sealed record SharedSession(
        InferenceSession Session,
        SemaphoreSlim Gate,
        string InputIdsName,
        string StyleName,
        string SpeedName,
        string OutputName);

    // The InferenceSession itself is owned + cached by OnnxModelHost (per (path, device)); this caches only
    // the run gate per (path, device) so the parallel-run cap is process-wide across every connection — NOT
    // the session, so OnnxModelHost.EvictAll() ("unload models") stays correct and a later StartAsync simply
    // re-loads from the host.
    private static readonly ConcurrentDictionary<(string Path, OnnxDevice Device), SemaphoreSlim> Gates = new();

    private readonly KokoroOptions _options;
    private readonly VoxaModelCache? _cache;
    private readonly OnnxModelHost _host = new();
    private readonly ILogger _logger;

    // Test seams: G2P and inference are independently fakeable.
    private Func<string, CancellationToken, Task<string>>? _phonemize;
    private Func<long[], CancellationToken, Task<float[]>>? _infer;

    public KokoroTtsEngine(KokoroOptions options, VoxaModelCache cache, ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? NullLogger.Instance;
    }

    internal KokoroTtsEngine(
        KokoroOptions options,
        Func<string, CancellationToken, Task<string>> phonemize,
        Func<long[], CancellationToken, Task<float[]>> infer)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _phonemize = phonemize ?? throw new ArgumentNullException(nameof(phonemize));
        _infer = infer ?? throw new ArgumentNullException(nameof(infer));
        _logger = NullLogger.Instance;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_infer is not null) return; // test seam or already started

        // Model: explicit path or catalog-by-precision → cache.
        string modelPath;
        if (!string.IsNullOrEmpty(_options.ModelPath))
        {
            modelPath = _options.ModelPath;
            if (!File.Exists(modelPath))
                throw new VoxaModelUnavailableException(
                    $"Voxa:Kokoro:ModelPath is set to '{modelPath}' but no file exists there.");
        }
        else
        {
            if (!KokoroCatalog.TryGetModel(_options.Precision, out var model))
                throw new VoxaModelUnavailableException(
                    $"Unknown Voxa:Kokoro:Precision '{_options.Precision}'. " +
                    $"Valid values: {string.Join(", ", KokoroCatalog.KnownPrecisions)}.");
            modelPath = await _cache!.ResolveAsync(model, ct).ConfigureAwait(false);
        }

        // Voice style vectors: raw float32, 510 rows × 256.
        string voicePath;
        if (!string.IsNullOrEmpty(_options.VoicePath))
        {
            voicePath = _options.VoicePath;
            if (!File.Exists(voicePath))
                throw new VoxaModelUnavailableException(
                    $"Voxa:Kokoro:VoicePath is set to '{voicePath}' but no file exists there.");
        }
        else
        {
            if (!KokoroCatalog.TryGetVoice(_options.Voice, out var voice))
                throw new VoxaModelUnavailableException(
                    $"Unknown Voxa:Kokoro:Voice '{_options.Voice}'. Known voices: " +
                    $"{string.Join(", ", KokoroCatalog.KnownVoices)}. " +
                    "Or set Voxa:Kokoro:VoicePath to a style-vector file of your own.");
            voicePath = await _cache!.ResolveAsync(voice, ct).ConfigureAwait(false);
        }
        var styleRows = LoadStyleRows(voicePath);

        // espeak-ng CLI: explicit path → PATH → pinned per-RID download. When resolved from our
        // archive, the espeak data dir sits at <bin>/../share/espeak-ng-data and espeak needs
        // --path pointed at its parent; system installs know their own data.
        string espeakPath;
        string? espeakDataParent = null;
        if (!string.IsNullOrEmpty(_options.EspeakPath))
        {
            espeakPath = _options.EspeakPath;
            if (!File.Exists(espeakPath))
                throw new VoxaModelUnavailableException(
                    $"Voxa:Kokoro:EspeakPath is set to '{espeakPath}' but no file exists there.");
        }
        else if (KokoroCatalog.FindEspeakOnPath() is { } onPath)
        {
            espeakPath = onPath;
        }
        else
        {
            var artifact = KokoroCatalog.EspeakForCurrentPlatform()
                ?? throw new VoxaModelUnavailableException(
                    $"No pinned espeak-ng build exists for this platform ({KokoroCatalog.CurrentRid()}). " +
                    "Install espeak-ng from your package manager and either add it to PATH or set " +
                    "Voxa:Kokoro:EspeakPath.");
            espeakPath = await _cache!.ResolveAsync(artifact, ct).ConfigureAwait(false);
            espeakDataParent = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(espeakPath)!, "..", "share"));
        }
        var phonemizer = new EspeakPhonemizer(espeakPath, espeakDataParent, _options.ResolveEspeakVoice());
        _phonemize = phonemizer.PhonemizeAsync;

        if (!OnnxDeviceParser.TryParse(_options.Device, out var device))
            throw new VoxaModelUnavailableException(
                $"Unknown Voxa:Kokoro:Device '{_options.Device}'. " +
                $"Valid values: {string.Join(", ", OnnxDeviceParser.ValidValues)}.");

        var shared = CreateSession(_host, modelPath, device, _options.MaxConcurrentSyntheses);

        var speed = (float)_options.Speed;
        _infer = (tokens, token) => RunInferenceAsync(shared, tokens, styleRows, speed, token);

        _logger.LogInformation(
            "Kokoro TTS ready: {Precision} model, voice {Voice}, espeak {Espeak}, device {Device}",
            _options.Precision, _options.Voice, Path.GetFileName(espeakPath), device);
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text, [EnumeratorCancellation] CancellationToken ct)
    {
        if (_infer is null || _phonemize is null)
            throw new InvalidOperationException("KokoroTtsEngine.StartAsync must run before SynthesizeAsync.");

        var phonemes = await _phonemize(text, ct).ConfigureAwait(false);
        var tokens = KokoroVocabulary.Tokenize(phonemes);
        if (tokens.Length == 0) yield break;

        // Sentence-level input fits comfortably in 510 tokens; split at phrase boundaries for the
        // rare monster sentence — never truncate silently.
        foreach (var slice in SplitTokens(tokens, KokoroCatalog.MaxTokens))
        {
            var waveform = await _infer(slice, ct).ConfigureAwait(false);
            var pcm = ToPcm16(waveform);
            for (int offset = 0; offset < pcm.Length; offset += ChunkBytes)
            {
                ct.ThrowIfCancellationRequested();
                yield return new ReadOnlyMemory<byte>(pcm, offset, Math.Min(ChunkBytes, pcm.Length - offset));
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        // The InferenceSession stays in the process-wide cache deliberately: it holds the model
        // weights shared by every other connection on this host. espeak runs leave nothing behind.
        return ValueTask.CompletedTask;
    }

    // ── internals ───────────────────────────────────────────────────────────

    private static SharedSession CreateSession(
        OnnxModelHost host, string modelPath, OnnxDevice device, int maxConcurrent)
    {
        // The host applies the device EP (ORT_ENABLE_ALL, as before) and caches the session per (path, device);
        // an explicit GPU device with no loaded provider throws VoxaModelUnavailableException here.
        var loaded = host.Load(modelPath, device);
        var inputs = loaded.InputNames;

        // Tolerate both published export flavors: onnx-community uses "input_ids"/"style"/"speed",
        // kokoro-onnx uses "tokens". Fail loudly with the actual names if neither matches.
        var inputIds = inputs.FirstOrDefault(n => n is "input_ids" or "tokens")
            ?? throw new InvalidOperationException(
                $"Unrecognized Kokoro model inputs [{string.Join(", ", inputs)}] — expected input_ids/tokens, style, speed.");
        var style = inputs.FirstOrDefault(n => n == "style")
            ?? throw new InvalidOperationException(
                $"Kokoro model has no 'style' input (found [{string.Join(", ", inputs)}]).");
        var speed = inputs.FirstOrDefault(n => n == "speed")
            ?? throw new InvalidOperationException(
                $"Kokoro model has no 'speed' input (found [{string.Join(", ", inputs)}]).");

        // One run gate per (path, device), shared across connections (the first caller's cap wins, matching
        // the previous single-Lazy behavior). Key normalized like the host so case-variant paths share a gate.
        var keyPath = OperatingSystem.IsWindows()
            ? Path.GetFullPath(modelPath).ToLowerInvariant()
            : Path.GetFullPath(modelPath);
        var gate = Gates.GetOrAdd(
            (keyPath, device),
            _ => new SemaphoreSlim(Math.Max(1, maxConcurrent), Math.Max(1, maxConcurrent)));

        return new SharedSession(loaded.Session, gate, inputIds, style, speed, loaded.OutputNames[0]);
    }

    private static float[] LoadStyleRows(string voicePath)
    {
        var bytes = File.ReadAllBytes(voicePath);
        if (bytes.Length == 0 || bytes.Length % (KokoroCatalog.StyleDim * sizeof(float)) != 0)
        {
            throw new VoxaModelUnavailableException(
                $"Kokoro voice file '{voicePath}' is not a multiple of {KokoroCatalog.StyleDim} float32s " +
                "— not a Kokoro style-vector file.");
        }
        var floats = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    private static async Task<float[]> RunInferenceAsync(
        SharedSession shared, long[] tokens, float[] styleRows, float speed, CancellationToken ct)
    {
        await shared.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // CPU-bound native code — keep it off the pipeline data loop.
            return await Task.Run(() =>
            {
                // Boundary pads wrap the sequence; the style row is indexed by token count.
                var padded = new long[tokens.Length + 2];
                tokens.CopyTo(padded, 1);

                var rows = styleRows.Length / KokoroCatalog.StyleDim;
                var row = Math.Clamp(tokens.Length - 1, 0, rows - 1);
                var style = new float[KokoroCatalog.StyleDim];
                Array.Copy(styleRows, row * KokoroCatalog.StyleDim, style, 0, KokoroCatalog.StyleDim);

                using var inputIdsValue = OrtValue.CreateTensorValueFromMemory(
                    padded, [1, padded.Length]);
                using var styleValue = OrtValue.CreateTensorValueFromMemory(
                    style, [1, KokoroCatalog.StyleDim]);
                using var speedValue = OrtValue.CreateTensorValueFromMemory(
                    new[] { speed }, [1]);

                var inputs = new Dictionary<string, OrtValue>
                {
                    [shared.InputIdsName] = inputIdsValue,
                    [shared.StyleName] = styleValue,
                    [shared.SpeedName] = speedValue,
                };

                using var runOptions = new RunOptions();
                using var results = shared.Session.Run(runOptions, inputs, [shared.OutputName]);
                return results[0].GetTensorDataAsSpan<float>().ToArray();
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            shared.Gate.Release();
        }
    }

    private static byte[] ToPcm16(float[] waveform)
    {
        var pcm = new byte[waveform.Length * 2];
        for (int i = 0; i < waveform.Length; i++)
        {
            var sample = (short)(Math.Clamp(waveform[i], -1f, 1f) * 32767f);
            pcm[i * 2] = (byte)sample;
            pcm[i * 2 + 1] = (byte)(sample >> 8);
        }
        return pcm;
    }

    /// <summary>Split at the latest phrase boundary (comma/space token) before the cap.</summary>
    internal static IEnumerable<long[]> SplitTokens(long[] tokens, int maxTokens)
    {
        if (tokens.Length <= maxTokens)
        {
            yield return tokens;
            yield break;
        }

        var start = 0;
        while (tokens.Length - start > maxTokens)
        {
            var end = start + maxTokens;
            // Prefer breaking after a comma (3) or space (16) token.
            var split = -1;
            for (int i = end - 1; i > start; i--)
            {
                if (tokens[i] is 3 or 16) { split = i + 1; break; }
            }
            if (split <= start) split = end; // no boundary — hard split, never truncate

            yield return tokens[start..split];
            start = split;
        }
        if (start < tokens.Length) yield return tokens[start..];
    }
}
