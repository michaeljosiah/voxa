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

/// <summary>A transcript line. Agent lines stream — Text grows as agent deltas arrive. Rendered as the
/// reference's role · text · time row (VST-005 strict-1:1); only user/agent roles ever appear here.</summary>
public sealed partial class ChatBubble : ObservableObject
{
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private bool _isInterrupted;
    public bool IsUser { get; init; }

    /// <summary>The mono caps role label in the left column ("you" / "agent").</summary>
    public string RoleLabel => IsUser ? "you" : "agent";

    /// <summary>Session-relative timestamp (mm:ss.fff) shown in the right column; "" before the first stamp.</summary>
    [ObservableProperty] private string _time = string.Empty;
}

/// <summary>One node in the Talk pipeline-flow strip (the reference's running chain). The active node
/// glows; <see cref="ShowLink"/> draws the dashed connector to the next node (false on the sink).</summary>
public sealed partial class PipelineNode : ObservableObject
{
    public required string Stage { get; init; }   // vad | stt | agent | tts | out — drives the stage colour
    public required string Kind { get; init; }    // source | vad | stt | agent | tts | sink (the mono micro-label)
    public required string Name { get; init; }
    public required string Meta { get; init; }
    public bool ShowLink { get; init; } = true;
    [ObservableProperty] private bool _isActive;
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
    private long _sessionStartTick; // 0 until Start; stamps transcript line timestamps relative to it

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

        var vad = voxa["Vad:Engine"] ?? "Silero";
        var stt = voxa["Stt"] ?? "?";
        var ttsLabel = $"{tts}{(voice is null ? "" : $" / {voice}")}";

        ProviderChain = string.Join("  ·  ", new[] { vad, stt, agent, ttsLabel });

        // The running pipeline-flow strip (reference Talk): Mic → VAD → STT → Agent → TTS → Speaker.
        // Stage colours and the active glow come from these; rebuilt whenever the config changes.
        var agentName = agent.Contains('/') ? agent[(agent.LastIndexOf('/') + 1)..].Trim() : agent;
        ProviderNodes.Clear();
        ProviderNodes.Add(new PipelineNode { Stage = "vad", Kind = "source", Name = "Mic", Meta = "16 kHz" });
        ProviderNodes.Add(new PipelineNode { Stage = "vad", Kind = "vad", Name = vad, Meta = "gate" });
        ProviderNodes.Add(new PipelineNode { Stage = "stt", Kind = "stt", Name = stt, Meta = "speech→text" });
        ProviderNodes.Add(new PipelineNode { Stage = "agent", Kind = "agent", Name = agentName, Meta = "llm" });
        ProviderNodes.Add(new PipelineNode { Stage = "tts", Kind = "tts", Name = tts, Meta = voice ?? "text→speech" });
        ProviderNodes.Add(new PipelineNode { Stage = "out", Kind = "sink", Name = "Speaker", Meta = "live", ShowLink = false });
        ApplyActiveNode();

