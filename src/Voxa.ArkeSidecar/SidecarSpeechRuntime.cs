using System.Collections.Concurrent;
using System.Reflection;
using Voxa.Speech;
using Voxa.Speech.Kokoro;
using Voxa.Speech.WhisperCpp;
using Microsoft.Extensions.Logging;

namespace Voxa.ArkeSidecar;

internal sealed class SidecarSpeechRuntime : ISidecarSpeechRuntime, IAsyncDisposable
{
    private readonly SidecarOptions _options;
    private readonly ConcurrentDictionary<(string Voice, double Speed), Lazy<Task<KokoroTtsEngine>>> _tts = new();
    private readonly SemaphoreSlim _ttsGate = new(2, 2);
    private readonly SemaphoreSlim _sttGate = new(1, 1);
    private string? _kokoroFailure;
    private string? _whisperFailure;
    private string? _wordTimingFailure;
    private readonly NativeWordTranscriber _words = new();

    public SidecarSpeechRuntime(SidecarOptions options)
    {
        _options = options;
    }

    public HealthResponse Health
    {
        get
        {
            var kokoro = KokoroAssetFailure() ?? _kokoroFailure;
            var whisper = WhisperAssetFailure() ?? _whisperFailure;
            var unavailable = kokoro is null && whisper is null
                ? null
                : $"{(kokoro is null ? null : "Kokoro is unavailable.")}{(kokoro is not null && whisper is not null ? " " : null)}{(whisper is null ? null : "Whisper is unavailable.")}";
            return new HealthResponse(
                true,
                Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.8.0",
                1,
                HealthResponse.CurrentArchitecture,
                ["kokoro", "whisper"],
                new Dictionary<string, EngineHealth>
                {
                    ["kokoro"] = new(kokoro is null, kokoro is null ? null : "Kokoro is unavailable."),
                    ["whisper"] = new(whisper is null, whisper is null ? null : "Whisper is unavailable."),
                },
                unavailable,
                new EngineHealth(whisper is null && _wordTimingFailure is null,
                    whisper is null ? _wordTimingFailure : "Whisper is unavailable."));
        }
    }

    public async Task<byte[]> SynthesizeAsync(TtsRequest request, CancellationToken ct)
    {
        if (!VoiceCatalog.Contains(request.Voice)) throw new ArgumentException("Unknown local voice.");
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("Text is required.");
        if (request.Text.Length > 10_000) throw new ArgumentException("Text exceeds 10,000 characters.");
        var speed = request.Speed ?? 1.0;
        if (speed is not (0.92 or 1.0 or 1.15)) throw new ArgumentException("Speed must be 0.92, 1.0, or 1.15.");
        EnsureKokoroAvailable();

        await _ttsGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var lazy = _tts.GetOrAdd((request.Voice, speed), key =>
                new Lazy<Task<KokoroTtsEngine>>(() => CreateTtsAsync(key.Voice, key.Speed, CancellationToken.None)));
            var engine = await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
            using var pcm = new MemoryStream();
            await foreach (var chunk in engine.SynthesizeAsync(request.Text, ct).ConfigureAwait(false))
                pcm.Write(chunk.Span);
            return Pcm16Wav.Wrap(pcm.GetBuffer().AsSpan(0, checked((int)pcm.Length)), KokoroCatalog.OutputSampleRate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _kokoroFailure = SafeReason(ex, "Kokoro is unavailable.");
            throw new SidecarUnavailableException(_kokoroFailure);
        }
        finally { _ttsGate.Release(); }
    }

