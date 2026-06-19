using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Voxa.Diagnostics;
using Voxa.Studio.Audio;
using Voxa.Studio.Services;

namespace Voxa.Studio.ViewModels;

/// <summary>One rendered VAD inference window (the trace control's unit of drawing).</summary>
public readonly record struct VadSample(float Probability, bool Voiced, bool GateOpen);

/// <summary>
/// The live pipeline state shown as the Talk status pill — derived from the diagnostics hub's turn
/// and stage events so the user always knows what's happening (and when the mic is actually live).
/// </summary>
public enum TalkPhase { Idle, WarmingUp, Listening, Hearing, Transcribing, Thinking, Speaking }

/// <summary>A transcript bubble. Bot bubbles stream — Text grows as agent deltas arrive.</summary>
public sealed partial class ChatBubble : ObservableObject
{
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private bool _isInterrupted;
    public bool IsUser { get; init; }
}

/// <summary>One measured stage in a turn's waterfall.</summary>
public sealed record WaterfallSegment(string Stage, double Ms, double StartMs)
{
    public string Label => Stage switch
    {
        "vad_close" => "VAD",
        "stt_final" => "STT",
        "agent_first_token" => "AGENT",
        "tts_first_byte" => "TTS",
        "audio_out" => "OUT",
        _ => Stage.ToUpperInvariant(),
    };
    public string MsText => Ms >= 1 ? $"{Ms:F0} ms" : $"{Ms:F2} ms";
}

/// <summary>A completed turn's latency breakdown, newest first in the Talk view.</summary>
public sealed record TurnWaterfall(int TurnNumber, IReadOnlyList<WaterfallSegment> Segments)
{
    public double TotalMs => Segments.Sum(s => s.Ms);
    public string TotalText => $"{TotalMs:F0} ms";
}

/// <summary>
/// The Talk view (VST-001 WS2): live conversation + VAD trace + per-turn latency waterfall.
/// Avalonia-free by design — hub events buffer in concurrent queues and the view's render timer
/// calls <see cref="DrainPending"/> (≤30 fps), so this whole class is testable headless.
/// </summary>
public sealed partial class TalkViewModel : ObservableObject
{
    /// <summary>~40 s of VAD windows at 16 kHz (31.25 windows/s) kept on screen.</summary>
    internal const int TraceCapacity = 1250;

    private readonly StudioServices _services;
    private readonly ConcurrentQueue<DiagnosticEvent> _pending = new();
    private readonly List<VadSample> _trace = new(TraceCapacity);

    private TalkSession? _session;
    private CancellationTokenSource? _subscription;
    private ChatBubble? _streamingBot;
    private Dictionary<string, double> _stages = new();
    private int _turnNumber;
    private long _lastBotTick;     // debounces the per-sentence Bot edges so Speaking doesn't flicker
    private long _phaseSinceTick;  // when Transcribing/Thinking began — a timeout safety net
    private long _lastTraceTick;   // throttles the VAD-trace snapshot (render-jank reduction)

    /// <summary>Test seam for the time-based phase debounce/throttle (Environment.TickCount64).</summary>
    internal Func<long> NowTick = () => Environment.TickCount64;

    private const long BotIdleMs = 600;          // quiet gap after the last bot signal → turn is done
    private const long ThinkingTimeoutMs = 12_000; // a turn that yields no speech falls back to Listening
    private const long TraceThrottleMs = 80;     // ~12 fps trace updates — smooth enough, far less GC

    public TalkViewModel(StudioServices services)
    {
        _services = services;
        RefreshDevices();
        RefreshFromConfig();
    }

