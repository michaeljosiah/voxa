namespace Voxa.Speech;

/// <summary>
/// Decides whether the user's turn is actually over when the VAD reaches its silence timeout (P0 smart
/// turn). This is the classifier behind <see cref="VoxaVadSettings.ConfirmTurnEnd"/>: silence-end fires →
/// the classifier inspects the recent speech audio → only a "complete" verdict emits
/// <c>UserStoppedSpeakingFrame</c>. With one wired, the VAD's <c>StopDuration</c> can drop to ~200 ms
/// without cutting off speakers who pause mid-sentence.
///
/// <para>
/// Register an implementation in DI and the default composer wires it into the VAD automatically;
/// implementations may run a remote endpoint (<c>HttpSmartTurnClassifier</c>), a local ONNX model, etc.
/// </para>
/// </summary>
public interface ISmartTurnClassifier
{
    /// <summary>
    /// True when the recent speech — 16-bit PCM mono at <paramref name="sampleRate"/>, the audio (up to
    /// ~1 s) leading into the silence — sounds like a completed turn; false to treat the silence as a
    /// mid-sentence pause and keep the gate open. Must be fast (it sits on the turn-taking path) and
    /// should fail "complete" so a classifier error never strands the conversation.
    /// </summary>
    ValueTask<bool> IsTurnCompleteAsync(ReadOnlyMemory<byte> recentSpeechPcm, int sampleRate, CancellationToken ct);
}
