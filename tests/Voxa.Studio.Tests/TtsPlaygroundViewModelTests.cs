using System.Runtime.CompilerServices;
using Voxa.Speech;
using Voxa.Studio.Services;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Tests;

/// <summary>
/// VST-002 D2 §7: the TTS lab's catalog and pins (carried over from WS3), plus the new take
/// history, A/B/X blind test, and batch bench — against a fake engine (real synthesis runs in
/// the LocalModels lane).
/// </summary>
public class TtsPlaygroundViewModelTests
{
    /// <summary>Emits one second of audible PCM in two chunks with a tiny TTFB.</summary>
    private sealed class FakeTtsEngine(int sampleRate) : ITextToSpeechEngine
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
            string text, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            var chunk = new byte[sampleRate]; // half a second of PCM16
            for (int i = 0; i < chunk.Length; i += 2) chunk[i] = 0x40;
            yield return chunk;
            yield return chunk;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static TtsPlaygroundViewModel Vm()
    {
        var vm = new TtsPlaygroundViewModel(TestSupport.Services());
        vm.EngineFactoryOverride = row => new FakeTtsEngine(row.SampleRate);
        return vm;
    }

    [Fact]
    public void Catalog_Lists_All_Piper_And_Kokoro_Voices_With_Metadata()
    {
        var vm = new TtsPlaygroundViewModel(TestSupport.Services());

        // 7 Piper + 5 Kokoro voices are pinned today; growing the catalog should
        // consciously update this expectation.
        Assert.Equal(7, vm.Voices.Count(v => v.Engine == "Piper"));
        Assert.Equal(5, vm.Voices.Count(v => v.Engine == "Kokoro"));

        var amy = Assert.Single(vm.Voices, v => v.Name == "en_US-amy-low");
        Assert.Equal(16000, amy.SampleRate);
        Assert.False(amy.IsCached); // isolated empty temp cache

        var heart = Assert.Single(vm.Voices, v => v.Name == "af_heart");
        Assert.Equal(24000, heart.SampleRate); // Kokoro's fixed model rate

        Assert.All(vm.Voices, v => Assert.Null(v.TtfbMs)); // nothing measured before a take
    }

    [Fact]
    public void Pinning_Holds_Two_Independent_Slots_And_Toggles()
    {
        var vm = new TtsPlaygroundViewModel(TestSupport.Services());
        var first = vm.Voices[0];
        var second = vm.Voices[1];
        var third = vm.Voices[2];

        vm.PinCommand.Execute(first);
        Assert.Equal("A", first.Pin);
        Assert.Same(first, vm.PinnedA);

        vm.PinCommand.Execute(second);
        Assert.Equal("B", second.Pin);

        // A third pin replaces B (A stays — it's the reference voice).
        vm.PinCommand.Execute(third);
        Assert.Null(second.Pin);
        Assert.Same(third, vm.PinnedB);
        Assert.Same(first, vm.PinnedA);

        // Pinning an already-pinned row unpins it.
        vm.PinCommand.Execute(first);
        Assert.Null(first.Pin);
        Assert.Null(vm.PinnedA);
    }

    [Fact]
    public async Task Synthesis_Lands_A_Take_With_Numbers_And_Waveform()
    {
        var vm = Vm();
        var row = vm.Voices[0];

        var take = await vm.SynthesizeTakeAsync(row, "hello world");

        Assert.Same(take, Assert.Single(vm.Takes));
        Assert.Equal(row.Name, take.Voice);
        Assert.Equal(1.0, take.Seconds, 1);     // 2 × half-second chunks
        Assert.True(take.TtfbMs >= 0);
        Assert.True(take.Rtf > 0);
        Assert.NotEmpty(take.Levels);
        Assert.Equal(take.TtfbMs, row.TtfbMs);  // the catalog row shows the latest numbers
    }

