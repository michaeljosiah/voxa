using Voxa.Diagnostics;
using Voxa.Studio.ViewModels;

namespace Voxa.Studio.Tests;

/// <summary>
/// VST-001 WS2-A1/A2: a representative hub event sequence (the shape a real WhisperCpp + Echo +
/// Piper turn produces) renders the expected transcript, VAD trace, and 5-segment waterfall —
/// and a barge-in truncates the streaming bot bubble without leaking a half-measured waterfall.
/// Pure view-model tests: no Avalonia, no audio, no network.
/// </summary>
public class TalkViewModelTests
{
    private static TalkViewModel Vm() => new(TestSupport.Services());

    /// <summary>One full turn's hub events, in pipeline order (timestamps are hub-side, so 0 here).</summary>
    private static IEnumerable<DiagnosticEvent> OneGoodTurn()
    {
        // ~1 s of audio: 25 closed-gate windows, then voiced speech, then the turn.
        for (int i = 0; i < 25; i++)
            yield return new VadWindowEvent(0.05f, 0.001, Voiced: false, GateOpen: false);
        yield return new TurnEvent(TurnEdge.UserStarted);
        for (int i = 0; i < 12; i++)
            yield return new VadWindowEvent(0.97f, 0.06, Voiced: true, GateOpen: true);
        yield return new TurnEvent(TurnEdge.UserStopped);
        yield return new TranscriptEvent("tell me about the weather", IsFinal: true);
        yield return new StageLatencyEvent("vad_close", 780);
        yield return new StageLatencyEvent("stt_final", 310);
        yield return new AgentDeltaEvent("You said: tell me ");
        yield return new StageLatencyEvent("agent_first_token", 45);
        yield return new AgentDeltaEvent("about the weather.");
        yield return new TurnEvent(TurnEdge.BotStarted);
        yield return new TtsChunkEvent(6400, 16000);
        yield return new StageLatencyEvent("tts_first_byte", 120);
        yield return new StageLatencyEvent("audio_out", 0.4);
        yield return new TurnEvent(TurnEdge.BotStopped);
    }

    [Fact]
    public void A_Full_Turn_Renders_Transcript_Trace_And_A_Five_Segment_Waterfall()
    {
        var vm = Vm();
        foreach (var e in OneGoodTurn()) vm.EnqueueForTest(e);
        vm.DrainPending();

        // Transcript: one user bubble with the final text, one streamed bot bubble.
        Assert.Equal(2, vm.Transcript.Count);
        Assert.True(vm.Transcript[0].IsUser);
        Assert.Equal("tell me about the weather", vm.Transcript[0].Text);
        Assert.False(vm.Transcript[1].IsUser);
        Assert.Equal("You said: tell me about the weather.", vm.Transcript[1].Text);
        Assert.False(vm.Transcript[1].IsInterrupted);

        // VAD trace: every window kept, gate-open shading present.
        Assert.Equal(37, vm.TraceSnapshot.Count);
        Assert.Contains(vm.TraceSnapshot, s => s.GateOpen);
        Assert.Contains(vm.TraceSnapshot, s => !s.GateOpen);

        // Waterfall: all five stages, in turn order, newest-first list.
        var waterfall = Assert.Single(vm.Waterfalls);
        Assert.Equal(
            ["vad_close", "stt_final", "agent_first_token", "tts_first_byte", "audio_out"],
            waterfall.Segments.Select(s => s.Stage));
        Assert.Equal(780 + 310 + 45 + 120 + 0.4, waterfall.TotalMs, precision: 5);

        // Speaking indicators returned to idle.
        Assert.False(vm.IsUserSpeaking);
        Assert.False(vm.IsBotSpeaking);
    }

