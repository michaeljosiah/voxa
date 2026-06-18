namespace Voxa.Cli.Tests;

/// <summary>
/// Covers the CLI surface that needs no model download: argument dispatch, the model-cache view, and
/// config validation. <c>transcribe</c>/<c>say</c> against real models are exercised manually (they
/// pull weights); here we only assert <c>transcribe</c> rejects a wrong-format WAV before any download.
/// </summary>
public class VoxaCliTests
{
    private static async Task<(int Code, string Out, string Err)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await VoxaCliRunner.RunAsync(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task No_Args_Prints_Usage_And_Returns_NonZero()
    {
        var (code, output, _) = await RunAsync();
        Assert.Equal(1, code);
        Assert.Contains("transcribe", output);
        Assert.Contains("say", output);
    }

    [Fact]
    public async Task Help_Prints_Usage_And_Returns_Zero()
    {
        var (code, output, _) = await RunAsync("--help");
        Assert.Equal(0, code);
        Assert.Contains("voxa check", output);
    }

    [Fact]
    public async Task Unknown_Command_Is_An_Error()
    {
        var (code, _, err) = await RunAsync("frobnicate");
        Assert.Equal(1, code);
        Assert.Contains("frobnicate", err);
    }

    [Fact]
    public async Task Models_List_On_An_Empty_Cache_Shows_The_Catalogs()
    {
        var temp = Path.Combine(Path.GetTempPath(), "voxa-cli-tests", Guid.NewGuid().ToString("N"));
        var (code, output, _) = await RunAsync("models", "list", "--cache", temp);

        Assert.Equal(0, code);
        Assert.Contains("(empty", output);
        Assert.Contains("base.en", output);             // a whisper catalog entry
        Assert.Contains("en_US-lessac-medium", output); // a piper voice
        Assert.Contains("af_heart", output);            // a kokoro voice
    }

    [Fact]
    public async Task Models_Purge_Without_A_Target_Errors()
    {
        var temp = Path.Combine(Path.GetTempPath(), "voxa-cli-tests", Guid.NewGuid().ToString("N"));
        var (code, _, err) = await RunAsync("models", "purge", "--cache", temp);

        Assert.Equal(1, code);
        Assert.Contains("--all", err);
    }

    [Fact]
    public async Task Check_Validates_A_Keyless_Local_Config()
    {
        var path = WriteTempConfig(
            """{ "Voxa": { "Stt": "WhisperCpp", "Tts": "Piper", "Agent": { "Provider": "Echo" } } }""");
        try
        {
            var (code, output, _) = await RunAsync("check", path);
            Assert.Equal(0, code);
            Assert.Contains("Config OK", output);
            Assert.Contains("WhisperCpp", output);
            Assert.Contains("Echo", output);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Check_Rejects_An_Unknown_Agent_Provider()
    {
        var path = WriteTempConfig(
            """{ "Voxa": { "Stt": "WhisperCpp", "Tts": "Piper", "Agent": { "Provider": "Bogus" } } }""");
        try
        {
            var (code, _, err) = await RunAsync("check", path);
            Assert.Equal(1, code);
            Assert.Contains("Bogus", err);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task Transcribe_Rejects_A_Wrong_Format_Wav_Before_Any_Download()
    {
        var path = Path.Combine(Path.GetTempPath(), "voxa-cli-tests", Guid.NewGuid().ToString("N") + ".wav");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteStereo44kSilence(path);
        try
        {
            var (code, _, err) = await RunAsync("transcribe", path);
            Assert.Equal(1, code);
            Assert.Contains("16000", err);
            Assert.Contains("ffmpeg", err);
        }
        finally { Cleanup(path); }
    }

    private static string WriteTempConfig(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), "voxa-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "appsettings.json");
        File.WriteAllText(path, json);
        return path;
    }

    // A deliberately wrong-format WAV (44.1 kHz, stereo, 16-bit) — exercises the transcribe format guard.
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
