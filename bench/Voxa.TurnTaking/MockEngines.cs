using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Voxa.Speech;

namespace Voxa.TurnTaking;

/// <summary>
/// Deterministic, offline STT for the default lane. Emits a single fixed final transcript when the turn
/// ends — on <c>FlushAsync</c> (the VAD's <c>UserStoppedSpeakingFrame</c>) if that fires, otherwise on
/// <c>StopAsync</c> (the <c>EndFrame</c> teardown) — so a transcript always flows without a model or network.
/// </summary>
internal sealed class MockSpeechToTextEngine : ISpeechToTextEngine
{
    public const string FixedTranscript = "this is a turn taking benchmark sample";

    private readonly Channel<TranscriptionResult> _out = Channel.CreateUnbounded<TranscriptionResult>();
    private bool _emitted;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct) => ValueTask.CompletedTask;

    public IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync(CancellationToken ct)
        => _out.Reader.ReadAllAsync(ct);

    public Task FlushAsync()
    {
        EmitFinal();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        EmitFinal();
        _out.Writer.TryComplete(); // ends the processor's read loop on EndFrame teardown
        return Task.CompletedTask;
    }

    private void EmitFinal()
    {
        if (_emitted) return;
        _emitted = true;
        _out.Writer.TryWrite(new TranscriptionResult(FixedTranscript, IsFinal: true));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Deterministic, offline TTS for the default lane. Yields a fixed amount of 16-bit silence per call so the
/// response WAV is real PCM bytes (exercising streaming + the sink) without invoking a vocoder. 24 kHz mono.
/// </summary>
internal sealed class MockTextToSpeechEngine : ITextToSpeechEngine
{
    public const int SampleRate = 24000;
    private const int Chunks = 2;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
        string text, [EnumeratorCancellation] CancellationToken ct)
    {
        var chunk = new byte[SampleRate / 10 * 2]; // ~100 ms of 16-bit mono silence
        for (int i = 0; i < Chunks; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return chunk;
            await Task.Yield();
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