    /// <summary>Re-read the provider chip and VAD threshold — called after a Config "Apply".</summary>
    public void RefreshFromConfig()
    {
        var voxa = _services.Configuration.GetSection("Voxa");
        var agent = voxa["Agent:Provider"] ?? "OpenAI";
        if (string.Equals(agent, "OpenAI", StringComparison.OrdinalIgnoreCase))
            agent = $"OpenAI / {voxa["Agent:Model"] ?? "gpt-4o-mini"}";
        var tts = voxa["Tts"] ?? "?";
        var voice = string.Equals(tts, "Piper", StringComparison.OrdinalIgnoreCase) ? voxa["Piper:Voice"]
                  : string.Equals(tts, "Kokoro", StringComparison.OrdinalIgnoreCase) ? voxa["Kokoro:Voice"]
                  : null;

        ProviderChain = string.Join("  ·  ", new[]
        {
            voxa["Vad:Engine"] ?? "Silero",
            voxa["Stt"] ?? "?",
            agent,
            $"{tts}{(voice is null ? "" : $" / {voice}")}",
        });
        VadThreshold = float.TryParse(voxa["Vad:ConfidenceThreshold"], out var t) ? t : 0.5f;
    }

    // ── bindable state ───────────────────────────────────────────────────────

    public ObservableCollection<AudioEndpoint> Microphones { get; } = new();
    public ObservableCollection<AudioEndpoint> Speakers { get; } = new();
    public ObservableCollection<ChatBubble> Transcript { get; } = new();
    public ObservableCollection<TurnWaterfall> Waterfalls { get; } = new();
    public ObservableCollection<string> EventLog { get; } = new();

    [ObservableProperty] private AudioEndpoint? _selectedMicrophone;
    [ObservableProperty] private AudioEndpoint? _selectedSpeaker;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isRunning;
    [ObservableProperty] private string _statusText = "Idle — pick devices and start a session.";
    [ObservableProperty] private string? _errorText;
    [ObservableProperty] private bool _isUserSpeaking;
    [ObservableProperty] private bool _isBotSpeaking;
    [ObservableProperty] private bool _showEventLog;

    /// <summary>The live pipeline state — the prominent status pill binds to this.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhaseLabel)), NotifyPropertyChangedFor(nameof(PhasePulse))]
    private TalkPhase _phase = TalkPhase.Idle;

    public string PhaseLabel => Phase switch
    {
        TalkPhase.WarmingUp => "Warming up…",
        TalkPhase.Listening => "Listening",
        TalkPhase.Hearing => "Hearing you",
        TalkPhase.Transcribing => "Transcribing…",
        TalkPhase.Thinking => "Thinking…",
        TalkPhase.Speaking => "Speaking",
        _ => "Idle",
    };

    /// <summary>In-progress phases pulse; settled ones (Idle/Listening) hold steady.</summary>
    public bool PhasePulse => Phase is TalkPhase.WarmingUp or TalkPhase.Hearing
        or TalkPhase.Transcribing or TalkPhase.Thinking or TalkPhase.Speaking;

    /// <summary>True while a Builder or Metrics run owns the pipeline — starting Talk is blocked.</summary>
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _startBlocked;

    /// <summary>Clicking a waterfall stage block deep-links to Metrics (§5 cross-navigation).</summary>
    public event Action<string>? OpenInMetricsRequested;

    internal void RequestStageInMetrics(string stage) => OpenInMetricsRequested?.Invoke(stage);

    /// <summary>Immutable snapshot for the trace control; replaced on each drain that saw VAD windows.</summary>
    [ObservableProperty] private IReadOnlyList<VadSample> _traceSnapshot = [];

    [ObservableProperty] private string _providerChain = "";
    [ObservableProperty] private float _vadThreshold = 0.5f;
    public bool HasAudioDevices => Microphones.Count > 0 && Speakers.Count > 0;

    public void RefreshDevices()
    {
        Microphones.Clear();
        foreach (var m in _services.AudioDevice.CaptureEndpoints()) Microphones.Add(m);
        Speakers.Clear();
        foreach (var s in _services.AudioDevice.RenderEndpoints()) Speakers.Add(s);
        SelectedMicrophone ??= Microphones.FirstOrDefault();
        SelectedSpeaker ??= Speakers.FirstOrDefault();
        OnPropertyChanged(nameof(HasAudioDevices));
    }

    // ── session lifecycle ────────────────────────────────────────────────────

    private bool CanStart() => !IsRunning && !StartBlocked;
    private bool CanStop() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (SelectedMicrophone is null || SelectedSpeaker is null)
        {
            ErrorText = "No microphone/speaker available. Connect a device and refresh.";
            return;
        }

