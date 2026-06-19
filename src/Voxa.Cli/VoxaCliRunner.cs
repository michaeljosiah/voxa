using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Voxa.AspNetCore;
using Voxa.Speech;
using Voxa.Speech.Kokoro;
using Voxa.Speech.Piper;
using Voxa.Speech.WhisperCpp;

namespace Voxa.Cli;

/// <summary>
/// The <c>voxa</c> command-line interface (VDX-003): Voxa Core's headless entry point. Commands act on
/// voice <em>artifacts</em> and config without a GUI or a server — <c>transcribe</c> a WAV, <c>say</c>
/// text to a WAV, manage the model cache (<c>models</c>), and validate a pipeline config (<c>check</c>).
/// All logic lives here (not in <c>Program.cs</c>) so it is unit-testable against in-memory writers.
/// </summary>
public static class VoxaCliRunner
{
    /// <summary>Run one CLI invocation. Returns a process exit code; does not throw for user errors.</summary>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length == 0)
        {
            PrintUsage(output);
            return 1;
        }
        if (args[0] is "-h" or "--help" or "help")
        {
            PrintUsage(output);
            return 0;
        }

        var rest = args[1..];
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "transcribe" => await TranscribeAsync(rest, output, error, ct).ConfigureAwait(false),
                "say"        => await SayAsync(rest, output, error, ct).ConfigureAwait(false),
                "models"     => await ModelsAsync(rest, output, error, ct).ConfigureAwait(false),
                "check"      => await CheckAsync(rest, output, error, ct).ConfigureAwait(false),
                _            => UnknownCommand(args[0], error),
            };
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("voxa: cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            // Top-level safety net: a CLI reports the message and a non-zero code, never a stack trace.
            error.WriteLine($"voxa: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> TranscribeAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var (positional, options) = Parse(args);
        if (positional.Count == 0)
        {
            error.WriteLine("usage: voxa transcribe <file.wav> [--model <name>] [--language <lang>]");
            return 1;
        }

        var wavPath = positional[0];
        if (!File.Exists(wavPath))
        {
            error.WriteLine($"voxa: file not found: {wavPath}");
            return 1;
        }

        var wav = WavIO.ReadPcm(wavPath);
        if (wav.Format != 1 || wav.SampleRate != WhisperCppSttEngine.RequiredSampleRate || wav.Channels != 1 || wav.Bits != 16)
        {
            error.WriteLine(
                $"voxa: transcribe needs {WhisperCppSttEngine.RequiredSampleRate} Hz mono 16-bit PCM WAV " +
                $"(got {wav.SampleRate} Hz, {wav.Channels} ch, {wav.Bits}-bit, format {wav.Format}). " +
                "Convert it first, e.g.: ffmpeg -i input -ar 16000 -ac 1 -c:a pcm_s16le out.wav");
            return 1;
        }

        var root = VoxaRoot(
            ("WhisperCpp:Model", options.GetValueOrDefault("model") ?? "base.en"),
            ("WhisperCpp:Language", options.GetValueOrDefault("language") ?? "en"));

        var engine = new WhisperCppSttEngine(WhisperCppOptions.FromConfiguration(root), CacheFor(root));
        try
        {
            await engine.StartAsync(ct).ConfigureAwait(false);
            await engine.WriteAudioAsync(wav.Pcm, ct).ConfigureAwait(false);
            await engine.StopAsync().ConfigureAwait(false); // final flush + completes the transcript stream

            var any = false;
            await foreach (var result in engine.ReadTranscriptsAsync(ct).ConfigureAwait(false))
            {
                output.WriteLine(result.Text);
                any = true;
            }
            if (!any) error.WriteLine("voxa: (no speech transcribed)");
            return 0;
        }
        finally
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<int> SayAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var (positional, options) = Parse(args);
        if (positional.Count == 0)
        {
            error.WriteLine("usage: voxa say \"<text>\" [--out <file.wav>] [--tts piper|kokoro] [--voice <name>]");
            return 1;
        }

        var text = positional[0];
        var outPath = options.GetValueOrDefault("out") ?? options.GetValueOrDefault("o") ?? "voxa-say.wav";
        var tts = (options.GetValueOrDefault("tts") ?? "piper").ToLowerInvariant();
        var voice = options.GetValueOrDefault("voice");

        int sampleRate;
        ITextToSpeechEngine engine;
        switch (tts)
        {
            case "piper":
            {
                var root = VoxaRoot(("Piper:Voice", voice ?? "en_US-lessac-medium"));
                sampleRate = PiperDescriptors.Tts.GetEffectiveOutputSampleRate(root);
                engine = new PiperTtsEngine(PiperOptions.FromConfiguration(root), CacheFor(root));
                break;
            }
            case "kokoro":
            {
                var root = VoxaRoot(("Kokoro:Voice", voice ?? "af_heart"));
                sampleRate = KokoroDescriptors.Tts.GetEffectiveOutputSampleRate(root);
                engine = new KokoroTtsEngine(KokoroOptions.FromConfiguration(root), CacheFor(root));
                break;
            }
            default:
                error.WriteLine($"voxa: unknown --tts '{tts}' (use 'piper' or 'kokoro').");
                return 1;
        }

        try
        {
            await engine.StartAsync(ct).ConfigureAwait(false);
            using var pcm = new MemoryStream();
            await foreach (var chunk in engine.SynthesizeAsync(text, ct).ConfigureAwait(false))
                pcm.Write(chunk.Span);
            await WavIO.WritePcm16Async(outPath, pcm.ToArray(), sampleRate, ct).ConfigureAwait(false);
        }
        finally
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }

        output.WriteLine($"Wrote {outPath} ({tts}, {sampleRate} Hz, {new FileInfo(outPath).Length} bytes).");
        return 0;
    }

    private static Task<int> ModelsAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        _ = ct;
        var (positional, options) = Parse(args);
        var sub = positional.Count > 0 ? positional[0].ToLowerInvariant() : "list";

        var root = options.TryGetValue("cache", out var cacheDir)
            ? VoxaRoot(("Models:CachePath", cacheDir))
            : VoxaRoot();
        var cache = CacheFor(root);

        switch (sub)
        {
            case "list":
            {
                output.WriteLine($"Cache: {cache.Options.CacheRoot}");
                var entries = cache.Enumerate();
                if (entries.Count == 0)
                    output.WriteLine("  (empty — models download on first use)");
                foreach (var entry in entries.OrderBy(e => e.Id, StringComparer.Ordinal))
                    output.WriteLine($"  {entry.Id}  ({Mb(entry.SizeBytes)} MB)");

                output.WriteLine();
                output.WriteLine("Available to download (pinned catalogs):");
                output.WriteLine($"  whisper : {string.Join(", ", WhisperCppModelCatalog.KnownModels)}");
                output.WriteLine($"  piper   : {string.Join(", ", PiperVoiceCatalog.KnownVoices)}");
                output.WriteLine($"  kokoro  : {string.Join(", ", KokoroCatalog.KnownVoices)} " +
                                 $"(precisions: {string.Join(", ", KokoroCatalog.KnownPrecisions)})");
                return Task.FromResult(0);
            }
            case "purge":
            {
                var entries = cache.Enumerate();
                if (options.ContainsKey("all"))
                {
                    if (entries.Count == 0)
                    {
                        output.WriteLine("Cache already empty.");
                        return Task.FromResult(0);
                    }
                    foreach (var entry in entries)
                    {
                        cache.Purge(entry);
                        output.WriteLine($"purged {entry.Id}");
                    }
                    return Task.FromResult(0);
                }
                if (positional.Count > 1)
                {
                    var id = positional[1];
                    var entry = entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
                    if (entry is null)
                    {
                        error.WriteLine($"voxa: '{id}' is not in the cache (run 'voxa models list').");
                        return Task.FromResult(1);
                    }
                    cache.Purge(entry);
                    output.WriteLine($"purged {entry.Id}");
                    return Task.FromResult(0);
                }
                error.WriteLine("usage: voxa models purge (<id> | --all)");
                return Task.FromResult(1);
            }
            default:
                error.WriteLine($"voxa: unknown 'models' subcommand '{sub}' (use 'list' or 'purge').");
                return Task.FromResult(1);
        }
    }

    private static async Task<int> CheckAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var (positional, _) = Parse(args);
        var path = positional.Count > 0
            ? positional[0]
            : File.Exists("appsettings.json") ? "appsettings.json" : null;

        if (path is null)
        {
            error.WriteLine("usage: voxa check <appsettings.json>");
            return 1;
        }
        if (!File.Exists(path))
        {
            error.WriteLine($"voxa: file not found: {path}");
            return 1;
        }

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.GetFullPath(path), optional: false)
            // Validate the config without downloading anything: warm-up (which resolves/downloads
            // models) is the only side-effecting part of the guard, so turn it off — `check` is offline.
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Voxa:Models:EagerWarmup"] = "false" })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddVoxa(configuration);

        using var sp = services.BuildServiceProvider();
        var guard = sp.GetRequiredService<VoxaDefaultsGuard>();
        guard.Arm();
        try
        {
            await guard.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error.WriteLine("Config invalid:");
            error.WriteLine($"  {ex.Message}");
            return 1;
        }

        var voxa = configuration.GetSection("Voxa");
        output.WriteLine($"Config OK: {Path.GetFileName(path)}");
        output.WriteLine($"  STT     : {voxa["Stt"] ?? "(none)"}");
        output.WriteLine($"  TTS     : {voxa["Tts"] ?? "(none)"}");
        output.WriteLine($"  Agent   : {voxa.GetSection("Agent")["Provider"] ?? "OpenAI (default)"}");
        output.WriteLine($"  VAD     : {voxa.GetSection("Vad")["Engine"] ?? "Silero (default)"}");
        output.WriteLine($"  Profile : {voxa["Profile"] ?? "Default"}");
        return 0;
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>Split argv into positional values and <c>--key value</c> / <c>--flag</c> options.</summary>
    private static (List<string> Positional, Dictionary<string, string> Options) Parse(string[] args)
    {
        var positional = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Length > 1 && a[0] == '-')
            {
                var key = a.TrimStart('-');
                var hasValue = i + 1 < args.Length && !(args[i + 1].Length > 1 && args[i + 1][0] == '-');
                options[key] = hasValue ? args[++i] : "true";
            }
            else
            {
                positional.Add(a);
            }
        }
        return (positional, options);
    }

    /// <summary>Build a <c>"Voxa"</c> root section from in-memory pairs (keys without the <c>Voxa:</c> prefix).</summary>
    private static IConfigurationSection VoxaRoot(params (string Key, string? Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => $"Voxa:{p.Key}", p => p.Value))
            .Build()
            .GetSection("Voxa");

    private static VoxaModelCache CacheFor(IConfigurationSection voxaRoot)
        => new(VoxaModelCacheOptions.FromConfiguration(voxaRoot));

    private static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:0.0}";

    private static int UnknownCommand(string command, TextWriter error)
    {
        error.WriteLine($"voxa: unknown command '{command}'. Run 'voxa --help' for usage.");
        return 1;
    }

    private static void PrintUsage(TextWriter output) => output.WriteLine(Usage);

    private const string Usage =
        "voxa — headless commands for the Voxa voice pipeline (Core).\n\n" +
        "Usage:\n" +
        "  voxa transcribe <file.wav> [--model <name>] [--language <lang>]\n" +
        "      Speech-to-text a 16 kHz mono 16-bit PCM WAV via whisper.cpp (prints the transcript).\n\n" +
        "  voxa say \"<text>\" [--out <file.wav>] [--tts piper|kokoro] [--voice <name>]\n" +
        "      Text-to-speech to a WAV file (default voxa-say.wav, Piper voice).\n\n" +
        "  voxa models [list | purge (<id> | --all)] [--cache <dir>]\n" +
        "      Inspect or clear the SHA-256-pinned model cache.\n\n" +
        "  voxa check [<appsettings.json>]\n" +
        "      Validate a Voxa pipeline config (providers, models, credentials) without downloading.\n\n" +
        "Models download on first use into the shared cache (VOXA_MODEL_CACHE or the OS default).";
}
