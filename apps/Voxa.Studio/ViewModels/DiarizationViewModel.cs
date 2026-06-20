using System.Buffers.Binary;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Voxa.Audio.Diarization;
using Voxa.Audio.Diarization.Onnx;
using Voxa.Audio.Onnx;
using Voxa.Studio.Services;

namespace Voxa.Studio.ViewModels;

/// <summary>One detected speech region on the clip's timeline (absolute seconds from the start).</summary>
public sealed record SpeechSpan(int Index, double StartSeconds, double EndSeconds)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
    public string RangeText => $"{StartSeconds:F2}s – {EndSeconds:F2}s";
    public string DurationText => $"{DurationSeconds:F2}s";
}

/// <summary>The segmentation of one clip: the speech regions over <c>[0, TotalSeconds]</c>.</summary>
public sealed record SpeechTimeline(double TotalSeconds, IReadOnlyList<SpeechSpan> Speech)
{
    public int RegionCount => Speech.Count;
    public double SpeechSeconds => Speech.Sum(s => s.DurationSeconds);
    public double SpeechFraction => TotalSeconds > 0 ? SpeechSeconds / TotalSeconds : 0;
}

/// <summary>
/// The Diarization analytics view: load a clip (or the bundled sample), run the pyannote
/// segmentation model (VLS-005 WS2) on the shared <see cref="OnnxModelHost"/>, and show the
/// speech-activity timeline. This is the segmentation stage of diarization — speech vs. silence.
/// Per-speaker labelling ("who spoke when") needs the speaker-embedding model, a deferred follow-up,
/// so the view is honest about showing activity, not identities.
///
/// <para>Avalonia-free and headless-testable: a fake <see cref="ISpeakerSegmentation"/> (via
/// <see cref="SegmentationFactoryOverride"/>) drives the VM with no model or network. The real
/// engine downloads the pinned model on first run (then offline), off the UI thread.</para>
/// </summary>
public sealed partial class DiarizationViewModel : ObservableObject
{
    private readonly StudioServices _services;
    private readonly OnnxModelHost _host = new(); // process-wide session cache; cheap to construct, no load here

    /// <summary>Test seam: supply a fake segmentation so VM tests need no real model or network.</summary>
    internal Func<ISpeakerSegmentation>? SegmentationFactoryOverride;

    public DiarizationViewModel(StudioServices services) => _services = services;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _useFixture = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private string _filePath = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _statusText =
        "Pick a clip (or use the bundled sample) and run speech segmentation.";
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private SpeechTimeline? _timeline;
    [ObservableProperty] private string _summaryText = "";

    /// <summary>True when there's an error to show — drives the red note's visibility.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorText);
    partial void OnErrorTextChanged(string? value) => OnPropertyChanged(nameof(HasError));

    /// <summary>The detected regions, newest run replacing the last (the list the view binds to).</summary>
    public ObservableCollection<SpeechSpan> Segments { get; } = new();

    /// <summary>The bundled clip the playgrounds also use — known content, ships in-repo.</summary>
    internal static string FixturePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "jfk.wav");

    private bool CanRun() => !IsBusy && (UseFixture || !string.IsNullOrWhiteSpace(FilePath));

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsBusy = true;
        ErrorText = null;
        Segments.Clear();
        Timeline = null;
        SummaryText = "";
        try
        {
            const int sampleRate = 16000; // pyannote segmentation-3.0 runs at 16 kHz
            var path = UseFixture ? FixturePath : FilePath;

            StatusText = "Reading audio…";
            var audio = await Task.Run(() => DecodeMono(path, sampleRate));
            if (audio.Length == 0)
            {
                StatusText = "Nothing to segment.";
                ErrorText = "The clip is empty.";
                return;
            }

            var segmentation = SegmentationFactoryOverride?.Invoke() ?? await ResolveEngineAsync();

            StatusText = "Segmenting…";
            var windows = await Task.Run(() => segmentation.Segment(audio, sampleRate));

            var total = audio.Length / (double)sampleRate;
            var spans = windows
                .SelectMany(w => w.Regions)
                .OrderBy(r => r.Start)
                .Select((r, i) => new SpeechSpan(i + 1, r.Start, r.End))
                .ToList();

            var timeline = new SpeechTimeline(total, spans);
            Timeline = timeline;
            foreach (var span in spans) Segments.Add(span);
            SummaryText = spans.Count == 0
                ? $"No speech detected in {total:F1}s."
                : $"{spans.Count} speech region(s) · {timeline.SpeechSeconds:F1}s of speech in " +
                  $"{total:F1}s ({timeline.SpeechFraction:P0}).";
            StatusText = "Done.";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            StatusText = "Segmentation failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Resolve the pinned model (download on first run, then offline) and bind the engine.</summary>
    private async Task<ISpeakerSegmentation> ResolveEngineAsync()
    {
        var artifact = PyannoteSegmentationCatalog.Model;
        if (!_services.ModelCache.IsCached(artifact))
        {
            StatusText = $"Downloading the segmentation model (~{artifact.SizeBytes / (1024 * 1024)} MB)…";
            await _services.ModelCache.PrefetchAsync([artifact]);
        }
        var modelPath = await _services.ModelCache.ResolveAsync(artifact);
        // Constructing the engine builds the ORT InferenceSession synchronously (a cold session load can be
        // slow) — do it off the UI thread, like the decode/Segment work, so clicking Run never freezes the window.
        return await Task.Run<ISpeakerSegmentation>(() => new PyannoteOnnxSegmentation(modelPath, _host));
    }

    /// <summary>Decode a 16-bit PCM WAV to mono float samples at <paramref name="sampleRate"/>.</summary>
    internal static float[] DecodeMono(string path, int sampleRate)
    {
        var wav = WavIo.ReadMono(path, sampleRate);
        var samples = new float[wav.Pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(wav.Pcm.AsSpan(i * 2, 2)) / 32768f;
        return samples;
    }
}
