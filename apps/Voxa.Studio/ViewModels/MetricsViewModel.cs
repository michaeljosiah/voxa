using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Voxa.AspNetCore;
using Voxa.Diagnostics;
using Voxa.Speech;
using Voxa.Studio.Audio;
using Voxa.Studio.Services;

namespace Voxa.Studio.ViewModels;

/// <summary>One run in the workbench's run list; check two to compare.</summary>
public sealed partial class RunRowVm : ObservableObject
{
    private readonly Action _checkedChanged;

    public RunRowVm(RunBundle bundle, Action checkedChanged)
    {
        Bundle = bundle;
        _checkedChanged = checkedChanged;
    }

    public RunBundle Bundle { get; }
    [ObservableProperty] private bool _isChecked;

    public string Title => $"#{Bundle.Number}  {Bundle.Label}";
    public string Sub => $"{Bundle.SourceDescription} · {Bundle.StartedAt.LocalDateTime:yyyy-MM-dd HH:mm}";
    public string P50Text => Bundle.Stats.TurnCount > 0 ? $"{Bundle.Stats.TtfbP50:F0} ms" : "—";

    partial void OnIsCheckedChanged(bool value) => _checkedChanged();
}

/// <summary>
/// The Run &amp; Metrics workbench (VST-002 §9): turn sessions into evidence. A run is one
/// configuration exercised by one input source; bundles persist as JSON under the user profile
/// and the run list is a folder scan. Avalonia-free like every Studio VM — hub events buffer in
/// a concurrent queue and the view's timer calls <see cref="DrainPending"/>.
/// </summary>
public sealed partial class MetricsViewModel : ObservableObject
{
    private readonly StudioServices _services;
    private readonly ConcurrentQueue<DiagnosticEvent> _pending = new();
    private readonly List<RunEvent> _recorded = new();
    private readonly Dictionary<string, double> _liveStages = new();

    private RunStore _store;
    private TalkSession? _session;
    private ServiceProvider? _runProvider;
    private CancellationTokenSource? _subscription;
    private TaskCompletionSource? _botStopped;
    private Stopwatch _clock = new();
    private DateTimeOffset _runStarted;
    private string _sourceDescription = "";
    private bool _modelsWereCached;

    /// <summary>Test seam: replaces the ephemeral-container session creation.</summary>
    internal Func<IServiceProvider, IStudioAudioDevice, TalkSession>? SessionFactoryOverride;

    private string? _runsDirOverride;

    /// <summary>Test seam: redirects the bundle store (defaults to ~/voxa-runs).</summary>
    internal string? RunsDirOverride
    {
        get => _runsDirOverride;
        set
        {
            _runsDirOverride = value;
            _store = new RunStore(value);
            RefreshRuns();
        }
    }

    /// <summary>A scripted utterance that gets no bot reply still advances after this long.</summary>
    internal int UtteranceTimeoutMs { get; set; } = 30_000;

    /// <summary>Grace after the last utterance so the trailing <c>audio_out</c> stage lands.</summary>
    internal int ScriptGraceMs { get; set; } = 600;

