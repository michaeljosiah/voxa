namespace Voxa.Mcp.Tests;

/// <summary>
/// Covers the MCP tool logic that needs no model download: input validation and the voice listing.
/// <c>speak</c>/<c>transcribe</c> happy paths pull weights and are exercised manually; here we only
/// assert the guards fire (and, for transcribe, before any download is attempted).
/// </summary>
public class VoiceToolCoreTests
{
    [Fact]
    public async Task Transcribe_Missing_File_Throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            VoiceToolCore.TranscribeAsync("does-not-exist.wav", null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Transcribe_Wrong_Format_Wav_Is_Rejected_Before_Any_Download()
    {
        var path = Path.Combine(Path.GetTempPath(), "voxa-mcp-tests", Guid.NewGuid().ToString("N") + ".wav");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteStereo44kSilence(path);
        try
        {
            var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
                VoiceToolCore.TranscribeAsync(path, null, null, CancellationToken.None));
            Assert.Contains("16000", ex.Message);
            Assert.Contains("ffmpeg", ex.Message);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Speak_Unknown_Engine_Is_Rejected()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            VoiceToolCore.SpeakAsync("hello", "festival", null, null, CancellationToken.None));
        Assert.Contains("festival", ex.Message);
    }

    [Fact]
    public async Task Speak_Empty_Text_Is_Rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            VoiceToolCore.SpeakAsync("   ", "piper", null, null, CancellationToken.None));
    }

    [Fact]
    public void List_Voices_Includes_Piper_And_Kokoro()
    {
        var voices = VoiceToolCore.ListVoices();
        Assert.Contains("piper/en_US-lessac-medium", voices);
        Assert.Contains("kokoro/af_heart", voices);
    }

    private static void WriteStereo44kSilence(string path)
    {
        var pcm = new byte[4410 * 2 * 2]; // 100 ms, 2 ch, 16-bit
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write("RIFF"u8.ToArray()); bw.Write(36 + pcm.Length); bw.Write("WAVE"u8.ToArray());
        bw.Write("fmt "u8.ToArray()); bw.Write(16); bw.Write((short)1); bw.Write((short)2);
        bw.Write(44100); bw.Write(44100 * 2 * 2); bw.Write((short)4); bw.Write((short)16);
        bw.Write("data"u8.ToArray()); bw.Write(pcm.Length); bw.Write(pcm);
    }

    private static void Cleanup(string path)
    {
        try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { /* best-effort */ }
    }
}