    [Fact]
    public void Interruption_Truncates_The_Streaming_Bot_Bubble()
    {
        var vm = Vm();
        vm.EnqueueForTest(new TurnEvent(TurnEdge.UserStopped));
        vm.EnqueueForTest(new TranscriptEvent("question", IsFinal: true));
        vm.EnqueueForTest(new AgentDeltaEvent("A long answer that the user "));
        vm.EnqueueForTest(new TurnEvent(TurnEdge.BotStarted));
        vm.EnqueueForTest(new TurnEvent(TurnEdge.Interrupted)); // barge-in mid-stream
        vm.DrainPending();

        var bot = Assert.Single(vm.Transcript, b => !b.IsUser);
        Assert.True(bot.IsInterrupted);
        Assert.False(vm.IsBotSpeaking);

        // A later delta (stale agent output racing the cancel) must NOT resurrect the bubble.
        vm.EnqueueForTest(new AgentDeltaEvent("never hears."));
        vm.DrainPending();
        Assert.Equal("A long answer that the user ", bot.Text);

        // And no waterfall was emitted for the abandoned turn.
        Assert.Empty(vm.Waterfalls);
    }

    [Fact]
    public void Consecutive_Turns_Stream_Into_Separate_Bot_Bubbles()
    {
        var vm = Vm();
        foreach (var e in OneGoodTurn()) vm.EnqueueForTest(e);
        foreach (var e in OneGoodTurn()) vm.EnqueueForTest(e);
        vm.DrainPending();

        Assert.Equal(4, vm.Transcript.Count); // user, bot, user, bot
        Assert.Equal(2, vm.Waterfalls.Count);
        Assert.Equal(2, vm.Waterfalls[0].TurnNumber); // newest first
    }

    [Fact]
    public void Trace_Ring_Buffer_Caps_At_Capacity()
    {
        var vm = Vm();
        for (int i = 0; i < TalkViewModel.TraceCapacity + 500; i++)
            vm.EnqueueForTest(new VadWindowEvent(0.5f, 0.01, false, false));
        vm.DrainPending();

        Assert.Equal(TalkViewModel.TraceCapacity, vm.TraceSnapshot.Count);
    }

    [Fact]
    public void Pipeline_Errors_Surface_In_The_Error_Banner_And_Log()
    {
        var vm = Vm();
        vm.EnqueueForTest(new PipelineErrorEvent("DiagnosticsTap[Vad]", "engine exploded"));
        vm.DrainPending();

        Assert.Equal("engine exploded", vm.ErrorText);
        Assert.Contains(vm.EventLog, line => line.Contains("engine exploded"));
    }

