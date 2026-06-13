using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Voxa.Speech;
using Voxa.Speech.Kokoro;
using Voxa.Speech.Piper;
using Voxa.Studio.Audio;
using Voxa.Studio.Services;

namespace Voxa.Studio.ViewModels;

/// <summary>One auditionable voice from the pinned local catalogs.</summary>
public sealed partial class VoiceRow : ObservableObject
{
    public required string Engine { get; init; }
    public required string Name { get; init; }
    public required int SampleRate { get; init; }
    public required long SizeBytes { get; init; }

    [ObservableProperty] private bool _isCached;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double? _ttfbMs;
    [ObservableProperty] private double? _rtf;
    [ObservableProperty] private string? _pin; // "A", "B", or null
    [ObservableProperty] private bool _isBenchSelected;

    public string SizeText => $"{SizeBytes / (1024.0 * 1024):F0} MB";
    public string RateText => $"{SampleRate / 1000.0:0.#} kHz";
    public string TtfbText => TtfbMs is { } t ? $"{t:F0} ms" : "—";
    public string RtfText => Rtf is { } r ? $"{r:F2}×" : "—";
}

/// <summary>
/// One synthesis result in the take history (§7): the audio, what made it, and the numbers —
/// replayable, scrubbable, exportable.
/// </summary>
public sealed record TtsTake(
    string Voice, string Engine, string Text, byte[] Pcm, int SampleRate,
    double TtfbMs, double Rtf, IReadOnlyList<float> Levels)
{
    public double Seconds => Pcm.Length / 2.0 / SampleRate;
    public string MetaText => $"{Seconds:F1}s · {TtfbMs:F0} ms · {Rtf:F2}×";
}

/// <summary>One phrase-deck entry: a short chip label, the full sentence in the tooltip.</summary>
public sealed record StressPhrase(string Label, string Text);

/// <summary>One row of the batch-bench table: TTFB percentiles + mean RTF over the phrase deck.</summary>
public sealed record BenchRow(string Voice, string Engine, double P50Ms, double P95Ms, double RtfMean)
{
    public string P50Text => $"{P50Ms:F0}";
    public string P95Text => $"{P95Ms:F0}";
    public string RtfText => $"{RtfMean:F2}";
}

/// <summary>
/// The TTS Playground (VST-002 §7): the v1 Voice Lab matured into a lab. Carries over the full
/// catalog, real engines, TTFB/RTF, A/B pins, WAV export, and talk-session device arbitration;
/// adds the take history with a waveform scrubber, the A/B/X blind test, the stress-phrase deck,
/// and the batch bench. Avalonia-free; the view's timer calls <see cref="UpdatePlayback"/>.
/// </summary>
public sealed partial class TtsPlaygroundViewModel : ObservableObject
{
    /// <summary>The §7 phrase deck — the sentences that actually break TTS, not pangrams.</summary>
    public static readonly IReadOnlyList<StressPhrase> StressPhrases =
    [
        new("currency & dates", "That comes to $1,204.50, due 03/14/2026."),
        new("code aloud", "Read HTTP/2 and SQL aloud, then call kubectl get pods."),
        new("homographs", "The bandage was wound around the wound."),
        new("diacritics", "A naïve café résumé, San José, Zürich."),
        new("long clause", "Although the order shipped on Thursday, which the tracking page confirmed twice, it still arrived a full week later than the estimate we quoted."),
    ];

    private readonly StudioServices _services;
    private readonly Stopwatch _playClock = new();
    private double _playOffsetSeconds;     // where the current playback started (seek support)
    private CancellationTokenSource? _playback;
    private bool? _abxVotedA;              // the user's vote this round; null = not voted
    private bool _abxXIsA;                 // the hidden coin flip

    /// <summary>Test seam: replaces the real Piper/Kokoro engine per voice row.</summary>
    internal Func<VoiceRow, ITextToSpeechEngine>? EngineFactoryOverride { get; set; }

