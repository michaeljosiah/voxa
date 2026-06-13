using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Voxa.Studio.Audio;

/// <summary>
/// The Metrics workbench's scripted input source (VST-002 §9.1): an <see cref="IStudioAudioDevice"/>
/// that "captures" queued utterance PCM instead of a microphone, padded with silence between
/// utterances, and discards rendered audio. Because it implements the same device contract,
/// <c>TalkSession</c> runs a scripted session through the exact pipeline a live one uses — same
/// pumps, same barge-in path — which is what makes scripted runs repeatable evidence rather than
/// a parallel code path.
///
/// <para>
/// Frames are paced to wall-clock (one 20 ms frame per 20 ms) so VAD hangover and stage timings
/// mean the same thing they do on a live mic. The pacing can be disabled for unit tests.
/// </para>
/// </summary>
internal sealed class ScriptedAudioDevice : IStudioAudioDevice
{
    private static readonly AudioEndpoint CaptureEndpoint = new("scripted", "Scripted source", IsDefault: true);
    private static readonly AudioEndpoint RenderEndpoint = new("scripted-out", "Silent output", IsDefault: true);

    private readonly ConcurrentQueue<byte[]> _utterances = new();

    /// <summary>Test seam: false removes the 20 ms wall-clock pacing (frames stream flat out).</summary>
    internal bool Paced { get; init; } = true;

    /// <summary>Queue one utterance (PCM16 mono at the session's input rate) for "capture".</summary>
    public void EnqueueUtterance(byte[] pcm) => _utterances.Enqueue(pcm);

    public IReadOnlyList<AudioEndpoint> CaptureEndpoints() => [CaptureEndpoint];
    public IReadOnlyList<AudioEndpoint> RenderEndpoints() => [RenderEndpoint];

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
        AudioEndpoint microphone, int sampleRate, [EnumeratorCancellation] CancellationToken ct)
    {
        var frameBytes = sampleRate / 50 * 2; // 20 ms of PCM16 mono
        var silence = new byte[frameBytes];
        byte[]? current = null;
        var offset = 0;
        var clock = Stopwatch.StartNew();
        long frameIndex = 0;

        while (!ct.IsCancellationRequested)
        {
            if (current is null && _utterances.TryDequeue(out var next) && next.Length > 0)
            {
                current = next;
                offset = 0;
            }

            ReadOnlyMemory<byte> frame;
            if (current is not null)
            {
                var len = Math.Min(frameBytes, current.Length - offset);
                if (len == frameBytes)
                {
                    frame = current.AsMemory(offset, len);
                }
                else
                {
                    // Final partial frame: pad with silence to keep the 20 ms contract.
                    var padded = new byte[frameBytes];
                    current.AsSpan(offset, len).CopyTo(padded);
                    frame = padded;
                }
                offset += len;
                if (offset >= current.Length) current = null;
            }
            else
            {
                frame = silence;
            }

            yield return frame;

            frameIndex++;
            if (Paced)
            {
                var due = TimeSpan.FromMilliseconds(frameIndex * 20.0);
                var wait = due - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    try { await Task.Delay(wait, ct); }
                    catch (OperationCanceledException) { yield break; }
                }
            }
        }
    }

    // Rendered audio is discarded — the run must stay unperturbed (§9.1), and the TTS leg's
    // numbers come from the hub's TtsChunkEvents, not from playback.
    public ValueTask StartRenderAsync(AudioEndpoint speaker, int sampleRate, CancellationToken ct) => ValueTask.CompletedTask;
    public ValueTask RenderAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct) => ValueTask.CompletedTask;
    public ValueTask FlushRenderAsync() => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