    public MetricsViewModel(StudioServices services)
    {
        _services = services;
        _store = new RunStore();
        ScriptFiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ScriptSummaryText));
        RefreshRuns();
    }

    // ── source selection (§9.1: scripted | wav | mic) ────────────────────────

    /// <summary>0 scripted deck, 1 single WAV, 2 live mic.</summary>
    [ObservableProperty] private int _sourceIndex;

    public bool IsScriptedSource => SourceIndex == 0;
    public bool IsWavSource => SourceIndex == 1;
    public bool IsMicSource => SourceIndex == 2;

    partial void OnSourceIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsScriptedSource));
        OnPropertyChanged(nameof(IsWavSource));
        OnPropertyChanged(nameof(IsMicSource));
    }

    public ObservableCollection<string> ScriptFiles { get; } = new();
    public string ScriptSummaryText => ScriptFiles.Count == 1
        ? "1 utterance queued"
        : $"{ScriptFiles.Count} utterances queued";
    [ObservableProperty] private string? _wavPath = SttPlaygroundViewModel.FixturePath;
    [ObservableProperty] private double _gapMs = 1500;
    public string GapText => $"{(int)GapMs} ms";

    partial void OnGapMsChanged(double value) => OnPropertyChanged(nameof(GapText));

    [RelayCommand]
    private void AddFixtureToScript() => ScriptFiles.Add(SttPlaygroundViewModel.FixturePath);

    /// <summary>The view's file picker lands paths here (multi-select).</summary>
    public void AddScriptFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths) ScriptFiles.Add(p);
    }

    [RelayCommand]
    private void ClearScript() => ScriptFiles.Clear();

    // ── run lifecycle ────────────────────────────────────────────────────────

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _runBlocked;

    [ObservableProperty] private string? _statusText = "No run yet — pick a source and press Run.";
    [ObservableProperty] private string? _errorText;

    // The compact live header (§9.1): heavy charts wait for completion.
    [ObservableProperty] private string _elapsedText = "00:00";
    [ObservableProperty] private int _turnCount;
    [ObservableProperty] private string _lastTtfbText = "—";
    [ObservableProperty] private int _runErrorCount;

    private bool CanRun() => !IsRunning && !RunBlocked;
    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        ErrorText = null;
        var script = SourceIndex switch
        {
            0 => (IReadOnlyList<string>)ScriptFiles.ToList(),
            1 => WavPath is { Length: > 0 } wav ? [wav] : [],
            _ => [],
        };
        if (SourceIndex != 2 && script.Count == 0)
        {
            ErrorText = "Add at least one utterance WAV.";
            return;
        }

        try
        {
            // An ephemeral container over the live config with diagnostics forced on — the same
            // pattern the Builder runs with; the app's own configuration is untouched.
            var config = new ConfigurationBuilder()
                .AddConfiguration(_services.Configuration)
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Voxa:Diagnostics:Enabled"] = "true" })
                .Build();
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Information));
            services.AddSingleton<IConfiguration>(config);
            services.AddVoxa(config);
            _runProvider = services.BuildServiceProvider();

            var cache = new VoxaModelCache(VoxaModelCacheOptions.FromConfiguration(config.GetSection("Voxa")));
            var missing = ActiveConfigArtifacts.Missing(config, cache);
            _modelsWereCached = missing.Count == 0; // recorded in the bundle's context (R4)
            // With a session factory override the real chain never composes — prefetching for it
            // would be wasted bytes (and a network touch in the default test suite).
            if (missing.Count > 0 && SessionFactoryOverride is null)
            {
                var totalMb = missing.Sum(a => a.SizeBytes) / (1024 * 1024);
                StatusText = $"Downloading {missing.Count} model file(s), ~{totalMb} MB…";
                var progress = new Progress<VoxaPrefetchProgress>(p =>
                    StatusText = $"Downloading {p.ArtifactId}  ({p.CompletedCount + (p.Completed ? 0 : 1)}/{p.TotalCount})…");
                await cache.PrefetchAsync(missing, progress);
            }

            var scripted = SourceIndex != 2 ? new ScriptedAudioDevice() : null;
            var device = (IStudioAudioDevice?)scripted ?? _services.AudioDevice;
            _session = SessionFactoryOverride is not null
                ? SessionFactoryOverride(_runProvider, device)
                : TalkSession.Create(_runProvider, device);

            _recorded.Clear();
            _liveStages.Clear();
            TurnCount = 0;
            RunErrorCount = 0;
            LastTtfbText = "—";
            ElapsedText = "00:00";
            _runStarted = DateTimeOffset.Now;
            _clock = Stopwatch.StartNew();
            _sourceDescription = SourceIndex switch
            {
                0 => $"scripted · {script.Count} utterance(s) · {(int)GapMs} ms gaps",
                1 => $"wav · {Path.GetFileName(WavPath)}",
                _ => "mic",
            };

            _subscription = new CancellationTokenSource();
            _ = Task.Run(() => SubscribeAsync(_session, _subscription.Token));
            _ = WatchSessionAsync(_session);

            await _session.StartAsync(Pick(device.CaptureEndpoints()), Pick(device.RenderEndpoints()));
            IsRunning = true;
            StatusText = SourceIndex == 2 ? "Recording from mic — Stop ends the run." : "Replaying the script…";

            if (scripted is not null)
                _ = DriveScriptAsync(scripted, script, (int)GapMs, _session.InputSampleRate, _subscription.Token);
        }
        catch (Exception ex)
        {
            ErrorText = ex switch
            {
                OptionsValidationException ove => string.Join(" ", ove.Failures),
                _ => ex.Message,
            };
            StatusText = "Failed to start.";
            await TearDownAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => StopRunAsync();

    /// <summary>
    /// Replay the deck turn-paced: each utterance waits for the bot's reply (or the timeout) plus
    /// the configured gap, so runs with the same deck are comparable turn for turn. Scripted runs
    /// end themselves.
    /// </summary>
    private async Task DriveScriptAsync(
        ScriptedAudioDevice device, IReadOnlyList<string> files, int gapMs, int sampleRate, CancellationToken ct)
    {
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                _botStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var wav = WavIo.ReadMono(files[i], sampleRate);
                device.EnqueueUtterance(wav.Pcm);
                StatusText = $"Utterance {i + 1}/{files.Count}…";
                await Task.WhenAny(_botStopped.Task, Task.Delay(UtteranceTimeoutMs, ct));
                await Task.Delay(gapMs, ct);
            }
            await Task.Delay(ScriptGraceMs, ct);
            await StopRunAsync();
        }
        catch (OperationCanceledException)
        {
            // User pressed Stop mid-script — StopRunAsync already handled the run.
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            await StopRunAsync();
        }
    }

    private async Task StopRunAsync()
    {
        if (!IsRunning) return;
        IsRunning = false; // gates the driver/user double-call

        _subscription?.Cancel();
        _subscription = null;
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }
        DrainPending(); // pull whatever the subscription already buffered
        _runProvider?.Dispose();
        _runProvider = null;

        var bundle = BuildBundle();
        var path = _store.Save(bundle);
        RefreshRuns();
        SelectedRun = Runs.FirstOrDefault(r => r.Bundle.Number == bundle.Number);
        StatusText = $"Run #{bundle.Number} saved — {Path.GetFileName(path)}";
    }

    private async Task TearDownAsync()
    {
        _subscription?.Cancel();
        _subscription = null;
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }
        _runProvider?.Dispose();
        _runProvider = null;
        IsRunning = false;
    }

    private async Task WatchSessionAsync(TalkSession session)
    {
        try { await session.WaitAsync(); }
        catch (Exception ex) { ErrorText = $"Pipeline stopped: {ex.Message}"; }
    }

    private async Task SubscribeAsync(TalkSession session, CancellationToken ct)
    {
        try
        {
            await foreach (var e in session.Hub.SubscribeAsync(ct))
                _pending.Enqueue(e);
        }
        catch (OperationCanceledException) { }
    }

    internal void EnqueueForTest(DiagnosticEvent e) => _pending.Enqueue(e);

    private static AudioEndpoint Pick(IReadOnlyList<AudioEndpoint> endpoints) =>
        endpoints.FirstOrDefault(e => e.IsDefault)
        ?? (endpoints.Count > 0 ? endpoints[0] : throw new InvalidOperationException("No audio endpoints available."));

    // ── drain: record events + the live header ──────────────────────────────

    /// <summary>Called by the view's timer (and directly by tests). Single-threaded by contract.</summary>
    public void DrainPending()
    {
        while (_pending.TryDequeue(out var e))
        {
            switch (e)
            {
                case TurnEvent turn:
                    _recorded.Add(new RunEvent { Micros = e.TimestampMicros, Kind = "turn", Edge = turn.Edge.ToString() });
                    if (turn.Edge == TurnEdge.BotStopped) _botStopped?.TrySetResult();
                    break;
                case TranscriptEvent { IsFinal: true } t:
                    _recorded.Add(new RunEvent { Micros = e.TimestampMicros, Kind = "transcript", Text = t.Text });
                    break;
                case TtsChunkEvent chunk:
                    _recorded.Add(new RunEvent { Micros = e.TimestampMicros, Kind = "tts", Bytes = chunk.Bytes, SampleRate = chunk.SampleRate });
                    break;
                case StageLatencyEvent stage:
                    _recorded.Add(new RunEvent { Micros = e.TimestampMicros, Kind = "stage", Stage = stage.Stage, Ms = stage.Ms });
                    _liveStages[stage.Stage] = stage.Ms;
                    if (stage.Stage == "audio_out")
                    {
                        TurnCount++;
                        LastTtfbText = $"{_liveStages.Values.Sum():F0} ms";
                        _liveStages.Clear();
                    }
                    break;
                case PipelineErrorEvent err:
                    _recorded.Add(new RunEvent { Micros = e.TimestampMicros, Kind = "error", Source = err.Source, Text = err.Message });
                    RunErrorCount++;
                    break;
                // VadWindowEvent / AgentDeltaEvent: not recorded — see RunEvent's doc comment.
            }
        }
        if (IsRunning) ElapsedText = _clock.Elapsed.ToString(@"mm\:ss");
    }

    private RunBundle BuildBundle()
    {
        var voxa = _services.Configuration.GetSection("Voxa");
        var bundle = new RunBundle
        {
            Label = $"{voxa["Stt"] ?? "WhisperCpp"}·{voxa["Agent:Provider"] ?? "Echo"}·{voxa["Tts"] ?? "Piper"}".ToLowerInvariant(),
            StartedAt = _runStarted,
            DurationSeconds = _clock.Elapsed.TotalSeconds,
            SourceDescription = _sourceDescription,
            Profile = voxa["Profile"] ?? "Default",
            Config = SnapshotConfig(_services.Configuration),
            Context = RunContext.Capture(_modelsWereCached),
            Events = new List<RunEvent>(_recorded),
        };
        bundle.Stats = RunStats.Compute(bundle.Events);
        return bundle;
    }

    /// <summary>The active config, flattened — bundles are made to be shared, so no secrets.</summary>
    internal static Dictionary<string, string?> SnapshotConfig(IConfiguration configuration) =>
        configuration.GetSection("Voxa").AsEnumerable(makePathsRelative: false)
            .Where(p => p.Value is not null && !p.Key.EndsWith("ApiKey", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(p => p.Key, p => p.Value);

    // ── the run list, selection, compare ─────────────────────────────────────

    public ObservableCollection<RunRowVm> Runs { get; } = new();

    [ObservableProperty] private RunRowVm? _selectedRun;
    [ObservableProperty] private RunCompare? _compare;
    [ObservableProperty] private string? _focusedStage;

    public bool HasRuns => Runs.Count > 0;
    public bool HasSelection => SelectedRun is not null;

    // Chart-facing state for the selected run (populated on completion, never live — §9.1).
    [ObservableProperty] private IReadOnlyList<RunTurn> _selectedTurns = [];
    [ObservableProperty] private string _takeawayText = "";
    [ObservableProperty] private string _p50Text = "—";
    [ObservableProperty] private string _p95Text = "—";
    [ObservableProperty] private string _maxText = "—";
    [ObservableProperty] private string _rtfText = "—";
    [ObservableProperty] private string _metaText = "";
    [ObservableProperty] private string? _deltaText;
    [ObservableProperty] private bool _deltaImproved;

    partial void OnSelectedRunChanged(RunRowVm? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        if (value is null)
        {
            SelectedTurns = [];
            TakeawayText = "";
            P50Text = P95Text = MaxText = RtfText = "—";
            MetaText = "";
            DeltaText = null;
            return;
        }

        var b = value.Bundle;
        SelectedTurns = b.Stats.Turns;
        TakeawayText = b.Takeaway();
        P50Text = b.Stats.TurnCount > 0 ? $"{b.Stats.TtfbP50:F0}" : "—";
        P95Text = b.Stats.TurnCount > 0 ? $"p95 {b.Stats.TtfbP95:F0} ms" : "p95 —";
        MaxText = b.Stats.TurnCount > 0 ? $"max {b.Stats.TtfbMax:F0} ms" : "max —";
        RtfText = b.Stats.TtsRtfMean is { } rtf ? $"tts rtf {rtf:F2}" : "tts rtf —";
        MetaText = $"{b.Stats.TurnCount} turn(s) · {b.Stats.ErrorCount} error(s) · " +
                   $"{b.Stats.InterruptionCount} interruption(s) · {b.DurationSeconds:F0} s · {b.SourceDescription}";

        // The percentile card's delta line: vs the previous run, when one exists (§9.2).
        var previous = Runs.Select(r => r.Bundle)
            .Where(x => x.Number < b.Number && x.Stats.TurnCount > 0)
            .MaxBy(x => x.Number);
        if (previous is not null && b.Stats.TurnCount > 0 && previous.Stats.TtfbP50 > 0)
        {
            var pct = (b.Stats.TtfbP50 - previous.Stats.TtfbP50) / previous.Stats.TtfbP50 * 100;
            DeltaImproved = pct <= 0;
            DeltaText = $"{(DeltaImproved ? "▼" : "▲")} {Math.Abs(pct):F0}% vs run #{previous.Number} ({previous.Label})";
        }
        else
        {
            DeltaText = null;
        }
    }

    public void RefreshRuns()
    {
        var checkedNumbers = Runs.Where(r => r.IsChecked).Select(r => r.Bundle.Number).ToHashSet();
        var selectedNumber = SelectedRun?.Bundle.Number;
        Runs.Clear();
        foreach (var bundle in _store.LoadAll())
            Runs.Add(new RunRowVm(bundle, RefreshCompare) { IsChecked = checkedNumbers.Contains(bundle.Number) });
        SelectedRun = Runs.FirstOrDefault(r => r.Bundle.Number == selectedNumber) ?? Runs.FirstOrDefault();
        OnPropertyChanged(nameof(HasRuns));
        RefreshCompare();

        // The empty-state hint must not claim "no run yet" over a list of bundles on disk.
        if (!IsRunning && Runs.Count > 0 && StatusText?.StartsWith("No run yet") == true)
            StatusText = $"{Runs.Count} saved run(s) — select one, or record another.";
    }

    private void RefreshCompare()
    {
        var picked = Runs.Where(r => r.IsChecked).ToList();
        Compare = picked.Count == 2 ? RunCompare.Build(picked[0].Bundle, picked[1].Bundle) : null;
    }

    [RelayCommand]
    private void DeleteSelectedRun()
    {
        if (SelectedRun is not { } row) return;
        _store.Delete(row.Bundle);
        RefreshRuns();
        StatusText = $"Deleted run #{row.Bundle.Number}.";
    }

    /// <summary>Talk's waterfall deep-link (§5): land on this stage's series in the trend chart.</summary>
    public void FocusStage(string stage)
    {
        FocusedStage = stage;
        SelectedRun ??= Runs.FirstOrDefault();
    }

    // ── export ───────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (SelectedRun is not { } row) return;
        var path = Path.Combine(_store.Directory, $"run-{row.Bundle.Number:D4}.csv");
        await File.WriteAllTextAsync(path, row.Bundle.ToCsv());
        StatusText = $"Exported {path}";
    }

    [RelayCommand]
    private void RevealRunsFolder()
    {
        try
        {
            Directory.CreateDirectory(_store.Directory);
            Process.Start(new ProcessStartInfo(_store.Directory) { UseShellExecute = true });
        }
        catch (Exception ex) { ErrorText = ex.Message; }
    }
}
