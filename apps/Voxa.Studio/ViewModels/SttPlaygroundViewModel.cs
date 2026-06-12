using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Voxa.Speech;
using Voxa.Speech.WhisperCpp;
using Voxa.Studio.Services;

namespace Voxa.Studio.ViewModels;

/// <summary>Where the playground's audio comes from (§6.1 input strip; one active at a time).</summary>
public enum SttSource { Fixture, File, Mic }

/// <summary>One Whisper catalog model in the selector, with its size and cache badge.</summary>
public sealed partial class SttModelRow : ObservableObject
{
    public required string Name { get; init; }
    public required long SizeBytes { get; init; }
    [ObservableProperty] private bool _isCached;
    public string SizeText => $"{SizeBytes / (1024.0 * 1024):F0} MB";
    public override string ToString() => Name;
}

/// <summary>
/// One final transcript card: the text, the captured utterance's waveform, and the
/// final-transcript latency (utterance end → final) measured standalone on this machine.
/// </summary>
public sealed record TranscriptCard(
    string Model, string Text, double UtteranceSeconds, double FinalLatencyMs,
    IReadOnlyList<float> Levels)
{
    public string MetaText => $"utterance {UtteranceSeconds:F1}s";
    public string LatencyText => $"final +{FinalLatencyMs:F0} ms";
}

/// <summary>
/// The STT Playground (VST-002 §6): how well and how fast does speech become text on this
/// machine, for any Whisper model, without composing a whole pipeline. Drives
/// <see cref="WhisperCppSttEngine"/> directly (the Voice-Lab pattern, STT edition). Avalonia-free
/// and headless-testable; long work happens in awaited commands like the Voice Lab.
/// </summary>
public sealed partial class SttPlaygroundViewModel : ObservableObject
{
    /// <summary>Mic recordings cap at Whisper's 30 s context window.</summary>
    internal const int MaxRecordSeconds = 30;

    private readonly StudioServices _services;
    private CancellationTokenSource? _recording;

    /// <summary>Test seam: replaces the real Whisper engine per model name.</summary>
    internal Func<string, ISpeechToTextEngine>? EngineFactoryOverride { get; set; }

    public SttPlaygroundViewModel(StudioServices services)
    {
        _services = services;

        // tiny→small reads as a quality ladder; quantized variants follow their parents.
        foreach (var name in WhisperCppModelCatalog.KnownModels
                     .OrderBy(n => n.Contains("-q") ? 1 : 0)
                     .ThenBy(n => n.StartsWith("tiny") ? 0 : n.StartsWith("base") ? 1 : 2)
                     .ThenBy(n => n, StringComparer.Ordinal))
        {
            if (!WhisperCppModelCatalog.TryGet(name, out var artifact)) continue;
            Models.Add(new SttModelRow { Name = name, SizeBytes = artifact.SizeBytes });
        }

        SelectedModel = Models.FirstOrDefault(m => m.Name == "tiny.en") ?? Models.FirstOrDefault();
        CompareModel = Models.FirstOrDefault(m => m.Name == "base.en");
        RefreshCacheState();
    }

    // ── bindable state ───────────────────────────────────────────────────────

    public ObservableCollection<SttModelRow> Models { get; } = new();
    public ObservableCollection<TranscriptCard> Cards { get; } = new();