    public TtsPlaygroundViewModel(StudioServices services)
    {
        _services = services;

        foreach (var name in PiperVoiceCatalog.KnownVoices)
        {
            if (!PiperVoiceCatalog.TryGet(name, out var voice)) continue;
            Voices.Add(new VoiceRow
            {
                Engine = "Piper",
                Name = name,
                SampleRate = voice.SampleRate,
                SizeBytes = voice.Onnx.SizeBytes + voice.Json.SizeBytes,
            });
        }

        KokoroCatalog.TryGetModel("int8", out var kokoroModel);
        foreach (var name in KokoroCatalog.KnownVoices)
        {
            if (!KokoroCatalog.TryGetVoice(name, out var style)) continue;
            Voices.Add(new VoiceRow
            {
                Engine = "Kokoro",
                Name = name,
                SampleRate = KokoroCatalog.OutputSampleRate,
                SizeBytes = kokoroModel.SizeBytes + style.SizeBytes,
            });
        }

        SelectedVoice = Voices.FirstOrDefault();
        RefreshCacheState();
    }

    /// <summary>
    /// Select a catalog voice by engine + name (the Voices section's "audition" deep-link). Returns
    /// false when no matching local row exists (e.g. a cloud/cloned voice the lab can't synthesize),
    /// leaving the current selection untouched.
    /// </summary>
    public bool TrySelectVoice(string engine, string name)
    {
        var row = Voices.FirstOrDefault(v =>
            string.Equals(v.Engine, engine, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
        if (row is null) return false;
        SelectedVoice = row;
        return true;
    }

    // ── bindable state ───────────────────────────────────────────────────────

    public ObservableCollection<VoiceRow> Voices { get; } = new();
    public ObservableCollection<TtsTake> Takes { get; } = new();
    public ObservableCollection<BenchRow> BenchRows { get; } = new();

    [ObservableProperty] private string _text = "Your order shipped this morning and arrives Thursday.";
    [ObservableProperty] private VoiceRow? _selectedVoice;
    [ObservableProperty] private string _statusText = "Pick a voice and synthesize. First use downloads that voice.";
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private VoiceRow? _pinnedA;
    [ObservableProperty] private VoiceRow? _pinnedB;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SynthesizeCommand), nameof(RunBenchCommand))]
    private bool _isBusy;

    /// <summary>The take under the scrubber.</summary>
    [ObservableProperty] private TtsTake? _currentTake;
    [ObservableProperty] private double _playbackPosition = -1;
    [ObservableProperty] private bool _isPlaying;

    /// <summary>A/B/X round state, shown verbatim under the X button.</summary>
    [ObservableProperty] private string _abxStatusText = "Pin two voices, then start a round.";
    [ObservableProperty] private bool _abxRoundActive;
    [ObservableProperty] private bool _abxCanReveal;

    /// <summary>Disclosure state: the experiments stay folded until invited (P2, calm by default).</summary>
    [ObservableProperty] private bool _isAbxOpen;
    [ObservableProperty] private bool _isBenchOpen;

    /// <summary>Set true while a Talk session owns the output device — playback and bench disable.</summary>
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SynthesizeCommand), nameof(RunBenchCommand))]
    private bool _playbackBlocked;

    public void RefreshCacheState()
    {
        foreach (var row in Voices)
            row.IsCached = IsRowCached(row);
    }

    private bool IsRowCached(VoiceRow row) => row.Engine switch
    {
        "Piper" => PiperVoiceCatalog.TryGet(row.Name, out var v)
                   && _services.ModelCache.IsCached(v.Onnx) && _services.ModelCache.IsCached(v.Json),
        "Kokoro" => KokoroCatalog.TryGetModel("int8", out var m) && _services.ModelCache.IsCached(m)
                    && KokoroCatalog.TryGetVoice(row.Name, out var s) && _services.ModelCache.IsCached(s),
        _ => false,
    };

    [RelayCommand]
    private void UseStressPhrase(string phrase) => Text = phrase;

    // ── synthesis + take history ─────────────────────────────────────────────

    private bool CanSynthesize() => !IsBusy && !PlaybackBlocked;

    [RelayCommand(CanExecute = nameof(CanSynthesize))]
    private async Task SynthesizeAsync()
    {
        if (SelectedVoice is null) return;
        ErrorText = null;
        IsBusy = true;
        try
        {
            var take = await SynthesizeTakeAsync(SelectedVoice, Text);
            await PlayFromAsync(take, 0);
            StatusText = $"{take.Voice}: TTFB {take.TtfbMs:F0} ms, RTF {take.Rtf:F2}× on this machine.";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            StatusText = $"Failed to synthesize with {SelectedVoice?.Name}.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task ReplayTakeAsync(TtsTake take) =>
        PlaybackBlocked ? Task.CompletedTask : PlayFromAsync(take, 0);

    /// <summary>Synthesize one take with the REAL engine and land it in the history.</summary>
    internal Task<TtsTake> SynthesizeTakeAsync(VoiceRow row, string text)
        => SynthesizeTakeAsync(row, text, reuseExisting: true);

    private async Task<TtsTake> SynthesizeTakeAsync(VoiceRow row, string text, bool reuseExisting)
    {
        // An identical take is a replay, not a new synthesis — the history stays meaningful.
        // The bench passes false: a measurement run must measure, never replay old numbers (P4).
        if (reuseExisting && Takes.FirstOrDefault(t => t.Voice == row.Name && t.Text == text) is { } existing)
            return existing;

        row.IsBusy = true;
        try
        {
            StatusText = row.IsCached
                ? $"Synthesizing with {row.Name}…"
                : $"Downloading {row.Name} ({row.SizeText}) then synthesizing…";

            await using var engine = CreateEngine(row);
            await engine.StartAsync(CancellationToken.None);

            using var pcm = new MemoryStream();
            double? ttfb = null;
            var clock = Stopwatch.StartNew();
            await foreach (var chunk in engine.SynthesizeAsync(text, CancellationToken.None))
            {
                ttfb ??= clock.Elapsed.TotalMilliseconds;
                pcm.Write(chunk.Span); // engines may pool buffers — copy before the next chunk
            }
            var wallMs = clock.Elapsed.TotalMilliseconds;

            var bytes = pcm.ToArray();
            var audioSeconds = bytes.Length / 2.0 / row.SampleRate;
            var take = new TtsTake(
                row.Name, row.Engine, text, bytes, row.SampleRate,
                ttfb ?? 0, audioSeconds > 0 ? wallMs / 1000.0 / audioSeconds : 0,
                PcmEnvelope.Compute(bytes, 56));

            row.TtfbMs = take.TtfbMs;
            row.Rtf = take.Rtf;
            row.IsCached = IsRowCached(row);

            Takes.Insert(0, take);
            while (Takes.Count > 20) Takes.RemoveAt(Takes.Count - 1);
            return take;
        }
        finally
        {
            row.IsBusy = false;
        }
    }

    private ITextToSpeechEngine CreateEngine(VoiceRow row) =>
        EngineFactoryOverride?.Invoke(row) ?? row.Engine switch
        {
            "Piper" => new PiperTtsEngine(
                new PiperOptions { Voice = row.Name, OutputSampleRate = row.SampleRate, MaxProcesses = 1 },
                _services.ModelCache),
            "Kokoro" => new KokoroTtsEngine(
                new KokoroOptions { Voice = row.Name, Precision = "int8" },
                _services.ModelCache),
            _ => throw new InvalidOperationException($"Unknown engine '{row.Engine}'."),
        };

    // ── playback + scrubber ──────────────────────────────────────────────────

    /// <summary>Play <paramref name="take"/> from a 0..1 position; the scrubber calls this on seek.</summary>
    public async Task PlayFromAsync(TtsTake take, double position)
    {
        if (PlaybackBlocked) return;
        CurrentTake = take;

        _playback?.Cancel();
        _playback = new CancellationTokenSource();
        var ct = _playback.Token;

        var device = _services.AudioDevice;
        await device.FlushRenderAsync();

        // Byte offset must land on a sample boundary or playback turns to static.
        var offset = (int)(Math.Clamp(position, 0, 1) * take.Pcm.Length) & ~1;
        _playOffsetSeconds = offset / 2.0 / take.SampleRate;
        PlaybackPosition = Math.Clamp(position, 0, 1);

        try
        {
            await device.StartRenderAsync(
                device.RenderEndpoints().FirstOrDefault(s => s.IsDefault)
                    ?? device.RenderEndpoints().FirstOrDefault()
                    ?? new AudioEndpoint("default", "Default", true),
                take.SampleRate, ct);
            await device.RenderAsync(take.Pcm.AsMemory(offset), ct);
            _playClock.Restart();
            IsPlaying = true;
        }
        catch (OperationCanceledException) { /* superseded by a newer play */ }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            IsPlaying = false;
        }
    }

    /// <summary>
    /// Called by the view's render timer (and tests): playback position is wall-clock over the
    /// render queue — the device renders gap-free, so elapsed time IS the position.
    /// </summary>
    public void UpdatePlayback()
    {
        if (!IsPlaying || CurrentTake is null) return;
        var seconds = CurrentTake.Seconds;
        if (seconds <= 0) { IsPlaying = false; return; }

        var pos = (_playOffsetSeconds + _playClock.Elapsed.TotalSeconds) / seconds;
        if (pos >= 1)
        {
            PlaybackPosition = 1;
            IsPlaying = false;
            return;
        }
        PlaybackPosition = pos;
    }

    // ── A/B pins + A/B/X blind test ──────────────────────────────────────────

    [RelayCommand]
    private void Pin(VoiceRow row)
    {
        if (row.Pin == "A") { row.Pin = null; PinnedA = null; return; }
        if (row.Pin == "B") { row.Pin = null; PinnedB = null; return; }
        if (PinnedA is null) { PinnedA = row; row.Pin = "A"; }
        else if (PinnedB is null) { PinnedB = row; row.Pin = "B"; }
        else { PinnedB.Pin = null; PinnedB = row; row.Pin = "B"; }
        ResetAbx();
    }

    [RelayCommand] private Task PlayPinnedAAsync() => PlayPinnedAsync(PinnedA);
    [RelayCommand] private Task PlayPinnedBAsync() => PlayPinnedAsync(PinnedB);

    private async Task PlayPinnedAsync(VoiceRow? row)
    {
        if (row is null || IsBusy || PlaybackBlocked) return;
        IsBusy = true;
        try
        {
            var take = await SynthesizeTakeAsync(row, Text);
            await PlayFromAsync(take, 0);
        }
        catch (Exception ex) { ErrorText = ex.Message; }
        finally { IsBusy = false; }
    }

    /// <summary>Start a blind round: X is randomly A or B — vote, then reveal (§7, honest by design).</summary>
    [RelayCommand]
    private void StartAbxRound()
    {
        if (PinnedA is null || PinnedB is null)
        {
            AbxStatusText = "Pin two voices (A and B) first.";
            return;
        }
        _abxXIsA = Random.Shared.Next(2) == 0;
        _abxVotedA = null;
        AbxRoundActive = true;
        AbxCanReveal = false;
        AbxStatusText = "X is randomly A or B. Listen to all three, vote, then reveal.";
    }

    [RelayCommand]
    private async Task PlayXAsync()
    {
        if (!AbxRoundActive || IsBusy || PlaybackBlocked) return;
        var row = _abxXIsA ? PinnedA : PinnedB;
        if (row is null) return;
        IsBusy = true;
        try
        {
            var take = await SynthesizeTakeAsync(row, Text);
            await PlayFromAsync(take, 0);
        }
        catch (Exception ex) { ErrorText = ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void VoteX(string choice)
    {
        if (!AbxRoundActive) return;
        _abxVotedA = choice == "A";
        AbxCanReveal = true;
        AbxStatusText = $"You voted X = {choice}. Reveal when ready.";
    }

    [RelayCommand]
    private void RevealAbx()
    {
        if (!AbxRoundActive || _abxVotedA is null) return;
        var truth = _abxXIsA ? PinnedA : PinnedB;
        var verdict = _abxVotedA == _abxXIsA ? "You were right." : "You were wrong.";
        AbxStatusText = $"X was {(_abxXIsA ? "A" : "B")} · {truth?.Name}. {verdict}";
        AbxRoundActive = false;
        AbxCanReveal = false;
    }

    private void ResetAbx()
    {
        AbxRoundActive = false;
        AbxCanReveal = false;
        _abxVotedA = null;
        AbxStatusText = "Pin two voices, then start a round.";
    }

    // ── batch bench ──────────────────────────────────────────────────────────

    private bool CanRunBench() => !IsBusy && !PlaybackBlocked;

    /// <summary>
    /// Synthesize the whole phrase deck on every bench-checked voice (sequentially — measured
    /// numbers must not fight each other for cores) → TTFB p50/p95 + mean RTF per voice.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRunBench))]
    private async Task RunBenchAsync()
    {
        var selected = Voices.Where(v => v.IsBenchSelected).ToList();
        if (selected.Count == 0 && SelectedVoice is { } current) selected = [current];
        if (selected.Count == 0) return;

        ErrorText = null;
        IsBusy = true;
        BenchRows.Clear();
        try
        {
            for (int v = 0; v < selected.Count; v++)
            {
                var row = selected[v];
                var ttfbs = new List<double>(StressPhrases.Count);
                var rtfs = new List<double>(StressPhrases.Count);
                for (int p = 0; p < StressPhrases.Count; p++)
                {
                    StatusText = $"Bench {row.Name} ({v + 1}/{selected.Count}) · phrase {p + 1}/{StressPhrases.Count}…";
                    // Always fresh: reusing a cached take would report numbers from an earlier
                    // run (and the history cap could mix cached and fresh within one run).
                    var take = await SynthesizeTakeAsync(row, StressPhrases[p].Text, reuseExisting: false);
                    ttfbs.Add(take.TtfbMs);
                    rtfs.Add(take.Rtf);
                }
                BenchRows.Add(new BenchRow(
                    row.Name, row.Engine, Percentile(ttfbs, 0.50), Percentile(ttfbs, 0.95),
                    rtfs.Average()));
            }
            StatusText = $"Bench done: {selected.Count} voice(s) × {StressPhrases.Count} phrases, sequential, this machine.";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            StatusText = "Bench failed.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Nearest-rank percentile — honest for the deck's small N (no interpolation theater).</summary>
    internal static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToArray();
        var rank = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Length - 1)];
    }

    [RelayCommand]
    private async Task ExportBenchCsvAsync()
    {
        if (BenchRows.Count == 0) return;
        try
        {
            var csv = new StringBuilder("voice,engine,ttfb_p50_ms,ttfb_p95_ms,rtf_mean\n");
            foreach (var r in BenchRows)
                csv.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"{r.Voice},{r.Engine},{r.P50Ms:F1},{r.P95Ms:F1},{r.RtfMean:F3}"));

            var path = Path.Combine(ExportDir(), $"voxa-tts-bench-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            await File.WriteAllTextAsync(path, csv.ToString());
            StatusText = $"Exported {path}";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    // ── export ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ExportWavAsync(TtsTake take)
    {
        try
        {
            var path = Path.Combine(ExportDir(), $"voxa-{take.Voice}-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
            await File.WriteAllBytesAsync(path, WavIo.Write(take.Pcm, take.SampleRate));
            StatusText = $"Exported {path}";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    private static string ExportDir()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        return string.IsNullOrEmpty(dir) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : dir;
    }
}