        VadThreshold = float.TryParse(voxa["Vad:ConfidenceThreshold"], out var t) ? t : 0.5f;
    }

    /// <summary>Light the flow node that matches the current phase (Hearing→VAD, Transcribing→STT,
    /// Thinking→Agent, Speaking→TTS); nothing glows while Idle/WarmingUp/Listening.</summary>
    private void ApplyActiveNode()
    {
        var active = Phase switch
        {
            TalkPhase.Hearing => 1,
            TalkPhase.Transcribing => 2,
            TalkPhase.Thinking => 3,
            TalkPhase.Speaking => 4,
            _ => -1,
        };
        for (int i = 0; i < ProviderNodes.Count; i++)
            ProviderNodes[i].IsActive = i == active;
    }

    partial void OnPhaseChanged(TalkPhase value) => ApplyActiveNode();

    // ── bindable state ───────────────────────────────────────────────────────

    public ObservableCollection<AudioEndpoint> Microphones { get; } = new();
    public ObservableCollection<AudioEndpoint> Speakers { get; } = new();
    public ObservableCollection<ChatBubble> Transcript { get; } = new();
    public ObservableCollection<TurnWaterfall> Waterfalls { get; } = new();
    public ObservableCollection<string> EventLog { get; } = new();

    /// <summary>The running pipeline-flow strip shown along the bottom of a live Talk session.</summary>
    public ObservableCollection<PipelineNode> ProviderNodes { get; } = new();

    // ── aside stats (reference Talk right rail): time-to-first-byte, turns, barge-ins ──

    /// <summary>Time from the user finishing to the first synthesized audio of the latest turn.</summary>
    [ObservableProperty] private string _ttfbText = "—";

    /// <summary>Completed turns this session.</summary>
    [ObservableProperty] private int _turnCount;

    /// <summary>Times the user barged in over the agent (interruptions) this session.</summary>
    [ObservableProperty] private int _bargeInCount;

    /// <summary>Delegated background tasks currently running (VDX-008) — the viewbar badge shows while &gt; 0.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BackgroundTasksLabel)), NotifyPropertyChangedFor(nameof(ShowBackgroundTasksBadge))]
    private int _activeBackgroundTasks;

    public string BackgroundTasksLabel => ActiveBackgroundTasks == 1
        ? "1 background task"
        : $"{ActiveBackgroundTasks} background tasks";

    public bool ShowBackgroundTasksBadge => ActiveBackgroundTasks > 0;

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
    [NotifyPropertyChangedFor(nameof(PhaseLabel)), NotifyPropertyChangedFor(nameof(PhasePulse)),
     NotifyPropertyChangedFor(nameof(ShowPhasePill))]
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

    /// <summary>The pill shows from the moment Start is pressed (WarmingUp, before IsRunning) until the
    /// session ends — so the cold-start "Warming up…" state is actually visible.</summary>
    public bool ShowPhasePill => Phase != TalkPhase.Idle;

    /// <summary>True while a Builder or Metrics run owns the pipeline — starting Talk is blocked.</summary>
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _startBlocked;

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
        _sessionStartTick = NowTick();

        // Each session starts clean — the transcript is now the primary Talk surface, so a re-start
        // must not prepend the previous conversation, and the aside counters must reset to this run.
        Transcript.Clear();
        Waterfalls.Clear();
        EventLog.Clear();
        _trace.Clear();
        TraceSnapshot = [];
        _streamingBot = null;
        _stages = new Dictionary<string, double>();
        _turnNumber = 0;
        TurnCount = 0;
        BargeInCount = 0;
        ActiveBackgroundTasks = 0;
        TtfbText = "—";
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
        ActiveBackgroundTasks = 0; // in-flight tasks die with the session (VDX-008 EndFrame semantics)
        _streamingBot = null;
        _sessionStartTick = 0;
    }

    /// <summary>Session-relative mm:ss.fff for a transcript line; "" before a session has started.</summary>
    private string Stamp(long now) =>
        _sessionStartTick == 0 ? string.Empty
            : TimeSpan.FromMilliseconds(Math.Max(0, now - _sessionStartTick)).ToString(@"mm\:ss\.fff");

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
                    Transcript.Add(new ChatBubble { IsUser = true, Text = t.Text, Time = Stamp(now) });
                    // Entering Thinking restarts the no-speech timeout — STT may have eaten most of the
                    // budget (a big Whisper model on CPU), and the agent's work shouldn't count against it.
                    if (Phase is TalkPhase.Hearing or TalkPhase.Transcribing)
                    {
                        Phase = TalkPhase.Thinking;
                        _phaseSinceTick = now;
                    }
                    Log(e, $"\"{t.Text}\"");
                    break;

                case AgentDeltaEvent delta:
                    _streamingBot ??= NewBotBubble();
                    _streamingBot.Text += delta.TextDelta;
                    _phaseSinceTick = now; // the agent is producing — keep the timeout from firing mid-turn
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

                // ── VDX-008 background delegation ─────────────────────────────
                case BackgroundTaskStartedEvent started:
                    ActiveBackgroundTasks++;
                    Log(e, Truncate(started.Goal, 60));
                    break;

                case BackgroundTaskCompletedEvent completed:
                    ActiveBackgroundTasks = Math.Max(0, ActiveBackgroundTasks - 1);
                    Log(e, completed.IsError
                        ? $"FAILED after {completed.ElapsedMs:F0} ms"
                        : $"done in {completed.ElapsedMs:F0} ms");
                    break;

                case BackgroundTaskRejectedEvent:
                    Log(e, "rejected — request queue full");
                    break;

                case BackgroundTaskDroppedEvent:
                    Log(e, "held result dropped (pending cap)");
                    break;

                case LlmTurnEvent { Trigger: Voxa.Frames.TurnTrigger.BackgroundResult } llmTurn:
                    // A result gated to silence produces no transcript line — the log is the only
                    // place these turns are visible, so name them explicitly.
                    Log(e, llmTurn.Started ? "background-result turn started" : "background-result turn ended");
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
                BargeInCount++;
                Phase = TalkPhase.Listening;
                break;
        }
    }

    private ChatBubble NewBotBubble()
    {
        var bubble = new ChatBubble { IsUser = false, Time = Stamp(NowTick()) };
        Transcript.Add(bubble);
        return bubble;
    }

    private static readonly string[] StageOrder =
        ["vad_close", "stt_final", "agent_first_token", "tts_first_byte", "audio_out"];

    /// <summary>The measured ms for a stage in the turn under construction, or 0 if not seen yet.</summary>
    private double Stage(string key) => _stages.TryGetValue(key, out var v) ? v : 0;

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
                TurnCount = _turnNumber;
                // TTFB (aside): user-perceived time from finishing speaking to first response audio —
                // the post-utterance stages (transcribe → first token → first synthesized byte).
                var ttfb = Stage("stt_final") + Stage("agent_first_token") + Stage("tts_first_byte");
                if (ttfb > 0) TtfbText = $"{ttfb:F0} ms";
            }
            _stages = new Dictionary<string, double>();
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private void Log(DiagnosticEvent e, string detail)
    {
        EventLog.Insert(0, $"{e.SeqNo,6}  {e.TimestampMicros / 1000.0,9:F1} ms  {e.GetType().Name.Replace("Event", ""),-14} {detail}");
        while (EventLog.Count > 400) EventLog.RemoveAt(EventLog.Count - 1);
    }
}
