using Voxa.Studio.Services;

namespace Voxa.Studio.Tests;

/// <summary>
/// Half-duplex echo suppression (ROADMAP P1): the Talk mic pump must not ingest the bot's own audio
/// while it plays on speakers. <see cref="MicGate"/> reopens off the <b>queued playback duration</b>, not
/// the priority-routed BotStopped control frame (which arrives while audio is still buffered), plus a
/// short hangover. <c>Voxa:Studio:AllowBargeIn=true</c> disables it for headphone/full-duplex use.
/// </summary>
public class MicGateTests
{
    // Controllable clock so the playback model is asserted deterministically, no real waiting.
    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan d) => Now += d;
    }

    private static MicGate Gate(Clock clock, bool allowBargeIn = false, int hangoverMs = 250)
        => new(allowBargeIn, TimeSpan.FromMilliseconds(hangoverMs), () => clock.Now);

    [Fact]
    public void Gate_Is_Open_Before_Any_Bot_Speech()
        => Assert.True(Gate(new Clock()).ShouldIngest());

    [Fact]
    public void Gate_Closes_While_The_Bot_Is_Speaking()
    {
        var gate = Gate(new Clock());
        gate.BotStartedSpeaking();
        Assert.False(gate.ShouldIngest());
    }

    [Fact]
    public void Mic_Stays_Closed_Until_Queued_Audio_Drains_Then_Reopens()
    {
        var clock = new Clock();
        var gate = Gate(clock, hangoverMs: 250);

        gate.BotStartedSpeaking();
        gate.NoteRenderedAudio(TimeSpan.FromSeconds(1)); // 1s of audio queued for playback
        gate.BotStoppedSpeaking();                       // control frame arrives immediately (priority)
        Assert.False(gate.ShouldIngest());               // still playing the queued second

        clock.Advance(TimeSpan.FromMilliseconds(900));   // 900ms < 1000 + 250 hangover
        Assert.False(gate.ShouldIngest());

        clock.Advance(TimeSpan.FromMilliseconds(400));    // 1300ms > 1250ms → drained
        Assert.True(gate.ShouldIngest());
    }

    [Fact]
    public void Control_Frame_Alone_Does_Not_Reopen_While_Audio_Is_Still_Queued()
    {
        // Codex P1 regression: BotStopped is priority-routed and lands long before a multi-second TTS
        // response has finished playing. Reopening on its timestamp (old behavior) would unmute the mic
        // mid-playback and let the feedback loop recur. The gate must follow the queued audio, not the frame.
        var clock = new Clock();
        var gate = Gate(clock, hangoverMs: 250);

        gate.BotStartedSpeaking();
        gate.NoteRenderedAudio(TimeSpan.FromSeconds(3)); // long response still on the speakers
        gate.BotStoppedSpeaking();

        clock.Advance(TimeSpan.FromMilliseconds(300));    // past the OLD 250ms hangover-from-stop
        Assert.False(gate.ShouldIngest());                // ...but 3s of audio is still audible
    }

    [Fact]
    public void Queued_Audio_Accumulates_Across_Frames()
    {
        var clock = new Clock();
        var gate = Gate(clock, hangoverMs: 0); // isolate the FIFO accumulation from the hangover

        gate.BotStartedSpeaking();
        gate.NoteRenderedAudio(TimeSpan.FromMilliseconds(500));
        gate.NoteRenderedAudio(TimeSpan.FromMilliseconds(500)); // tail is now 1000ms out, not 500ms
        gate.BotStoppedSpeaking();

        clock.Advance(TimeSpan.FromMilliseconds(900));
        Assert.False(gate.ShouldIngest());

        clock.Advance(TimeSpan.FromMilliseconds(150));        // 1050ms > 1000ms
        Assert.True(gate.ShouldIngest());
    }

    [Fact]
    public void Barge_In_Flush_Collapses_The_Playback_Tail()
    {
        var clock = new Clock();
        var gate = Gate(clock, hangoverMs: 250);

        gate.BotStartedSpeaking();
        gate.NoteRenderedAudio(TimeSpan.FromSeconds(5)); // a long tail is queued
        gate.BotStoppedSpeaking();
        Assert.False(gate.ShouldIngest());

        gate.PlaybackFlushed();                           // barge-in dropped the queued samples
        Assert.False(gate.ShouldIngest());                // still inside the hangover
        clock.Advance(TimeSpan.FromMilliseconds(300));    // past it → reopened, not stuck for 5s
        Assert.True(gate.ShouldIngest());
    }

    [Fact]
    public void Allow_Barge_In_Keeps_The_Gate_Open_Even_While_The_Bot_Speaks()
    {
        var gate = Gate(new Clock(), allowBargeIn: true);
        gate.BotStartedSpeaking();
        gate.NoteRenderedAudio(TimeSpan.FromSeconds(2));
        Assert.True(gate.ShouldIngest()); // full-duplex: user can interrupt (use headphones)
    }
}
