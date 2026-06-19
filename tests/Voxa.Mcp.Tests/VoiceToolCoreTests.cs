using Microsoft.Extensions.Configuration;
using Voxa.Speech;

namespace Voxa.Mcp.Tests;

/// <summary>
/// Covers the MCP tool logic that needs no model download: input validation, host-config overlay, and
/// the voice listing. <c>speak</c>/<c>transcribe</c> happy paths pull weights and are exercised
/// manually; here we only assert the guards fire (and, for transcribe, before any download is attempted).
/// </summary>
public class VoiceToolCoreTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value))
            .Build();

    [Fact]
    public async Task Transcribe_Missing_File_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            VoiceToolCore.TranscribeAsync(Config(), "does-not-exist.wav", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Transcribe_Wrong_Format_Wav_Is_Rejected_Before_Any_Download()
    {
        var path = Path.Combine(Path.GetTempPath(), "voxa-mcp-tests", Guid.NewGuid().ToString("N") + ".wav");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteWav(path, sampleRate: 44100, channels: 2);
        try
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                VoiceToolCore.TranscribeAsync(Config(), path, null, null, CancellationToken.None));
            Assert.Contains("16000", ex.Message);
            Assert.Contains("ffmpeg", ex.Message);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Transcribe_Honors_Host_Offline_Config()
    {
        // VDX-002 review (Codex P2): the tool must overlay call args ON the host config, not discard it.
        // With the host set Offline + an empty cache, transcribe must fail loud offline (no download
        // attempt) instead of ignoring Offline and hitting the network.
        var dir = Path.Combine(Path.GetTempPath(), "voxa-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var wav = Path.Combine(dir, "in.wav");
        WriteWav(wav, sampleRate: 16000, channels: 1);
        var hostConfig = Config(("Voxa:Models:Offline", "true"), ("Voxa:Models:CachePath", dir));
        // The empty CachePath must win, not a CI/dev VOXA_MODEL_CACHE that might already hold tiny.en.
        var priorCacheEnv = Environment.GetEnvironmentVariable("VOXA_MODEL_CACHE");
        Environment.SetEnvironmentVariable("VOXA_MODEL_CACHE", null);
        try
        {
            var ex = await Assert.ThrowsAsync<VoxaModelUnavailableException>(() =>
                VoiceToolCore.TranscribeAsync(hostConfig, wav, "tiny.en", "en", CancellationToken.None));
            Assert.Contains("Offline", ex.Message); // proves the host's Offline setting was preserved
        }
        finally
        {
            Environment.SetEnvironmentVariable("VOXA_MODEL_CACHE", priorCacheEnv);
            Cleanup(wav);
        }
    }

    [Fact]
    public async Task Speak_Unknown_Engine_Is_Rejected()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            VoiceToolCore.SpeakAsync(Config(), "hello", "festival", null, null, CancellationToken.None));
        Assert.Contains("festival", ex.Message);
    }

    [Fact]
    public async Task Speak_Empty_Text_Is_Rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            VoiceToolCore.SpeakAsync(Config(), "   ", "piper", null, null, CancellationToken.None));
    }

    [Fact]
    public void List_Voices_Includes_Piper_And_Kokoro()
    {
        var voices = VoiceToolCore.ListVoices();
        Assert.Contains("piper/en_US-lessac-medium", voices);
        Assert.Contains("kokoro/af_heart", voices);
    }

    // 100 ms of silent 16-bit PCM as a WAV, parameterised by rate/channels for the format-guard tests.
    private static void WriteWav(string path, int sampleRate, short channels)
    {
        var pcm = new byte[sampleRate / 10 * channels * 2];
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write("RIFF"u8.ToArray()); bw.Write(36 + pcm.Length); bw.Write("WAVE"u8.ToArray());
        bw.Write("fmt "u8.ToArray()); bw.Write(16); bw.Write((short)1); bw.Write(channels);
        bw.Write(sampleRate); bw.Write(sampleRate * channels * 2); bw.Write((short)(channels * 2)); bw.Write((short)16);
        bw.Write("data"u8.ToArray()); bw.Write(pcm.Length); bw.Write(pcm);
    }

    private static void Cleanup(string path)
    {
        try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { /* best-effort */ }
    }
}
