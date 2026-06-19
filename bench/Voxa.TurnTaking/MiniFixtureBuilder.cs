using Voxa.Testing.Audio;

namespace Voxa.TurnTaking;

/// <summary>
/// Builds the checked-in mini fixture from a real source WAV (the repo's <c>jfk.wav</c> — a public-domain
/// JFK address). Each cascade-fair category gets one short sample, shaped to its turn-taking behaviour with
/// trailing silence so the VAD closes the turn. The audio is real (never synthesized); the silence padding
/// is documented in each <c>meta.json</c>. A <c>backchannel/</c> dir is created so the walker discovers and
/// skips it. Run once via <c>--make-mini-fixture</c>; the outputs are committed (VRT-001 §5).
/// </summary>
internal static class MiniFixtureBuilder
{
    public static void Build(string sourceWavPath, string destRoot)
    {
        var src = WavFile.Read(sourceWavPath);
        int rate = src.SampleRate, channels = src.Channels;
        int bytesPerSec = rate * channels * 2; // 16-bit

        byte[] Slice(double startSec, double durSec)
        {
            int start = Align(Math.Clamp((int)(startSec * bytesPerSec), 0, src.Pcm.Length));
            int len = Align(Math.Min((int)(durSec * bytesPerSec), src.Pcm.Length - start));
            var dst = new byte[Math.Max(0, len)];
            Array.Copy(src.Pcm, start, dst, 0, dst.Length);
            return dst;
        }
        byte[] Silence(double durSec) => new byte[Align((int)(durSec * bytesPerSec))];
        static int Align(int b) => b - (b % 2); // 16-bit sample alignment

        // smooth_turn_taking — a clean utterance, then a clear end-of-turn the bot should answer into.
        Write(destRoot, "smooth_turn_taking", rate, channels,
            Concat(Slice(0.0, 3.0), Silence(1.2)),
            "Clean end-of-turn: speech then a clear pause the bot should respond into.");

        // pause_handling — speech, a within-turn pause (the user is NOT done), more speech, then end-of-turn.
        Write(destRoot, "pause_handling", rate, channels,
            Concat(Slice(0.0, 1.6), Silence(0.7), Slice(1.6, 1.6), Silence(1.2)),
            "Within-turn pause: the bot should wait through the mid-thought gap, not barge in.");

        // user_interruption — a short lead-in silence then the user's barge-in speech, then end-of-turn.
        Write(destRoot, "user_interruption", rate, channels,
            Concat(Silence(0.4), Slice(3.0, 2.0), Silence(1.2)),
            "Barge-in onset: the user starts speaking; the bot should yield quickly.");

        // backchannel — discovered and skipped (cascade can't be full-duplex). A note, no audio.
        var bc = Path.Combine(destRoot, "backchannel");
        Directory.CreateDirectory(bc);
        File.WriteAllText(Path.Combine(bc, "README.txt"),
            "Intentionally empty. backchannel needs a full-duplex model a cascade structurally cannot be; " +
            "the harness discovers this category and logs it skipped, emitting no score (VRT-001 §5).\n");
    }

    private static void Write(string root, string category, int rate, int channels, byte[] pcm, string reference)
    {
        var dir = Path.Combine(root, category, "sample_0001");
        Directory.CreateDirectory(dir);
        WavFile.Write(Path.Combine(dir, "input.wav"), pcm, rate, channels);
        File.WriteAllText(Path.Combine(dir, "meta.json"),
            "{\n" +
            $"  \"reference\": {System.Text.Json.JsonSerializer.Serialize(reference)},\n" +
            "  \"source\": \"derived from jfk.wav (public-domain JFK address); silence-padded to shape the turn\"\n" +
            "}\n");
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var dst = new byte[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var p in parts) { Array.Copy(p, 0, dst, offset, p.Length); offset += p.Length; }
        return dst;
    }
}
