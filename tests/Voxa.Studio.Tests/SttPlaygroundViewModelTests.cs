using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Voxa.Speech;
using Voxa.Studio.Services;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Tests;

/// <summary>
/// VST-002 D2 §6: the STT lab's catalog, transcription flow, latency stamping, accuracy harness,
/// and side-by-side comparison — against a fake engine (real Whisper runs in the LocalModels lane).
/// </summary>
public class SttPlaygroundViewModelTests
{
    /// <summary>Final-only fake: echoes a canned transcript per model after the flush, like Whisper.</summary>
    private sealed class FakeSttEngine(string transcript) : ISpeechToTextEngine
    {
        private readonly Channel<TranscriptionResult> _results = Channel.CreateUnbounded<TranscriptionResult>();
        public long BytesWritten { get; private set; }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
        {
            BytesWritten += pcm.Length;
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var r in _results.Reader.ReadAllAsync(ct))
                yield return r;
        }

        public Task FlushAsync()
        {
            _results.Writer.TryWrite(new TranscriptionResult(transcript, IsFinal: true));
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            _results.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static SttPlaygroundViewModel Vm(Func<string, string> transcriptFor)
    {
        var vm = new SttPlaygroundViewModel(TestSupport.Services());
        vm.EngineFactoryOverride = model => new FakeSttEngine(transcriptFor(model));
        return vm;
    }

    private static byte[] OneSecondPcm() => new byte[16000 * 2];

    [Fact]
    public void Catalog_Lists_Every_Pinned_Whisper_Model_Uncached_In_A_Temp_Cache()
    {
        var vm = new SttPlaygroundViewModel(TestSupport.Services());

        // 12 pinned GGML models today; growing the catalog should consciously update this.
        Assert.Equal(12, vm.Models.Count);
        Assert.All(vm.Models, m => Assert.False(m.IsCached));
        Assert.Equal("tiny.en", vm.SelectedModel?.Name); // the 2-minutes-to-wow default
        Assert.All(vm.Models, m => Assert.True(m.SizeBytes > 0));
    }

    [Fact]
    public async Task Transcription_Produces_A_Card_With_Text_Duration_And_Latency()
    {
        var vm = Vm(_ => "ask not what your country can do for you");

        await vm.TranscribePcmAsync(OneSecondPcm());

        var card = Assert.Single(vm.Cards);
        Assert.Equal("ask not what your country can do for you", card.Text);
        Assert.Equal("tiny.en", card.Model);
        Assert.Equal(1.0, card.UtteranceSeconds, 2);
        Assert.True(card.FinalLatencyMs >= 0);
        Assert.NotEmpty(card.Levels);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Reference_Text_Drives_The_Wer_Harness_Live()
    {
        var vm = Vm(_ => "ask what your country can do for you");

        await vm.TranscribePcmAsync(OneSecondPcm());
        Assert.Null(vm.Wer); // no reference yet — no number to show

        vm.ReferenceText = "ask not what your country can do for you";
        Assert.NotNull(vm.Wer);
        Assert.Equal(1, vm.Wer!.Deletions); // "not" went missing
        Assert.Equal(1.0 / 9, vm.Wer.Wer, 5);

        vm.ReferenceText = "";
        Assert.Null(vm.Wer); // clearing the reference clears the harness
    }

    [Fact]
    public async Task Side_By_Side_Runs_Both_Models_Sequentially_And_Summarizes()
    {
        var vm = Vm(model => model == "tiny.en" ? "the quick brown facts" : "the quick brown fox");
        vm.SideBySide = true;
        vm.CompareModel = vm.Models.Single(m => m.Name == "base.en");
        vm.ReferenceText = "the quick brown fox";

        await vm.TranscribePcmAsync(OneSecondPcm());

        Assert.Equal(2, vm.Cards.Count);
        Assert.Equal("base.en", vm.Cards[0].Model);  // newest first
        Assert.Equal("tiny.en", vm.Cards[1].Model);
        Assert.Contains("WER", vm.StatusText);       // the §6 trade-off sentence
        Assert.Contains("tiny.en", vm.StatusText);
        Assert.Contains("base.en", vm.StatusText);
    }

    [Fact]
    public async Task Card_History_Is_Capped()
    {
        var vm = Vm(_ => "hello");
        for (int i = 0; i < 15; i++)
            await vm.TranscribePcmAsync(OneSecondPcm());
        Assert.Equal(12, vm.Cards.Count);
    }

    [Fact]
    public async Task Mic_Record_Is_Refused_When_No_Microphone_Exists()
    {
        // NullAudioDevice (headless/test) exposes no endpoints — the lab says so instead of hanging.
        var vm = new SttPlaygroundViewModel(TestSupport.Services()) { Source = SttSource.Mic };
        await vm.ToggleRecordCommand.ExecuteAsync(null);
        Assert.False(vm.IsRecording);
        Assert.Contains("microphone", vm.ErrorText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fixture_Ships_With_The_App()
    {
        // §6.1: jfk.wav is bundled and replayable — the keyless first-run path.
        Assert.True(File.Exists(SttPlaygroundViewModel.FixturePath),
            $"missing {SttPlaygroundViewModel.FixturePath}");
        var wav = WavIo.ReadMono(SttPlaygroundViewModel.FixturePath, 16000);
        Assert.True(wav.Pcm.Length > 16000 * 2 * 5); // several seconds of speech
    }
}
