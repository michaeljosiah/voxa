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
}
