using Voxa.Studio.Services;

namespace Voxa.Studio.Tests;

/// <summary>
/// Half-duplex echo suppression (ROADMAP P1): the Talk mic pump must not ingest the bot's own audio
/// while it plays on speakers. <see cref="MicGate"/> closes the gate for the bot turn plus a short
/// hangover; <c>Voxa:Studio:AllowBargeIn=true</c> disables it for headphone/full-duplex use.
/// </summary>
public class MicGateTests
{
    // Controllable clock so the hangover is asserted deterministically, no real waiting.
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
    public void Gate_Stays_Closed_Through_The_Hangover_Then_Reopens()
    {
        var clock = new Clock();
        var gate = Gate(clock, hangoverMs: 250);

        gate.BotStartedSpeaking();
        gate.BotStoppedSpeaking();          // hangover starts now
        Assert.False(gate.ShouldIngest());  // still draining the speaker buffer

        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.False(gate.ShouldIngest());  // not yet

        clock.Advance(TimeSpan.FromMilliseconds(60));
        Assert.True(gate.ShouldIngest());   // hangover elapsed → mic live again
    }

    [Fact]
    public void Allow_Barge_In_Keeps_The_Gate_Open_Even_While_The_Bot_Speaks()
    {
        var gate = Gate(new Clock(), allowBargeIn: true);
        gate.BotStartedSpeaking();
        Assert.True(gate.ShouldIngest()); // full-duplex: user can interrupt (use headphones)
    }
}
