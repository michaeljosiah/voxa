using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Voxa.Speech.Piper;

/// <summary>
/// Local text-to-speech over the official piper executable (VLS-001 WS2). Per-sentence,
/// non-streaming synthesis — invisible to the pipeline because <c>SentenceAggregator</c> already
/// delivers exactly that unit — with the resulting PCM yielded in small chunks so the sink's
/// pacing and barge-in flush behave identically to cloud TTS.
///
/// <para>
/// The GPL-licensed espeak-ng phonemizer ships inside the piper process; this package links no
/// GPL code (VLS-001 §3.3 — the process boundary is a hard rule, not an implementation detail).
/// </para>
/// </summary>
public sealed class PiperTtsEngine : ITextToSpeechEngine
{
    /// <summary>~4096 samples per yielded chunk (8 KiB of PCM16) — barge-in stops between chunks.</summary>
    private const int ChunkBytes = 4096 * 2;

    private readonly PiperOptions _options;
    private readonly VoxaModelCache? _cache;
    private readonly ILogger _logger;

    // Test seam: replaces (executable + voice resolution + process pool) with a WAV source.
    private Func<string, CancellationToken, Task<byte[]>>? _synthesizeWav;
    private int _expectedSampleRate;

    public PiperTtsEngine(PiperOptions options, VoxaModelCache cache, ILogger? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? NullLogger.Instance;
    }

    internal PiperTtsEngine(PiperOptions options, Func<string, CancellationToken, Task<byte[]>> wavSynthesizer)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _synthesizeWav = wavSynthesizer ?? throw new ArgumentNullException(nameof(wavSynthesizer));
        _logger = NullLogger.Instance;
        _expectedSampleRate = ExpectedRate(options);
    }

    /// <summary>The rate this engine announces/enforces: explicit override → voice-name inference.</summary>
    internal static int ExpectedRate(PiperOptions options)
        => options.OutputSampleRate ?? PiperVoiceCatalog.InferSampleRateFromName(options.Voice);

    public async Task StartAsync(CancellationToken ct)
    {
        if (_synthesizeWav is not null) return; // test seam or already started

        // Voice: explicit path (json sibling expected) or catalog → cache.
        string voicePath;
        if (!string.IsNullOrEmpty(_options.VoicePath))
        {
            // Root the path against the app's current directory NOW. The piper child runs with
            // WorkingDirectory set to a temp output dir, so a relative --model would be resolved
            // against that temp dir — not the CWD where validation and ReadVoiceSampleRate found
            // the file — and piper would fail to start despite startup validation passing.
            voicePath = Path.GetFullPath(_options.VoicePath);
            if (!File.Exists(voicePath))
                throw new VoxaModelUnavailableException(
                    $"Voxa:Piper:VoicePath is set to '{_options.VoicePath}' but no file exists there.");
            if (!File.Exists(voicePath + ".json"))
                throw new VoxaModelUnavailableException(
                    $"piper requires the voice config next to the model: '{voicePath}.json' was not found.");
        }
        else
        {
            if (!PiperVoiceCatalog.TryGet(_options.Voice, out var voice))
                throw new VoxaModelUnavailableException(
                    $"Unknown Voxa:Piper:Voice '{_options.Voice}'. Known voices: " +
                    $"{string.Join(", ", PiperVoiceCatalog.KnownVoices)}. " +
                    "Or set Voxa:Piper:VoicePath to a piper voice of your own.");
            voicePath = await _cache!.ResolveAsync(voice.Onnx, ct).ConfigureAwait(false);
            await _cache.ResolveAsync(voice.Json, ct).ConfigureAwait(false);
        }

        // The voice's own config is the ground truth for its rate. The session envelope already
        // announced ExpectedRate(options) at composition time, so a mismatch here must be a loud
        // startup failure, not a runtime mystery of wrong-speed audio.
        var configuredRate = ReadVoiceSampleRate(voicePath + ".json");
        var expected = _options.OutputSampleRate
            ?? (_options.VoicePath is null ? PiperVoiceCatalog.InferSampleRateFromName(_options.Voice) : configuredRate);
        if (configuredRate != expected)
        {
            throw new InvalidOperationException(
                $"The piper voice at '{voicePath}' synthesizes at {configuredRate} Hz, but the session is " +
                $"configured for {expected} Hz. Set Voxa:Piper:OutputSampleRate to {configuredRate} " +
                "(or remove a wrong override) so the announced rate matches the voice.");
        }
        _expectedSampleRate = expected;

        // Executable: explicit path → PATH → pinned per-RID download.
        var exePath = await ResolveExecutableAsync(ct).ConfigureAwait(false);

        var pool = PiperProcessPool.GetOrCreate(
            exePath, voicePath, _options.LengthScale, _options.MaxProcesses, _logger);
        _synthesizeWav = pool.SynthesizeAsync;
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text, [EnumeratorCancellation] CancellationToken ct)
    {
        if (_synthesizeWav is null)
            throw new InvalidOperationException("PiperTtsEngine.StartAsync must run before SynthesizeAsync.");

        var wav = await _synthesizeWav(text, ct).ConfigureAwait(false);
        var info = WavAudio.Parse(wav);

        if (info.SampleRate != _expectedSampleRate)
        {
            throw new InvalidOperationException(
                $"piper produced {info.SampleRate} Hz audio but the session announced {_expectedSampleRate} Hz. " +
                $"Set Voxa:Piper:OutputSampleRate to {info.SampleRate} so the rates agree.");
        }

        for (int offset = 0; offset < info.DataLength; offset += ChunkBytes)
        {
            ct.ThrowIfCancellationRequested();
            var length = Math.Min(ChunkBytes, info.DataLength - offset);
            yield return new ReadOnlyMemory<byte>(wav, info.DataOffset + offset, length);
        }
    }

    public ValueTask DisposeAsync()
    {
        // The pool (and its piper processes) is process-lifetime by design — engines are
        // per-connection and must not tear down hosts other connections are using.
        return ValueTask.CompletedTask;
    }

    private async Task<string> ResolveExecutableAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_options.ExecutablePath))
        {
            // Root it for the same reason as the voice path: the child runs in a temp dir, so a
            // relative executable would be resolved against that dir rather than the app's CWD.
            var exePath = Path.GetFullPath(_options.ExecutablePath);
            if (!File.Exists(exePath))
                throw new VoxaModelUnavailableException(
                    $"Voxa:Piper:ExecutablePath is set to '{_options.ExecutablePath}' but no file exists there.");
            return exePath;
        }

        if (PiperExecutableCatalog.FindOnPath() is { } onPath)
        {
            _logger.LogInformation("piper executable found on PATH: {Path}", onPath);
            return onPath;
        }

        var artifact = PiperExecutableCatalog.ForCurrentPlatform();
        if (artifact is null)
        {
            throw new VoxaModelUnavailableException(
                $"No pinned piper build exists for this platform ({PiperExecutableCatalog.CurrentRid()}). " +
                "Install piper (https://github.com/rhasspy/piper) and either add it to PATH or set " +
                "Voxa:Piper:ExecutablePath.");
        }
        return await _cache!.ResolveAsync(artifact, ct).ConfigureAwait(false);
    }

    private static int ReadVoiceSampleRate(string jsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(jsonPath));
        return doc.RootElement.GetProperty("audio").GetProperty("sample_rate").GetInt32();
    }
}
