using System.Threading.Channels;
using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Speech;
using Voxa.Testing.Processors;

namespace Voxa.Services.SpeechToSpeech.Tests;

/// <summary>
/// Drives <see cref="SpeechToSpeechProcessor"/> through a real pipeline against a fake session (no model),
/// asserting the frame-parity contract the composite shares with the cloud realtime processors.
/// </summary>
public class SpeechToSpeechProcessorTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(3);

    private sealed record Harness(PipelineRunner Runner, FakeS2SSession Session, CapturingProcessor Captured, Pipeline Pipeline);

    private static Harness Build(SpeechToSpeechOptions? options = null, FakeS2SSession? session = null)
    {
        var s = session ?? new FakeS2SSession();
        var processor = new SpeechToSpeechProcessor(() => s, options ?? new SpeechToSpeechOptions { Voice = "nova" });
        var captured = new CapturingProcessor("after-s2s");

        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(processor)
            .Then(captured)
            .Sink(new PipelineSink());

        return new Harness(new PipelineRunner(pipeline), s, captured, pipeline);
    }

    [Fact]
    public async Task Sets_voice_and_system_prompt_on_start()
    {
        var h = Build(new SpeechToSpeechOptions { Voice = "nova", SystemPrompt = "Be brief." });
        await using (h.Runner)
        {
            await h.Runner.StartAsync();
            await WaitUntil(() => h.Session.Voice is not null, WaitTimeout);
            Assert.Equal("nova", h.Session.Voice);
            Assert.Equal("Be brief.", h.Session.SystemPrompt);
        }
    }

    [Fact]
    public async Task Forwards_user_audio_to_the_session()
    {
        var h = Build();
        await using (h.Runner)
        {
            await h.Runner.StartAsync();
            var pcm = new byte[] { 1, 2, 3, 4 };
            await h.Pipeline.Source.IngestAsync(new AudioRawFrame(pcm, 16000, 1));

            await WaitUntil(() => h.Session.Appended.Count > 0, WaitTimeout);
            Assert.Equal(pcm, h.Session.Appended[0]);
        }
    }

    [Fact]
    public async Task Agent_audio_emits_bot_started_then_audio_raw()
    {
        var h = Build();
        await using (h.Runner)
        {
            await h.Runner.StartAsync();
            var pcm = new byte[] { 9, 9, 9, 9 };
            await h.Session.EmitAsync(new SpeechToSpeechChunk(pcm, null, IsFinal: false));

            var audio = await WaitForCaptured<AudioRawFrame>(h.Captured);
            Assert.Contains(h.Captured.Captured, f => f is BotStartedSpeakingFrame);
            Assert.NotNull(audio);
            Assert.Equal(pcm, audio!.Pcm.ToArray());
            Assert.Equal(24000, audio.SampleRate); // the session's OutputSampleRate
            Assert.Equal(1, audio.Channels);
        }
    }

    [Fact]
    public async Task Agent_text_emits_llm_text_chunk()
    {
        var h = Build();
        await using (h.Runner)
        {
            await h.Runner.StartAsync();
            await h.Session.EmitAsync(new SpeechToSpeechChunk(default, "hello", IsFinal: false));

            var text = await WaitForCaptured<LlmTextChunkFrame>(h.Captured);
            Assert.NotNull(text);
            Assert.Equal("hello", text!.Text);
        }
    }

    [Fact]
    public async Task Final_chunk_emits_bot_stopped()
    {
        var h = Build();
        await using (h.Runner)
        {
            await h.Runner.StartAsync();
            await h.Session.EmitAsync(new SpeechToSpeechChunk(new byte[] { 1, 2 }, null, IsFinal: false));
            await WaitForCaptured<BotStartedSpeakingFrame>(h.Captured);

            await h.Session.EmitAsync(new SpeechToSpeechChunk(default, null, IsFinal: true));
            await WaitForCaptured<BotStoppedSpeakingFrame>(h.Captured);

            Assert.Contains(h.Captured.Captured, f => f is BotStartedSpeakingFrame);
            Assert.Contains(h.Captured.Captured, f => f is BotStoppedSpeakingFrame);
        }
    }

    [Fact]
    public async Task User_speech_events_emit_user_frames()
    {
        var h = Build();
        await using (h.Runner)
        {
            await h.Runner.StartAsync();
            await h.Session.EmitAsync(new SpeechToSpeechChunk(default, null, false, SpeechToSpeechEvent.UserStartedSpeaking));
            await h.Session.EmitAsync(new SpeechToSpeechChunk(default, null, false, SpeechToSpeechEvent.UserStoppedSpeaking));

            await WaitForCaptured<UserStoppedSpeakingFrame>(h.Captured);
            Assert.Contains(h.Captured.Captured, f => f is UserStartedSpeakingFrame);
            Assert.Contains(h.Captured.Captured, f => f is UserStoppedSpeakingFrame);
        }
    }

    [Fact]
    public async Task Model_interruption_emits_interruption_and_resets_speaking_edge()
    {
        var h = Build();
        await using (h.Runner)
        {
            await h.Runner.StartAsync();
            await h.Session.EmitAsync(new SpeechToSpeechChunk(new byte[] { 1, 2 }, null, false)); // bot starts
            await WaitForCaptured<BotStartedSpeakingFrame>(h.Captured);
            var startsBefore = h.Captured.Captured.Count(f => f is BotStartedSpeakingFrame);

            await h.Session.EmitAsync(new SpeechToSpeechChunk(default, null, false, SpeechToSpeechEvent.Interrupted));
            await WaitForCaptured<InterruptionFrame>(h.Captured);

            // The speaking edge was reset, so the next agent audio re-announces BotStarted.
            await h.Session.EmitAsync(new SpeechToSpeechChunk(new byte[] { 3, 4 }, null, false));
            await WaitUntil(() => h.Captured.Captured.Count(f => f is BotStartedSpeakingFrame) > startsBefore, WaitTimeout);
        }
    }

    [Fact]
    public async Task Upstream_interruption_cancels_the_session_and_forwards_the_frame()
    {
        var h = Build();
        await using (h.Runner)
        {
            await h.Runner.StartAsync();
            await h.Pipeline.Source.IngestAsync(new InterruptionFrame());

            await WaitUntil(() => h.Session.CancelCount > 0, WaitTimeout);
            Assert.Equal(1, h.Session.CancelCount);
            await WaitForCaptured<InterruptionFrame>(h.Captured); // forwarded downstream
        }
    }

    [Fact]
    public async Task Read_loop_failure_surfaces_as_pipeline_failure()
    {
        var h = Build();
        await using (h.Runner)
        {
            await h.Runner.StartAsync();
            h.Session.Fail(new InvalidOperationException("s2s boom"));

            var ex = await Assert.ThrowsAsync<PipelineFailedException>(
                async () => await h.Runner.WaitAsync().WaitAsync(WaitTimeout));
            Assert.Contains("s2s boom", ex.Message);
        }
    }

    [Fact]
    public async Task Disposes_session_on_end()
    {
        var h = Build();
        await using (h.Runner)
        {
            await h.Runner.StartAsync();
            await h.Pipeline.Source.IngestAsync(new EndFrame());
            await WaitUntil(() => h.Session.Disposed, WaitTimeout);
            Assert.True(h.Session.Disposed);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("Condition not met within timeout.");
    }

    private static async Task<T?> WaitForCaptured<T>(CapturingProcessor captured) where T : Frame
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var match = captured.Captured.OfType<T>().FirstOrDefault();
            if (match is not null) return match;
            await Task.Delay(10);
        }
        return null;
    }

    /// <summary>In-memory full-duplex session: records calls, and scripts the response stream via a channel.</summary>
    private sealed class FakeS2SSession : ISpeechToSpeechSession
    {
        private readonly Channel<SpeechToSpeechChunk> _out = Channel.CreateUnbounded<SpeechToSpeechChunk>();

        public int OutputSampleRate { get; init; } = 24000;
        public string? Voice { get; private set; }
        public string? SystemPrompt { get; private set; }
        public List<byte[]> Appended { get; } = [];
        public int CancelCount { get; private set; }
        public int ResetCount { get; private set; }
        public bool Disposed { get; private set; }

        public ValueTask AppendUserAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
        {
            Appended.Add(pcm.ToArray());
            return ValueTask.CompletedTask;
        }

        public IAsyncEnumerable<SpeechToSpeechChunk> RespondAsync(CancellationToken ct)
            => _out.Reader.ReadAllAsync(ct);

        public ValueTask SetVoiceAsync(string voiceId, CancellationToken ct) { Voice = voiceId; return ValueTask.CompletedTask; }
        public ValueTask SetSystemPromptAsync(string systemPrompt, CancellationToken ct) { SystemPrompt = systemPrompt; return ValueTask.CompletedTask; }
        public ValueTask ResetSessionAsync(CancellationToken ct) { ResetCount++; return ValueTask.CompletedTask; }
        public ValueTask CancelAsync(CancellationToken ct) { CancelCount++; return ValueTask.CompletedTask; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            _out.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        // Test driver: push a chunk into RespondAsync, or complete the stream with an error.
        public ValueTask EmitAsync(SpeechToSpeechChunk chunk) => _out.Writer.WriteAsync(chunk);
        public void Fail(Exception ex) => _out.Writer.TryComplete(ex);
    }
}
