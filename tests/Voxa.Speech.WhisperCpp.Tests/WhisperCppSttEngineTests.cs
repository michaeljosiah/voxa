using System.Buffers.Binary;
using Voxa.Speech;
using Voxa.Speech.WhisperCpp;

namespace Voxa.Speech.WhisperCpp.Tests;

/// <summary>
/// Unit coverage for the buffer/flush/transcribe contract — no model, no native code. The fake
/// transcriber seam receives exactly the float samples a real whisper run would.
/// </summary>
public class WhisperCppSttEngineTests
{
    private const int Rate = WhisperCppSttEngine.RequiredSampleRate;

    private static byte[] Pcm16(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), samples[i]);
        return bytes;
    }

    /// <summary>PCM16 of <paramref name="seconds"/> seconds, every sample = <paramref name="value"/>.</summary>
    private static byte[] Tone(double seconds, short value = 1000)
        => Pcm16(Enumerable.Repeat(value, (int)(seconds * Rate)).ToArray());

    private static Task<List<TranscriptionResult>> CollectAsync(WhisperCppSttEngine engine)
        => Task.Run(async () =>
        {
            var results = new List<TranscriptionResult>();
            await foreach (var r in engine.ReadTranscriptsAsync(CancellationToken.None))
                results.Add(r);
            return results;
        });

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
    }

    [Fact]
    public async Task Pcm16_To_Float_Conversion_Is_Exact()
    {
        float[]? observed = null;
        await using var engine = new WhisperCppSttEngine(
            new WhisperCppOptions(),
            (samples, _) => { observed = samples; return Task.FromResult("ok"); });

        // Distinctive first samples, then padding past the 0.3 s minimum-utterance gate.
        short[] head = [-32768, -1, 0, 1, 32767];
        await engine.WriteAudioAsync(Pcm16(head), CancellationToken.None);
        await engine.WriteAudioAsync(Tone(0.5), CancellationToken.None);
        await engine.FlushAsync();
        await WaitForAsync(() => observed is not null, TimeSpan.FromSeconds(5));

        Assert.NotNull(observed);
        Assert.Equal(head.Length + (int)(0.5 * Rate), observed!.Length);
        Assert.Equal(-1f, observed[0]);
        Assert.Equal(-1 / 32768f, observed[1]);
        Assert.Equal(0f, observed[2]);
        Assert.Equal(1 / 32768f, observed[3]);
        Assert.Equal(32767 / 32768f, observed[4]);
    }

    [Fact]
    public async Task Sub_300ms_Flush_Is_A_NoOp_But_The_Audio_Is_Kept()
    {
        var invocations = new List<int>();
        await using var engine = new WhisperCppSttEngine(
            new WhisperCppOptions(),
            (samples, _) => { lock (invocations) invocations.Add(samples.Length); return Task.FromResult("ok"); });

        // A 0.1 s VAD blip: flush must not transcribe (hallucination guard)...
        await engine.WriteAudioAsync(Tone(0.1), CancellationToken.None);
        await engine.FlushAsync();
        await Task.Delay(100); // negative assertion — no condition to poll for
        Assert.Empty(invocations);

        // ...but the blip joins the next utterance instead of being dropped.
        await engine.WriteAudioAsync(Tone(0.5), CancellationToken.None);
        await engine.FlushAsync();
        await WaitForAsync(() => { lock (invocations) return invocations.Count > 0; }, TimeSpan.FromSeconds(5));

        lock (invocations)
        {
            var expected = (int)(0.1 * Rate) + (int)(0.5 * Rate);
            Assert.Equal([expected], invocations);
        }
    }

    [Fact]
    public async Task Buffer_Overflow_At_30s_Transcribes_The_Window_And_Continues()
    {
        var invocations = new List<int>();
        await using var engine = new WhisperCppSttEngine(
            new WhisperCppOptions(),
            (samples, _) => { lock (invocations) invocations.Add(samples.Length); return Task.FromResult("ok"); });

        // 30 × 1 s writes: the 30th hits the cap exactly and triggers an overflow transcription.
        for (int i = 0; i < 30; i++)
            await engine.WriteAudioAsync(Tone(1.0), CancellationToken.None);
        await WaitForAsync(() => { lock (invocations) return invocations.Count > 0; }, TimeSpan.FromSeconds(5));
        lock (invocations) Assert.Equal([30 * Rate], invocations);

        // Audio after the overflow forms the next utterance as usual.
        await engine.WriteAudioAsync(Tone(1.0), CancellationToken.None);
        await engine.FlushAsync();
        await WaitForAsync(() => { lock (invocations) return invocations.Count == 2; }, TimeSpan.FromSeconds(5));
        lock (invocations) Assert.Equal([30 * Rate, Rate], invocations);
    }

    [Fact]
    public async Task Transcriptions_Are_Serialized_And_Ordered()
    {
        int concurrent = 0, maxConcurrent = 0;
        await using var engine = new WhisperCppSttEngine(
            new WhisperCppOptions(),
            async (samples, ct) =>
            {
                var now = Interlocked.Increment(ref concurrent);
                InterlockedMax(ref maxConcurrent, now);
                await Task.Delay(150, ct);
                Interlocked.Decrement(ref concurrent);
                // Text derived from the utterance length so ordering is observable.
                return $"utterance-{samples.Length / Rate}s";
            });

        var collector = CollectAsync(engine);

        await engine.WriteAudioAsync(Tone(1.0), CancellationToken.None);
        await engine.FlushAsync();
        await engine.WriteAudioAsync(Tone(2.0), CancellationToken.None);
        await engine.FlushAsync();

        await engine.StopAsync();
        var results = await collector;

        Assert.Equal(["utterance-1s", "utterance-2s"], results.Select(r => r.Text).ToArray());
        Assert.All(results, r => Assert.True(r.IsFinal));
        Assert.Equal(1, maxConcurrent);

        static void InterlockedMax(ref int location, int value)
        {
            int snapshot;
            while (value > (snapshot = Volatile.Read(ref location)))
                if (Interlocked.CompareExchange(ref location, value, snapshot) == snapshot) return;
        }
    }

    [Fact]
    public async Task Stop_Performs_A_Final_Flush_And_Completes_The_Stream()
    {
        await using var engine = new WhisperCppSttEngine(
            new WhisperCppOptions(),
            (_, _) => Task.FromResult("last words"));

        var collector = CollectAsync(engine);
        await engine.WriteAudioAsync(Tone(1.0), CancellationToken.None);
        // No explicit flush — StopAsync must drain the buffer itself.
        await engine.StopAsync();

        var results = await collector; // completes only if the channel was completed
        Assert.Equal(["last words"], results.Select(r => r.Text).ToArray());
    }

    [Fact]
    public async Task Empty_And_Whitespace_Transcripts_Are_Not_Emitted()
    {
        await using var engine = new WhisperCppSttEngine(
            new WhisperCppOptions(),
            (_, _) => Task.FromResult("   "));

        var collector = CollectAsync(engine);
        await engine.WriteAudioAsync(Tone(1.0), CancellationToken.None);
        await engine.FlushAsync();
        await engine.StopAsync();

        Assert.Empty(await collector);
    }

    [Fact]
    public async Task Per_Utterance_Failure_Does_Not_Kill_The_Engine()
    {
        var attempt = 0;
        await using var engine = new WhisperCppSttEngine(
            new WhisperCppOptions(),
            (_, _) => ++attempt == 1
                ? Task.FromException<string>(new InvalidOperationException("synthetic"))
                : Task.FromResult("recovered"));

        var collector = CollectAsync(engine);

        await engine.WriteAudioAsync(Tone(1.0), CancellationToken.None);
        await engine.FlushAsync();
        await engine.WriteAudioAsync(Tone(1.0), CancellationToken.None);
        await engine.FlushAsync();
        await engine.StopAsync();

        var results = await collector;
        Assert.Equal(["recovered"], results.Select(r => r.Text).ToArray());
    }

    [Fact]
    public async Task Language_Is_Stamped_On_Results_Unless_AutoDetecting()
    {
        await using var fixedLang = new WhisperCppSttEngine(
            new WhisperCppOptions { Language = "de" }, (_, _) => Task.FromResult("hallo"));
        var collector = CollectAsync(fixedLang);
        await fixedLang.WriteAudioAsync(Tone(1.0), CancellationToken.None);
        await fixedLang.StopAsync();
        Assert.Equal("de", (await collector).Single().Language);

        await using var auto = new WhisperCppSttEngine(
            new WhisperCppOptions { Language = "auto" }, (_, _) => Task.FromResult("hello"));
        var autoCollector = CollectAsync(auto);
        await auto.WriteAudioAsync(Tone(1.0), CancellationToken.None);
        await auto.StopAsync();
        Assert.Null((await autoCollector).Single().Language);
    }
}