    public async Task<string> TranscribeAsync(ReadOnlyMemory<byte> audio, string? contentType, CancellationToken ct, string? prompt = null)
    {
        EnsureWhisperAvailable();
        var pcm = AudioInput.ToWhisperPcm(audio.Span, contentType);
        await _sttGate.WaitAsync(ct).ConfigureAwait(false);
        var failures = new FailureCapturingLogger();
        var engine = new WhisperCppSttEngine(
            new WhisperCppOptions { ModelPath = _options.WhisperModel, Language = "en", InitialPrompt = prompt },
            new VoxaModelCache(new VoxaModelCacheOptions(Path.GetTempPath(), Offline: true)), failures);
        try
        {
            await engine.StartAsync(ct).ConfigureAwait(false);
            await engine.WriteAudioAsync(pcm, ct).ConfigureAwait(false);
            await engine.FlushAsync().ConfigureAwait(false);
            await engine.WaitForPendingAsync(ct).ConfigureAwait(false);
            await engine.StopAsync().ConfigureAwait(false);
            if (failures.Failed) throw new InvalidOperationException("Whisper transcription failed.");
            var parts = new List<string>();
            await foreach (var result in engine.ReadTranscriptsAsync(ct).ConfigureAwait(false))
                if (result.IsFinal) parts.Add(result.Text);
            return string.Join(' ', parts);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _whisperFailure = SafeReason(ex, "Whisper is unavailable.");
            throw new SidecarUnavailableException(_whisperFailure);
        }
        catch (OperationCanceledException)
        {
            await engine.CancelAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            await engine.DisposeAsync().ConfigureAwait(false);
            _sttGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var engine in _tts.Values.Where(v => v.IsValueCreated))
        {
            try { await (await engine.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
            catch { }
        }
        _ttsGate.Dispose();
        await _sttGate.WaitAsync().ConfigureAwait(false);
        try { await _words.DisposeAsync().ConfigureAwait(false); }
        finally { _sttGate.Release(); }
        _sttGate.Dispose();
    }

    public async Task<WordTimingResponse> TranscribeWordsAsync(ReadOnlyMemory<byte> audio, string? contentType, CancellationToken ct)
    {
        EnsureWhisperAvailable();
        if (_wordTimingFailure is not null) throw new SidecarUnavailableException(_wordTimingFailure);
        var pcm = AudioInput.ToWhisperPcm(audio.Span, contentType);
        await _sttGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await _words.TranscribeAsync(_options.WhisperModel!, pcm, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException and not WordTimingRejectedException and not InvalidDataException)
        {
            _wordTimingFailure = "Word timing is unavailable.";
            throw new SidecarUnavailableException(_wordTimingFailure);
        }
        finally { _sttGate.Release(); }
    }

    private async Task<KokoroTtsEngine> CreateTtsAsync(string voice, double speed, CancellationToken ct)
    {
        var engine = new KokoroTtsEngine(
            new KokoroOptions
            {
                ModelPath = _options.KokoroModel,
                Voice = voice,
                VoicePath = Path.Combine(_options.KokoroVoices!, voice + ".bin"),
                EspeakPath = _options.Espeak,
                EspeakDataPath = EspeakDataParent(_options.EspeakData!),
                Speed = speed,
                Device = "cpu",
            },
            new VoxaModelCache(new VoxaModelCacheOptions(Path.GetTempPath(), Offline: true)));
        await engine.StartAsync(ct).ConfigureAwait(false);
        return engine;
    }

    private string? KokoroAssetFailure()
    {
        if (HealthResponse.CurrentArchitecture == "unsupported") return "This processor architecture is unsupported.";
        if (!File.Exists(_options.KokoroModel)) return "Kokoro model is unavailable.";
        if (!File.Exists(_options.KokoroConfig)) return "Kokoro config is unavailable.";
        if (!Directory.Exists(_options.KokoroVoices)) return "Kokoro voices are unavailable.";
        if (VoiceCatalog.Voices.Any(v => !File.Exists(Path.Combine(_options.KokoroVoices, v.Id + ".bin"))))
            return "One or more Kokoro voices are unavailable.";
        if (!File.Exists(_options.Espeak)) return "espeak-ng is unavailable.";
        if (!Directory.Exists(_options.EspeakData)) return "espeak-ng data is unavailable.";
        return null;
    }

    private string? WhisperAssetFailure()
    {
        if (HealthResponse.CurrentArchitecture == "unsupported") return "This processor architecture is unsupported.";
        return File.Exists(_options.WhisperModel) ? null : "Whisper model is unavailable.";
    }

    private void EnsureKokoroAvailable()
    {
        var reason = KokoroAssetFailure() ?? _kokoroFailure;
        if (reason is not null) throw new SidecarUnavailableException(reason);
    }

    private void EnsureWhisperAvailable()
    {
        var reason = WhisperAssetFailure() ?? _whisperFailure;
        if (reason is not null) throw new SidecarUnavailableException(reason);
    }

    private static string SafeReason(Exception ex, string fallback)
        => ex is DllNotFoundException or BadImageFormatException ? "The local speech runtime is incompatible." : fallback;

    private static string EspeakDataParent(string path)
        => string.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), "espeak-ng-data", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path))!
            : path;

    private sealed class FailureCapturingLogger : ILogger
    {
        public bool Failed { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) Failed = true;
        }
    }
}
