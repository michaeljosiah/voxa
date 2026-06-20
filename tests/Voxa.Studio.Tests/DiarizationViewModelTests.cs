using Voxa.Audio.Diarization;
using Voxa.Studio.Services;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Tests;

/// <summary>
/// The Diarization view runs a clip through a segmentation engine and renders a speech-activity
/// timeline. Exercised with a synthetic WAV + a fake <see cref="ISpeakerSegmentation"/>, so the VM
/// logic (decode → run → flatten regions → summary) is covered with no real model or network.
/// </summary>
public class DiarizationViewModelTests
{
    private sealed class FakeSegmentation(params (double Start, double End)[] regions) : ISpeakerSegmentation
    {
        public IReadOnlyList<SegmentationWindow> Segment(ReadOnlySpan<float> audio, int sampleRate) =>
            [new SegmentationWindow(0, audio.Length / (double)sampleRate,
                regions.Select(r => new SpeechRegion(r.Start, r.End)).ToList())];
    }

    /// <summary>Write a silent 16-bit PCM WAV (content is irrelevant — the engine is faked).</summary>
    private static string WriteWav(double seconds, int sampleRate = 16000)
    {
        var pcm = new byte[(int)(seconds * sampleRate) * 2];
        var path = Path.Combine(TestSupport.TempDir(), "clip.wav");
        File.WriteAllBytes(path, WavIo.Write(pcm, sampleRate));
        return path;
    }

    [Fact]
    public async Task Runs_A_Clip_And_Builds_The_Speech_Timeline()
    {
        var vm = new DiarizationViewModel(TestSupport.Services())
        {
            UseFixture = false,
            FilePath = WriteWav(seconds: 6),
            SegmentationFactoryOverride = () => new FakeSegmentation((1.0, 2.0), (3.0, 5.5)),
        };

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorText);
        Assert.NotNull(vm.Timeline);
        Assert.Equal(6.0, vm.Timeline!.TotalSeconds, 1);   // duration from the decoded clip
        Assert.Equal(2, vm.Segments.Count);
        Assert.Equal(1, vm.Segments[0].Index);
        Assert.Equal(1.0, vm.Segments[0].StartSeconds, 3);
        Assert.Equal(2.0, vm.Segments[0].EndSeconds, 3);
        Assert.Equal(3.5, vm.Timeline.SpeechSeconds, 3);   // (2-1) + (5.5-3)
        Assert.Contains("region", vm.SummaryText);
    }

    [Fact]
    public async Task No_Regions_Reports_No_Speech()
    {
        var vm = new DiarizationViewModel(TestSupport.Services())
        {
            UseFixture = false,
            FilePath = WriteWav(seconds: 2),
            SegmentationFactoryOverride = () => new FakeSegmentation(),
        };

        await vm.RunCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorText);
        Assert.Empty(vm.Segments);
        Assert.Contains("No speech", vm.SummaryText);
    }

    [Fact]
    public async Task A_Missing_File_Surfaces_An_Error_Not_A_Crash()
    {
        var vm = new DiarizationViewModel(TestSupport.Services())
        {
            UseFixture = false,
            FilePath = Path.Combine(TestSupport.TempDir(), "does-not-exist.wav"),
            SegmentationFactoryOverride = () => new FakeSegmentation((0, 1)),
        };

        await vm.RunCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorText);
        Assert.Null(vm.Timeline);
        Assert.False(vm.IsBusy); // released even on failure
    }

    [Fact]
    public void Run_Is_Blocked_Until_A_File_Is_Chosen()
    {
        var vm = new DiarizationViewModel(TestSupport.Services()) { UseFixture = false, FilePath = "" };
        Assert.False(vm.RunCommand.CanExecute(null));

        vm.FilePath = WriteWav(seconds: 1);
        Assert.True(vm.RunCommand.CanExecute(null));

        // The bundled-sample source needs no path.
        vm.UseFixture = true;
        vm.FilePath = "";
        Assert.True(vm.RunCommand.CanExecute(null));
    }
}
