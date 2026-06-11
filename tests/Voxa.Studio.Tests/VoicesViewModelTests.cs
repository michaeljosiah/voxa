using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Tests;

/// <summary>
/// VST-001 WS3-A1/A3: the Voice Lab lists every pinned catalog voice with correct metadata and
/// cache state, and the A/B pinning state machine holds two independent slots. (WS3-A2 — real
/// synthesis — runs in the LocalModels-trait integration suite, not here.)
/// </summary>
public class VoicesViewModelTests
{
    [Fact]
    public void Catalog_Lists_All_Piper_And_Kokoro_Voices_With_Metadata()
    {
        var vm = new VoicesViewModel(TestSupport.Services());

        // 7 Piper voices + 5 Kokoro voices are pinned today; growing the catalog should
        // consciously update this expectation.
        Assert.Equal(7, vm.Voices.Count(v => v.Engine == "Piper"));
        Assert.Equal(5, vm.Voices.Count(v => v.Engine == "Kokoro"));

        var amy = Assert.Single(vm.Voices, v => v.Name == "en_US-amy-low");
        Assert.Equal(16000, amy.SampleRate);
        Assert.False(amy.IsCached); // isolated empty temp cache

        var heart = Assert.Single(vm.Voices, v => v.Name == "af_heart");
        Assert.Equal(24000, heart.SampleRate); // Kokoro's fixed model rate

        // Nothing has measurements before an audition.
        Assert.All(vm.Voices, v => Assert.Null(v.TtfbMs));
    }

    [Fact]
    public void Pinning_Holds_Two_Independent_Slots_And_Toggles()
    {
        var vm = new VoicesViewModel(TestSupport.Services());
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
    public void ToWav_Produces_A_Valid_Riff_Header()
    {
        var pcm = new byte[3200]; // 100 ms @ 16 kHz
        var wav = VoicesViewModel.ToWav(pcm, 16000);

        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal((byte)'F', wav[3]);
        Assert.Equal(44 + pcm.Length, wav.Length);
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));          // sample rate
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));              // bits per sample
        Assert.Equal(pcm.Length, BitConverter.ToInt32(wav, 40));      // data size
    }
}
