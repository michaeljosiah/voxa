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

            StatusText = "Starting pipeline…";
            _session = _services.CreateTalkSession();

            _subscription = new CancellationTokenSource();
            _ = Task.Run(() => SubscribeAsync(_session, _subscription.Token));
            _ = WatchSessionAsync(_session);

            await _session.StartAsync(SelectedMicrophone, SelectedSpeaker);
            IsRunning = true;
            StatusText = $"Live — {_session.InputSampleRate / 1000.0:0.#} kHz in, {_session.OutputSampleRate / 1000.0:0.#} kHz out. Say something.";
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            StatusText = "Failed to start.";
            await TearDownAsync();
        }
    }

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
                    ApplyTurn(turn);
                    Log(e, turn.Edge.ToString());
                    break;

                case TranscriptEvent { IsFinal: true } t:
                    Transcript.Add(new ChatBubble { IsUser = true, Text = t.Text });
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
                    Log(e, $"{chunk.Bytes} B @ {chunk.SampleRate} Hz");
                    break;

                case PipelineErrorEvent err:
                    ErrorText = err.Message;
                    Log(e, err.Message);
                    break;
            }
        }

        if (traceChanged)
            TraceSnapshot = _trace.ToArray();
    }

    private void ApplyTurn(TurnEvent turn)
    {
        switch (turn.Edge)
        {
            case TurnEdge.UserStarted:
                IsUserSpeaking = true;
                _streamingBot = null; // anything the bot says next is a NEW reply
                break;
            case TurnEdge.UserStopped:
                IsUserSpeaking = false;
                _stages = new Dictionary<string, double>(); // new turn measurement begins
                break;
            case TurnEdge.BotStarted:
                IsBotSpeaking = true;
                break;
            case TurnEdge.BotStopped:
                IsBotSpeaking = false;
                _streamingBot = null;
                break;
            case TurnEdge.Interrupted:
                IsBotSpeaking = false;
                if (_streamingBot is not null) _streamingBot.IsInterrupted = true;
                _streamingBot = null;
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