    [Fact]
    public async Task Identical_Take_Is_Reused_Not_Resynthesized()
    {
        var vm = Vm();
        var row = vm.Voices[0];

        var first = await vm.SynthesizeTakeAsync(row, "same text");
        var second = await vm.SynthesizeTakeAsync(row, "same text");

        Assert.Same(first, second);
        Assert.Single(vm.Takes);

        await vm.SynthesizeTakeAsync(row, "different text");
        Assert.Equal(2, vm.Takes.Count); // new text = a genuinely new take
    }

    [Fact]
    public async Task Abx_Round_Reveals_The_Truth_After_A_Vote()
    {
        var vm = Vm();
        vm.PinCommand.Execute(vm.Voices[0]);
        vm.PinCommand.Execute(vm.Voices[1]);

        vm.StartAbxRoundCommand.Execute(null);
        Assert.True(vm.AbxRoundActive);
        Assert.False(vm.AbxCanReveal); // no reveal before a vote — blind means blind

        await vm.PlayXCommand.ExecuteAsync(null); // synthesizes the hidden voice — must not throw

        vm.VoteXCommand.Execute("A");
        Assert.True(vm.AbxCanReveal);

        vm.RevealAbxCommand.Execute(null);
        Assert.False(vm.AbxRoundActive);
        Assert.Contains("X was", vm.AbxStatusText);
        // The reveal names the actual pinned voice, never a placeholder.
        Assert.True(
            vm.AbxStatusText.Contains(vm.PinnedA!.Name) || vm.AbxStatusText.Contains(vm.PinnedB!.Name));
    }

    [Fact]
    public void Abx_Needs_Two_Pins()
    {
        var vm = Vm();
        vm.StartAbxRoundCommand.Execute(null);
        Assert.False(vm.AbxRoundActive);
        Assert.Contains("Pin two voices", vm.AbxStatusText);
    }

    [Fact]
    public async Task Batch_Bench_Produces_Percentiles_Per_Checked_Voice()
    {
        var vm = Vm();
        vm.Voices[0].IsBenchSelected = true;
        vm.Voices[1].IsBenchSelected = true;

        await vm.RunBenchCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.BenchRows.Count);
        Assert.All(vm.BenchRows, r =>
        {
            Assert.True(r.P50Ms >= 0);
            Assert.True(r.P95Ms >= r.P50Ms); // p95 can never undercut the median
            Assert.True(r.RtfMean > 0);
        });
        // The whole deck landed in the take history for replay.
        Assert.Equal(2 * TtsPlaygroundViewModel.StressPhrases.Count, vm.Takes.Count);
    }

    [Fact]
    public void Bench_Is_Blocked_While_A_Talk_Session_Is_Live()
    {
        var vm = Vm();
        vm.PlaybackBlocked = true;
        Assert.False(vm.RunBenchCommand.CanExecute(null));
        Assert.False(vm.SynthesizeCommand.CanExecute(null));
    }

    [Fact]
    public void Nearest_Rank_Percentile_Is_Exact_On_Small_Decks()
    {
        double[] values = [10, 20, 30, 40, 50];
        Assert.Equal(30, TtsPlaygroundViewModel.Percentile(values, 0.50));
        Assert.Equal(50, TtsPlaygroundViewModel.Percentile(values, 0.95));
        Assert.Equal(10, TtsPlaygroundViewModel.Percentile(values, 0.01));
        Assert.Equal(0, TtsPlaygroundViewModel.Percentile([], 0.5));
    }

    [Fact]
    public async Task Playback_Position_Tracks_The_Clock_And_Stops_At_The_End()
    {
        var vm = Vm();
        var take = await vm.SynthesizeTakeAsync(vm.Voices[0], "scrub me");

        await vm.PlayFromAsync(take, 0.5);
        Assert.True(vm.IsPlaying);
        Assert.Equal(0.5, vm.PlaybackPosition, 2);

        // The fake take is 1 s; from the midpoint the clock outruns it quickly.
        await Task.Delay(700);
        vm.UpdatePlayback();
        Assert.True(vm.PlaybackPosition > 0.5);

        await Task.Delay(500);
        vm.UpdatePlayback();
        Assert.False(vm.IsPlaying);     // finished
        Assert.Equal(1, vm.PlaybackPosition);
    }
}