    [ObservableProperty] private SttSource _source = SttSource.Fixture;
    [ObservableProperty] private SttModelRow? _selectedModel;
    [ObservableProperty] private SttModelRow? _compareModel;
    [ObservableProperty] private bool _sideBySide;
    [ObservableProperty] private string? _filePath;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(TranscribeCommand), nameof(ToggleRecordCommand))]
    private bool _isBusy;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowRecordButton))]
    private bool _isRecording;
    [ObservableProperty] private string _statusText = "Pick a source and a model, then transcribe.";
    [ObservableProperty] private string? _errorText;

    /// <summary>Reference text for the accuracy harness; WER recomputes live (§6.1).</summary>
    [ObservableProperty] private string _referenceText = string.Empty;
    [ObservableProperty] private WerResult? _wer;

    /// <summary>True while a Talk session owns the capture device — the mic source disables.</summary>
    [ObservableProperty] private bool _captureBlocked;

    public string FixtureName => "jfk.wav";
    public bool IsFixtureSource => Source == SttSource.Fixture;
    public bool IsFileSource => Source == SttSource.File;
    public bool IsMicSource => Source == SttSource.Mic;
    public bool ShowRecordButton => IsMicSource && !IsRecording;

    internal static string FixturePath => Path.Combine(AppContext.BaseDirectory, "fixtures", "jfk.wav");

    partial void OnSourceChanged(SttSource value)
    {
        OnPropertyChanged(nameof(IsFixtureSource));
        OnPropertyChanged(nameof(IsFileSource));
        OnPropertyChanged(nameof(IsMicSource));
        OnPropertyChanged(nameof(ShowRecordButton));
    }

    partial void OnReferenceTextChanged(string value) => RecomputeWer();

    public void RefreshCacheState()
    {
        foreach (var row in Models)
            row.IsCached = WhisperCppModelCatalog.TryGet(row.Name, out var a) && _services.ModelCache.IsCached(a);
    }

    [RelayCommand]
    private void SelectSource(string source) => Source = Enum.Parse<SttSource>(source);

    // ── transcription ────────────────────────────────────────────────────────

    private bool CanTranscribe() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanTranscribe))]
    private async Task TranscribeAsync()
    {
        ErrorText = null;
        byte[] pcm;
        try
        {
            pcm = Source switch
            {
                SttSource.Fixture => WavIo.ReadMono(FixturePath, WhisperCppSttEngine.RequiredSampleRate).Pcm,
                SttSource.File when !string.IsNullOrWhiteSpace(FilePath) =>
                    WavIo.ReadMono(FilePath!, WhisperCppSttEngine.RequiredSampleRate).Pcm,
                SttSource.File => throw new InvalidOperationException("Drop a WAV file or browse for one first."),
                _ => throw new InvalidOperationException("In mic mode, use the record button."),
            };
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            return;
        }

        await TranscribePcmAsync(pcm);
    }

    /// <summary>Record from the default mic until toggled off (or the 30 s cap), then transcribe.</summary>
    [RelayCommand(CanExecute = nameof(CanTranscribe))]
    private async Task ToggleRecordAsync()
    {
        if (IsRecording)
        {
            _recording?.Cancel();
            return;
        }

        var mic = _services.AudioDevice.CaptureEndpoints().FirstOrDefault(m => m.IsDefault)
                  ?? _services.AudioDevice.CaptureEndpoints().FirstOrDefault();
        if (mic is null)
        {
            ErrorText = "No microphone available.";
            return;
        }
        if (CaptureBlocked)
        {
            ErrorText = "The mic belongs to the live Talk session — stop it first.";
            return;
        }

        ErrorText = null;
        IsRecording = true;
        StatusText = $"Recording from {mic.DisplayName} — press again to stop (max {MaxRecordSeconds}s).";
        _recording = new CancellationTokenSource();

        var captured = new MemoryStream();
        const int maxBytes = MaxRecordSeconds * WhisperCppSttEngine.RequiredSampleRate * 2;
        try
        {
            await foreach (var frame in _services.AudioDevice.CaptureAsync(
                mic, WhisperCppSttEngine.RequiredSampleRate, _recording.Token))
            {
                captured.Write(frame.Span);
                if (captured.Length >= maxBytes) break;
            }
        }
        catch (OperationCanceledException) { /* stop pressed */ }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            _recording.Dispose();
            _recording = null;
            IsRecording = false;
        }

        if (captured.Length < WhisperCppSttEngine.RequiredSampleRate / 2) // < 0.25 s is a misclick
        {
            StatusText = "Recording too short — nothing to transcribe.";
            return;
        }
        await TranscribePcmAsync(captured.ToArray());
    }

    /// <summary>Run the selected model (and the compare model when side-by-side) over one utterance.</summary>
    internal async Task TranscribePcmAsync(byte[] pcm)
    {
        if (SelectedModel is null) return;
        IsBusy = true;
        try
        {
            var primary = await RunModelAsync(SelectedModel, pcm);

            // One whisper context at a time (§6.1) — the comparison runs sequentially.
            if (SideBySide && CompareModel is { } other && other.Name != SelectedModel.Name)
            {
                var secondary = await RunModelAsync(other, pcm);
                StatusText = Summarize(primary, secondary);
            }
            else
            {
                StatusText = $"{primary.Model}: final +{primary.FinalLatencyMs:F0} ms for a " +
                             $"{primary.UtteranceSeconds:F1}s utterance on this machine.";
            }

            RecomputeWer();
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            StatusText = "Transcription failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<TranscriptCard> RunModelAsync(SttModelRow model, byte[] pcm)
    {
        if (!model.IsCached && EngineFactoryOverride is null
            && WhisperCppModelCatalog.TryGet(model.Name, out var artifact))
        {
            StatusText = $"Downloading {model.Name} ({model.SizeText})…";
            await _services.ModelCache.PrefetchAsync([artifact]);
            model.IsCached = true;
        }

        StatusText = $"Transcribing with {model.Name}…";
        await using var engine = EngineFactoryOverride?.Invoke(model.Name)
            ?? new WhisperCppSttEngine(
                new WhisperCppOptions { Model = model.Name, Language = "en" }, _services.ModelCache);
        await engine.StartAsync(CancellationToken.None);

        var clock = new Stopwatch();
        double lastFinalMs = 0;
        var collector = Task.Run(async () =>
        {
            var texts = new List<string>();
            await foreach (var r in engine.ReadTranscriptsAsync(CancellationToken.None))
            {
                if (!r.IsFinal) continue;
                texts.Add(r.Text.Trim());
                lastFinalMs = clock.Elapsed.TotalMilliseconds;
            }
            return texts;
        });

        // File-mode audio is fed as fast as the engine accepts it; the engine buffers per
        // utterance, so "utterance end" is the flush — that's where the latency clock starts.
        const int frameBytes = 2 * WhisperCppSttEngine.RequiredSampleRate / 50;
        for (int i = 0; i < pcm.Length; i += frameBytes)
        {
            var len = Math.Min(frameBytes, pcm.Length - i);
            await engine.WriteAudioAsync(pcm.AsMemory(i, len), CancellationToken.None);
        }
        clock.Start();
        await engine.FlushAsync();
        await engine.StopAsync();
        var pieces = await collector;

        var card = new TranscriptCard(
            model.Name,
            string.Join(" ", pieces.Where(t => t.Length > 0)),
            pcm.Length / 2.0 / WhisperCppSttEngine.RequiredSampleRate,
            lastFinalMs,
            PcmEnvelope.Compute(pcm, 48));

        Cards.Insert(0, card);
        while (Cards.Count > 12) Cards.RemoveAt(Cards.Count - 1);
        return card;
    }

    private string Summarize(TranscriptCard primary, TranscriptCard secondary)
    {
        var delta = secondary.FinalLatencyMs - primary.FinalLatencyMs;
        var speed = delta >= 0
            ? $"{secondary.Model} {delta:F0} ms slower"
            : $"{secondary.Model} {-delta:F0} ms faster";

        if (ReferenceText.Trim().Length > 0)
        {
            var a = WordErrorRate.Compute(ReferenceText, primary.Text);
            var b = WordErrorRate.Compute(ReferenceText, secondary.Text);
            return $"Same audio: {primary.Model} {a.WerText}% WER · {secondary.Model} {b.WerText}% WER · {speed}.";
        }
        return $"Same audio: {primary.Model} +{primary.FinalLatencyMs:F0} ms · {secondary.Model} +{secondary.FinalLatencyMs:F0} ms ({speed}).";
    }

    // ── accuracy harness ─────────────────────────────────────────────────────

    /// <summary>WER of the newest transcript against the reference text, alignment included.</summary>
    private void RecomputeWer()
    {
        var hypothesis = Cards.FirstOrDefault()?.Text;
        Wer = ReferenceText.Trim().Length > 0 && !string.IsNullOrEmpty(hypothesis)
            ? WordErrorRate.Compute(ReferenceText, hypothesis)
            : null;
    }
}
