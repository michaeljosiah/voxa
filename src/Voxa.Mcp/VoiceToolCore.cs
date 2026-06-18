using System.Text;
using Microsoft.Extensions.Configuration;
using Voxa.Speech;
using Voxa.Speech.Kokoro;
using Voxa.Speech.Piper;
using Voxa.Speech.WhisperCpp;

namespace Voxa.Mcp;

/// <summary>
/// Transport-free implementation behind the MCP tools (VDX-002). Mirrors the CLI's engine glue: build
/// a one-off <c>"Voxa"</c> config section, resolve the shared model cache, drive the local engine.
/// Kept separate from <see cref="VoiceTools"/> so the logic is unit-testable without the MCP host.
/// </summary>
internal static class VoiceToolCore
{
    /// <summary>Synthesize <paramref name="text"/> to a WAV file; returns the written path.</summary>
    public static async Task<string> SpeakAsync(string text, string tts, string? voice, string? outputPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("voxa_speak: 'text' must not be empty.", nameof(text));

        int sampleRate;
        ITextToSpeechEngine engine;
        switch ((tts ?? "piper").ToLowerInvariant())
        {
            case "kokoro":
            {
                var root = VoxaRoot(("Kokoro:Voice", voice ?? "af_heart"));
                sampleRate = KokoroDescriptors.Tts.GetEffectiveOutputSampleRate(root);
                engine = new KokoroTtsEngine(KokoroOptions.FromConfiguration(root), CacheFor(root));
                break;
            }
            case "piper":
            {
                var root = VoxaRoot(("Piper:Voice", voice ?? "en_US-lessac-medium"));
                sampleRate = PiperDescriptors.Tts.GetEffectiveOutputSampleRate(root);
                engine = new PiperTtsEngine(PiperOptions.FromConfiguration(root), CacheFor(root));
                break;
            }
            default:
                throw new ArgumentException($"voxa_speak: unknown tts '{tts}' (use 'piper' or 'kokoro').", nameof(tts));
        }

        var path = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(Path.GetTempPath(), $"voxa-speak-{Guid.NewGuid():N}.wav")
            : outputPath;

        try
        {
            await engine.StartAsync(ct).ConfigureAwait(false);
            using var pcm = new MemoryStream();
            await foreach (var chunk in engine.SynthesizeAsync(text, ct).ConfigureAwait(false))
                pcm.Write(chunk.Span);
            await WritePcm16Async(path, pcm.ToArray(), sampleRate, ct).ConfigureAwait(false);
        }
        finally
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }

        return path;
    }

    /// <summary>Transcribe a 16 kHz mono PCM16 WAV; returns the transcript text.</summary>
    public static async Task<string> TranscribeAsync(string wavPath, string? model, string? language, CancellationToken ct)
    {
        if (!File.Exists(wavPath))
            throw new FileNotFoundException($"voxa_transcribe: WAV file not found: {wavPath}");

        var wav = ReadWav(wavPath);
        if (wav.Format != 1 || wav.SampleRate != WhisperCppSttEngine.RequiredSampleRate || wav.Channels != 1 || wav.Bits != 16)
        {
            throw new ArgumentException(
                $"voxa_transcribe needs {WhisperCppSttEngine.RequiredSampleRate} Hz mono 16-bit PCM WAV " +
                $"(got {wav.SampleRate} Hz, {wav.Channels} ch, {wav.Bits}-bit). " +
                "Convert it first, e.g.: ffmpeg -i input -ar 16000 -ac 1 -c:a pcm_s16le out.wav");
        }

        var root = VoxaRoot(("WhisperCpp:Model", model ?? "base.en"), ("WhisperCpp:Language", language ?? "en"));
        var engine = new WhisperCppSttEngine(WhisperCppOptions.FromConfiguration(root), CacheFor(root));
        try
        {
            await engine.StartAsync(ct).ConfigureAwait(false);
            await engine.WriteAudioAsync(wav.Pcm, ct).ConfigureAwait(false);
            await engine.StopAsync().ConfigureAwait(false);

            var sb = new StringBuilder();
            await foreach (var result in engine.ReadTranscriptsAsync(ct).ConfigureAwait(false))
                sb.AppendLine(result.Text);
            return sb.ToString().Trim();
        }
        finally
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The local TTS voices, as <c>engine/voice</c> ids — no network, no model load.</summary>
    public static IReadOnlyList<string> ListVoices() =>
    [
        .. PiperVoiceCatalog.KnownVoices.Select(v => $"piper/{v}"),
        .. KokoroCatalog.KnownVoices.Select(v => $"kokoro/{v}"),
    ];

    // ── helpers (mirror the CLI; a shared headless-helpers package is a future refactor) ────────

    private static IConfigurationSection VoxaRoot(params (string Key, string? Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => $"Voxa:{p.Key}", p => p.Value))
            .Build()
            .GetSection("Voxa");

    private static VoxaModelCache CacheFor(IConfigurationSection voxaRoot)
        => new(VoxaModelCacheOptions.FromConfiguration(voxaRoot));

    private readonly record struct Wav(byte[] Pcm, int SampleRate, short Channels, short Bits, short Format);

    private static Wav ReadWav(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "RIFF") throw new InvalidDataException("Not a RIFF/WAV file.");
        br.ReadUInt32();
        if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "WAVE") throw new InvalidDataException("Not a WAVE file.");

        short format = 0, channels = 0, bits = 0;
        int sampleRate = 0;
        byte[]? data = null;
        while (fs.Position + 8 <= fs.Length)
        {
            var id = Encoding.ASCII.GetString(br.ReadBytes(4));
            var size = br.ReadInt32();
            if (size < 0) break;
            if (id == "fmt ")
            {
                format = br.ReadInt16();
                channels = br.ReadInt16();
                sampleRate = br.ReadInt32();
                br.ReadInt32();
                br.ReadInt16();
                bits = br.ReadInt16();
                if (size > 16) br.ReadBytes(size - 16);
            }
            else if (id == "data")
            {
                data = br.ReadBytes(size);
            }
            else
            {
                br.ReadBytes(size);
            }
            if ((size & 1) == 1 && fs.Position < fs.Length) br.ReadByte();
        }
        if (data is null) throw new InvalidDataException("WAV file has no 'data' chunk.");
        return new Wav(data, sampleRate, channels, bits, format);
    }

    private static async Task WritePcm16Async(string path, byte[] pcm, int sampleRate, CancellationToken ct)
    {
        const short channels = 1, bits = 16;
        using var header = new MemoryStream(44);
        using (var w = new BinaryWriter(header, Encoding.ASCII, leaveOpen: true))
        {
            w.Write("RIFF"u8.ToArray());
            w.Write(36 + pcm.Length);
            w.Write("WAVE"u8.ToArray());
            w.Write("fmt "u8.ToArray());
            w.Write(16);
            w.Write((short)1);
            w.Write(channels);
            w.Write(sampleRate);
            w.Write(sampleRate * channels * bits / 8);
            w.Write((short)(channels * bits / 8));
            w.Write(bits);
            w.Write("data"u8.ToArray());
            w.Write(pcm.Length);
        }

        await using var fs = File.Create(path);
        await fs.WriteAsync(header.GetBuffer().AsMemory(0, (int)header.Length), ct).ConfigureAwait(false);
        await fs.WriteAsync(pcm, ct).ConfigureAwait(false);
    }
}