        ErrorText = null;
        Phase = TalkPhase.WarmingUp;
        try
        {
            // First-run downloads happen HERE, with visible progress — never at app start.
            var missing = ActiveConfigArtifacts.Missing(_services.Configuration, _services.ModelCache);
            if (missing.Count > 0)
            {
                var totalMb = missing.Sum(a => a.SizeBytes) / (1024 * 1024);
                StatusText = $"Downloading {missing.Count} model file(s), ~{totalMb} MB…";
                var progress = new Progress<Voxa.Speech.VoxaPrefetchProgress>(p =>
                    StatusText = p.Completed
                        ? $"Downloaded {p.ArtifactId}  ({p.CompletedCount}/{p.TotalCount})"
                        : $"Downloading {p.ArtifactId}  ({p.CompletedCount + 1}/{p.TotalCount})…");
                await _services.ModelCache.PrefetchAsync(missing, progress);
            }

            // Load the model weights into memory NOW (a user action, so it honors "no work before the
            // user acts"), so the FIRST turn isn't a multi-second cold start. whisper.cpp caches its
            // factory process-wide, so the session reuses exactly what we warm here.
            StatusText = "Warming up models…";
            await WarmUpEnginesAsync();

            StatusText = "Starting pipeline…";
            _session = _services.CreateTalkSession();

            _subscription = new CancellationTokenSource();
            _ = Task.Run(() => SubscribeAsync(_session, _subscription.Token));
            _ = WatchSessionAsync(_session);

            await _session.StartAsync(SelectedMicrophone, SelectedSpeaker);
            IsRunning = true;
            Phase = TalkPhase.Listening;
            StatusText = $"Live — {_session.InputSampleRate / 1000.0:0.#} kHz in, {_session.OutputSampleRate / 1000.0:0.#} kHz out. Say something.";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            StatusText = "Failed to start.";
            await TearDownAsync();
        }
    }

    // Warm-up lives on StudioServices now — the splash and a Config Apply pre-warm cached models, so at
    // Start this is usually a fast no-op. We still call it (cachedOnly:false) as the safety net: it covers
    // a model just downloaded above on first run, or a provider changed since the splash warmed.
    private Task WarmUpEnginesAsync() => _services.WarmUpAsync(cachedOnly: false);

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        await TearDownAsync();
        StatusText = "Stopped.";
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
        IsRunning = false;
        IsUserSpeaking = false;
        IsBotSpeaking = false;
        Phase = TalkPhase.Idle;
        _streamingBot = null;
    }

    private async Task WatchSessionAsync(TalkSession session)
    {
        try { await session.WaitAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorText = ex.Message;
            StatusText = "Pipeline failed.";
        }
    }

    private async Task SubscribeAsync(TalkSession session, CancellationToken ct)
    {
        try
        {
            await foreach (var e in session.Hub.SubscribeAsync(ct))
                _pending.Enqueue(e);
        }
        catch (OperationCanceledException) { /* session stop */ }
    }

    /// <summary>Test seam: inject hub events exactly as the subscription loop would.</summary>
    internal void EnqueueForTest(DiagnosticEvent e) => _pending.Enqueue(e);

    // ── render-side drain (called by the view's ≤30 fps timer, or tests) ─────

    /// <summary>Apply buffered hub events to the bindable state. Single-threaded by contract.</summary>
    public void DrainPending()
    {
        var now = NowTick();
        bool traceChanged = false;
        while (_pending.TryDequeue(out var e))
        {
            switch (e)
            {
                case VadWindowEvent vad:
                    if (_trace.Count >= TraceCapacity) _trace.RemoveAt(0);
                    _trace.Add(new VadSample(vad.Probability, vad.Voiced, vad.GateOpen));
                    traceChanged = true;
                    break;

                case TurnEvent turn:
                    ApplyTurn(turn, now);
                    Log(e, turn.Edge.ToString());
                    break;

                case TranscriptEvent { IsFinal: true } t:
                    Transcript.Add(new ChatBubble { IsUser = true, Text = t.Text });
                    if (Phase is TalkPhase.Hearing or TalkPhase.Transcribing) Phase = TalkPhase.Thinking;
                    Log(e, $"\"{t.Text}\"");
                    break;

                case AgentDeltaEvent delta:
                    _streamingBot ??= NewBotBubble();
                    _streamingBot.Text += delta.TextDelta;
                    Log(e, $"+{delta.TextDelta.Length} chars");
                    break;

                case StageLatencyEvent stage:
                    ApplyStage(stage);
                    Log(e, $"{stage.Stage} {stage.Ms:F1} ms");
                    break;

                case TtsChunkEvent chunk:
                    Phase = TalkPhase.Speaking;
                    _lastBotTick = now;
                    Log(e, $"{chunk.Bytes} B @ {chunk.SampleRate} Hz");
                    break;

                case PipelineErrorEvent err:
                    ErrorText = err.Message;
                    Log(e, err.Message);
                    break;
            }
        }

        // Per-sentence Bot edges flicker, so hold Speaking until the bot has been quiet for a beat;
        // and never strand Transcribing/Thinking if a turn produced no speech at all.
        if (Phase == TalkPhase.Speaking && now - _lastBotTick > BotIdleMs)
            Phase = TalkPhase.Listening;
        else if (Phase is (TalkPhase.Transcribing or TalkPhase.Thinking)
                 && now - _phaseSinceTick > ThinkingTimeoutMs)
            Phase = TalkPhase.Listening;

        // Throttle the trace snapshot: the VAD trace is smooth at ~12 fps, and this avoids a
        // 1250-element array allocation (+ a control redraw) on every 33 ms frame.
        if (traceChanged && now - _lastTraceTick >= TraceThrottleMs)
        {
            TraceSnapshot = _trace.ToArray();
            _lastTraceTick = now;
        }
    }

    private void ApplyTurn(TurnEvent turn, long now)
    {
        switch (turn.Edge)
        {
            case TurnEdge.UserStarted:
                IsUserSpeaking = true;
                _streamingBot = null; // anything the bot says next is a NEW reply
                Phase = TalkPhase.Hearing;
                break;
            case TurnEdge.UserStopped:
                IsUserSpeaking = false;
                _stages = new Dictionary<string, double>(); // new turn measurement begins
                Phase = TalkPhase.Transcribing;
                _phaseSinceTick = now;
                break;
            case TurnEdge.BotStarted:
                IsBotSpeaking = true;
                Phase = TalkPhase.Speaking;
                _lastBotTick = now;
                break;
            case TurnEdge.BotStopped:
                IsBotSpeaking = false;
                _streamingBot = null;
                _lastBotTick = now; // stay Speaking through the debounce — another sentence may follow
                break;
            case TurnEdge.Interrupted:
                IsBotSpeaking = false;
                if (_streamingBot is not null) _streamingBot.IsInterrupted = true;
                _streamingBot = null;
                Phase = TalkPhase.Listening;
                break;
        }
    }

    private ChatBubble NewBotBubble()
    {
        var bubble = new ChatBubble { IsUser = false };
        Transcript.Add(bubble);
        return bubble;
    }

    private static readonly string[] StageOrder =
        ["vad_close", "stt_final", "agent_first_token", "tts_first_byte", "audio_out"];

    private void ApplyStage(StageLatencyEvent stage)
    {
        _stages[stage.Stage] = stage.Ms;

        // audio_out is the turn's last measured stage — publish the waterfall.
        if (stage.Stage == "audio_out")
        {
            double cursor = 0;
            var segments = new List<WaterfallSegment>();
            foreach (var key in StageOrder)
            {
                if (!_stages.TryGetValue(key, out var ms)) continue;
                segments.Add(new WaterfallSegment(key, ms, cursor));
                cursor += ms;
            }
            if (segments.Count > 0)
            {
                Waterfalls.Insert(0, new TurnWaterfall(++_turnNumber, segments));
                while (Waterfalls.Count > 8) Waterfalls.RemoveAt(Waterfalls.Count - 1);
            }
            _stages = new Dictionary<string, double>();
        }
    }

    private void Log(DiagnosticEvent e, string detail)
    {
        EventLog.Insert(0, $"{e.SeqNo,6}  {e.TimestampMicros / 1000.0,9:F1} ms  {e.GetType().Name.Replace("Event", ""),-14} {detail}");
        while (EventLog.Count > 400) EventLog.RemoveAt(EventLog.Count - 1);
    }
}