    [Fact]
    public void A_Live_Builder_Run_Blocks_Start()
    {
        // The mirror of the Builder's RunBlocked: one audio device, so a Builder run must keep
        // Talk from starting (MainWindowViewModel.SyncLiveState sets StartBlocked).
        var vm = Vm();
        Assert.True(vm.StartCommand.CanExecute(null));
        vm.StartBlocked = true;
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    // ── pipeline-state pill (the "what's happening" cue) ─────────────────────

    [Fact]
    public void Phase_Tracks_The_Turn_Lifecycle()
    {
        var vm = Vm();
        long clock = 1000;
        vm.NowTick = () => clock;
        Assert.Equal(TalkPhase.Idle, vm.Phase);

        vm.EnqueueForTest(new TurnEvent(TurnEdge.UserStarted));
        vm.DrainPending();
        Assert.Equal(TalkPhase.Hearing, vm.Phase);

        vm.EnqueueForTest(new TurnEvent(TurnEdge.UserStopped));
        vm.DrainPending();
        Assert.Equal(TalkPhase.Transcribing, vm.Phase);

        vm.EnqueueForTest(new TranscriptEvent("hi", IsFinal: true));
        vm.DrainPending();
        Assert.Equal(TalkPhase.Thinking, vm.Phase);

        vm.EnqueueForTest(new TurnEvent(TurnEdge.BotStarted));
        vm.DrainPending();
        Assert.Equal(TalkPhase.Speaking, vm.Phase);
    }

    [Fact]
    public void Speaking_Holds_Through_Per_Sentence_Gaps_Then_Settles_To_Listening()
    {
        // TextToSpeechProcessor emits BotStarted/BotStopped PER SENTENCE, so a naive mapping would
        // flicker Speaking↔Listening mid-reply. The debounce holds Speaking until the bot is quiet.
        var vm = Vm();
        long clock = 5000;
        vm.NowTick = () => clock;

        vm.EnqueueForTest(new TurnEvent(TurnEdge.BotStarted));
        vm.EnqueueForTest(new TurnEvent(TurnEdge.BotStopped)); // sentence 1 finished
        vm.DrainPending();
        Assert.Equal(TalkPhase.Speaking, vm.Phase); // debounced — another sentence may follow

        clock += 200;                                // a brief inter-sentence gap...
        vm.EnqueueForTest(new TtsChunkEvent(3200, 16000)); // ...sentence 2 audio
        vm.DrainPending();
        Assert.Equal(TalkPhase.Speaking, vm.Phase);

        clock += 700;                                // now genuinely quiet
        vm.DrainPending();
        Assert.Equal(TalkPhase.Listening, vm.Phase);
    }

    [Fact]
    public void A_Turn_That_Yields_No_Speech_Falls_Back_To_Listening()
    {
        var vm = Vm();
        long clock = 0;
        vm.NowTick = () => clock;

        vm.EnqueueForTest(new TurnEvent(TurnEdge.UserStopped));
        vm.DrainPending();
        Assert.Equal(TalkPhase.Transcribing, vm.Phase);

        clock += 13_000; // STT/agent produced nothing (filtered hallucination) — don't strand the pill
        vm.DrainPending();
        Assert.Equal(TalkPhase.Listening, vm.Phase);
    }

    [Fact]
    public void Phase_Label_And_Pulse_Reflect_The_State()
    {
        var vm = Vm();
        vm.NowTick = () => 0;
        Assert.Equal("Idle", vm.PhaseLabel);
        Assert.False(vm.PhasePulse);

        vm.EnqueueForTest(new TurnEvent(TurnEdge.UserStarted));
        vm.DrainPending();
        Assert.Equal("Hearing you", vm.PhaseLabel);
        Assert.True(vm.PhasePulse);
    }

    [Fact]
    public void The_Phase_Pill_Shows_Whenever_A_Phase_Is_Active_Even_Before_IsRunning()
    {
        // Codex P2: the pill must not be gated on IsRunning, or the cold-start "Warming up…" state
        // (set before IsRunning flips true) would never be seen.
        var vm = Vm();
        vm.NowTick = () => 0;
        Assert.False(vm.ShowPhasePill); // Idle → hidden

        vm.EnqueueForTest(new TurnEvent(TurnEdge.UserStarted));
        vm.DrainPending();
        Assert.False(vm.IsRunning);     // tests never call StartAsync
        Assert.True(vm.ShowPhasePill);  // ...yet the live phase is still shown
    }

    [Fact]
    public void Stt_Eating_The_Timeout_Budget_Does_Not_Flip_Thinking_To_Listening()
    {
        // Codex P2: a big Whisper model can chew most of the 12s budget during Transcribing; entering
        // Thinking must restart the clock so the agent's work doesn't trip the no-speech fallback.
        var vm = Vm();
        long clock = 0;
        vm.NowTick = () => clock;

        vm.EnqueueForTest(new TurnEvent(TurnEdge.UserStopped));
        vm.DrainPending(); // Transcribing, _phaseSinceTick = 0

        clock += 11_000;   // STT chews most of the budget
        vm.EnqueueForTest(new TranscriptEvent("a long utterance", IsFinal: true));
        vm.DrainPending();
        Assert.Equal(TalkPhase.Thinking, vm.Phase); // timeout was reset on entering Thinking

        clock += 2_000;    // 2s of agent work — would have blown the old (un-reset) budget
        vm.DrainPending();
        Assert.Equal(TalkPhase.Thinking, vm.Phase); // still thinking, not wrongly "Listening"
    }
}
