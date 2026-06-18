using System.Runtime.CompilerServices;
using Voxa.Speech;
using Voxa.Studio.Audio;
using Voxa.Studio.Services;

namespace Voxa.Studio.Tests;

/// <summary>
/// Headless coverage for the VST-004 dictation core: record → transcribe, final-only joining, and the
/// failure path — all with fakes, no real device or model.
/// </summary>
public class DictationSessionTests
{
    [Fact]
    public async Task Records_Then_Transcribes_The_Buffered_Audio()
    {
        var device = new FakeAudioDevice(frames: 3);
        var session = new DictationSession(device, () => new FakeSttEngine("hello world"));
        var states = new List<DictationSession.DictationState>();
        session.StateChanged += states.Add;

        session.Start(device.CaptureEndpoints()[0]);
        Assert.Equal(DictationSession.DictationState.Recording, session.State);

        var text = await session.StopAndTranscribeAsync();

        Assert.Equal("hello world", text);
        Assert.Equal("hello world", session.Transcript);
        Assert.Equal(DictationSession.DictationState.Completed, session.State);
        Assert.Contains(DictationSession.DictationState.Transcribing, states);
    }

    [Fact]
    public async Task Transcribe_Joins_Only_Final_Results()
    {
        var session = new DictationSession(new FakeAudioDevice(0), () => new FakeSttEngine(
            new TranscriptionResult("partial", IsFinal: false),
            new TranscriptionResult("the", IsFinal: true),
            new TranscriptionResult("answer", IsFinal: true)));

        var text = await session.TranscribeAsync([1, 2, 3, 4], CancellationToken.None);

        Assert.Equal("the answer", text);
    }

    [Fact]
    public async Task A_Failing_Engine_Surfaces_As_Failed_State()
    {
        var session = new DictationSession(new FakeAudioDevice(1), () => new ThrowingSttEngine());

        session.Start(new AudioEndpoint("mic", "Mic", IsDefault: true));
        var text = await session.StopAndTranscribeAsync();

        Assert.Equal(string.Empty, text);
        Assert.Equal(DictationSession.DictationState.Failed, session.State);
        Assert.False(string.IsNullOrEmpty(session.ErrorMessage));
    }

    // ── fakes ───────────────────────────────────────────────────────────────

    private sealed class FakeAudioDevice(int frames) : IStudioAudioDevice
    {
        public IReadOnlyList<AudioEndpoint> CaptureEndpoints() => [new("mic", "Test Mic", true)];
        public IReadOnlyList<AudioEndpoint> RenderEndpoints() => [];

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
            AudioEndpoint microphone, int sampleRate, [EnumeratorCancellation] CancellationToken ct)
        {
            for (var i = 0; i < frames && !ct.IsCancellationRequested; i++)
            {
                await Task.Yield();
                yield return new byte[640]; // one 20 ms PCM16 frame at 16 kHz
            }
        }

        public ValueTask StartRenderAsync(AudioEndpoint speaker, int sampleRate, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RenderAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask FlushRenderAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSttEngine(params TranscriptionResult[] results) : ISpeechToTextEngine
    {
        public FakeSttEngine(string finalText) : this(new TranscriptionResult(finalText, IsFinal: true)) { }

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct) => ValueTask.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;

        public async IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var r in results)
            {
                await Task.Yield();
                yield return r;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingSttEngine : ISpeechToTextEngine
    {
        public Task StartAsync(CancellationToken ct) => throw new InvalidOperationException("synthetic engine failure");
        public ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct) => ValueTask.CompletedTask;
        public IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
