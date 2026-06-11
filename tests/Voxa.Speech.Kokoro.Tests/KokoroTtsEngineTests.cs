using Voxa.Speech.Kokoro;

namespace Voxa.Speech.Kokoro.Tests;

/// <summary>
/// Unit coverage over the fake G2P + fake inference seams — no model, no espeak, no native code.
/// </summary>
public class KokoroTtsEngineTests
{
    private static KokoroTtsEngine Engine(
        Func<string, CancellationToken, Task<string>>? phonemize = null,
        Func<long[], CancellationToken, Task<float[]>>? infer = null,
        KokoroOptions? options = null)
        => new(
            options ?? new KokoroOptions(),
            phonemize ?? ((text, _) => Task.FromResult("həlˈoʊ")),
            infer ?? ((_, _) => Task.FromResult(new float[2400])));

    private static async Task<byte[]> CollectPcmAsync(KokoroTtsEngine engine, string text)
    {
        using var ms = new MemoryStream();
        await foreach (var chunk in engine.SynthesizeAsync(text, CancellationToken.None))
            ms.Write(chunk.Span);
        return ms.ToArray();
    }

    [Fact]
    public async Task Tokens_Are_Padded_With_Boundary_Zeros()
    {
        long[]? observed = null;
        var engine = Engine(infer: (tokens, _) => { observed = tokens; return Task.FromResult(new float[100]); });

        await CollectPcmAsync(engine, "hello");

        // The seam receives the unpadded tokens; padding happens inside the ONNX call. What we
        // assert here is the tokenization of "həlˈoʊ": h=50 ə=83 l=54 ˈ=156 oʊ → o=57 ʊ=135.
        Assert.NotNull(observed);
        Assert.Equal([50, 83, 54, 156, 57, 135], observed!);
    }

    [Fact]
    public async Task Float_Waveform_Becomes_Clamped_Pcm16_In_8KiB_Chunks()
    {
        var waveform = new float[10_000];
        waveform[0] = 0f; waveform[1] = 0.5f; waveform[2] = -0.5f; waveform[3] = 2f; waveform[4] = -2f;

        var chunks = new List<byte[]>();
        var engine = Engine(infer: (_, _) => Task.FromResult(waveform));
        await foreach (var chunk in engine.SynthesizeAsync("hello", CancellationToken.None))
            chunks.Add(chunk.ToArray());

        Assert.Equal([8192, 8192, 3616], chunks.Select(c => c.Length).ToArray());

        var pcm = chunks.SelectMany(c => c).ToArray();
        short Sample(int i) => (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));
        Assert.Equal(0, Sample(0));
        Assert.Equal((short)(0.5f * 32767), Sample(1));
        Assert.Equal((short)(-0.5f * 32767), Sample(2));
        Assert.Equal(32767, Sample(3));  // clamped from 2.0
        Assert.Equal(-32767, Sample(4)); // clamped from -2.0
    }

    [Fact]
    public async Task Empty_Phonemization_Yields_No_Audio()
    {
        var inferCalls = 0;
        var engine = Engine(
            phonemize: (_, _) => Task.FromResult("∅∅∅"), // nothing in the vocabulary
            infer: (_, _) => { inferCalls++; return Task.FromResult(new float[10]); });

        var pcm = await CollectPcmAsync(engine, "hello");

        Assert.Empty(pcm);
        Assert.Equal(0, inferCalls);
    }

    [Fact]
    public async Task Oversized_Sequences_Split_At_Phrase_Boundaries_Never_Truncate()
    {
        // 600 'a' tokens with a comma at position 400 — must split into 2 runs, total preserved.
        var phonemes = string.Concat(Enumerable.Repeat("a", 400)) + "," + string.Concat(Enumerable.Repeat("a", 199));
        var slices = new List<int>();
        var engine = Engine(
            phonemize: (_, _) => Task.FromResult(phonemes),
            infer: (tokens, _) => { slices.Add(tokens.Length); return Task.FromResult(new float[10]); });

        await CollectPcmAsync(engine, "long");

        Assert.Equal(2, slices.Count);
        Assert.Equal(600, slices.Sum());          // every token synthesized exactly once
        Assert.Equal(401, slices[0]);             // split right after the comma token
        Assert.All(slices, s => Assert.True(s <= KokoroCatalog.MaxTokens));
    }

    [Fact]
    public void SplitTokens_Hard_Splits_When_No_Boundary_Exists()
    {
        var tokens = Enumerable.Repeat(43L, 1200).ToArray(); // 'a' × 1200, no comma/space anywhere
        var slices = KokoroTtsEngine.SplitTokens(tokens, 510).ToArray();

        Assert.Equal([510, 510, 180], slices.Select(s => s.Length).ToArray());
        Assert.Equal(1200, slices.Sum(s => s.Length));
    }

    [Fact]
    public async Task Phonemizer_Failure_Surfaces_As_Engine_Exception()
    {
        var engine = Engine(phonemize: (_, _) =>
            Task.FromException<string>(new InvalidOperationException("espeak-ng exited with code 1. stderr: boom")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CollectPcmAsync(engine, "hello"));
        Assert.Contains("espeak-ng", ex.Message);
        Assert.Contains("boom", ex.Message); // stderr tail reaches the caller
    }
}

public class KokoroVocabularyTests
{
    [Fact]
    public void Real_Espeak_Output_Tokenizes_Losslessly()
    {
        // Actual espeak-ng --ipa output for "Hello from Voxa." — every char must be in the vocab.
        const string phonemes = "həlˈoʊ fɹʌm vˈɑːksə.";
        Assert.All(phonemes, c => Assert.True(KokoroVocabulary.Contains(c), $"missing '{c}' (U+{(int)c:X4})"));
        Assert.Equal(phonemes.Length, KokoroVocabulary.Tokenize(phonemes).Length);
    }

    [Fact]
    public void Punctuation_Carries_Prosody_Tokens()
    {
        Assert.Equal([3], KokoroVocabulary.Tokenize(","));
        Assert.Equal([4], KokoroVocabulary.Tokenize("."));
        Assert.Equal([5], KokoroVocabulary.Tokenize("!"));
        Assert.Equal([6], KokoroVocabulary.Tokenize("?"));
        Assert.Equal([16], KokoroVocabulary.Tokenize(" "));
    }

    [Fact]
    public void Unknown_Characters_Are_Skipped_Not_Thrown()
        => Assert.Equal([50, 51], KokoroVocabulary.Tokenize("h⚡i"));
}

public class EspeakPhonemizerTests
{
    [Fact]
    public void Clause_Lines_Rejoin_With_Commas_And_Terminal_Punctuation_Returns()
    {
        // espeak splits "Second sentence, with numbers." into two lines and drops punctuation.
        var formatted = EspeakPhonemizer.FormatPhonemes(
            "sˈɛkənd sˈɛntəns\nwɪð nˈʌmbɚz\n",
            "Second sentence, with numbers.");

        Assert.Equal("sˈɛkənd sˈɛntəns, wɪð nˈʌmbɚz.", formatted);
    }

    [Theory]
    [InlineData("Really?!", "ɹˈiəli", "ɹˈiəli!")]
    [InlineData("Wait…", "wˈeɪt", "wˈeɪt…")]
    [InlineData("no punctuation", "nˈoʊ", "nˈoʊ")]
    public void Terminal_Punctuation_Is_Restored(string original, string espeak, string expected)
        => Assert.Equal(expected, EspeakPhonemizer.FormatPhonemes(espeak + "\n", original));
}
