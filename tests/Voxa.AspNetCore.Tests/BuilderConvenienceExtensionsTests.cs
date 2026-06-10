using Microsoft.AspNetCore.Http;
using Voxa.Speech;

namespace Voxa.AspNetCore.Tests;

/// <summary>
/// Smoke coverage for the convenience extension methods that live on top of
/// <see cref="VoicePipelineBuilder.UseProcessor"/> — verifies they register the right factory
/// without needing a full pipeline run.
/// </summary>
public class BuilderConvenienceExtensionsTests
{
    [Fact]
    public void UseSpeechToText_Registers_Per_Connection_Factory()
    {
        var builder = new VoicePipelineBuilder();
        var calls = 0;

        builder.UseSpeechToText(() =>
        {
            calls++;
            return new SpeechToTextProcessor(new ScriptedSttEngine());
        });

        Assert.Single(builder.ProcessorFactories);

        // Factory should be invoked per connection.
        var ctx = new DefaultHttpContext();
        var p1 = builder.ProcessorFactories[0](ctx);
        var p2 = builder.ProcessorFactories[0](ctx);
        Assert.NotSame(p1, p2);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void UseTextToSpeech_Registers_Per_Connection_Factory()
    {
        var builder = new VoicePipelineBuilder();
        builder.UseTextToSpeech(() => new TextToSpeechProcessor(new ScriptedTtsEngine(), outputSampleRate: 24000));

        Assert.Single(builder.ProcessorFactories);
        var ctx = new DefaultHttpContext();
        var p = builder.ProcessorFactories[0](ctx);
        Assert.IsType<TextToSpeechProcessor>(p);
    }

    [Fact]
    public void UseSentenceAggregator_Registers_New_Instance_Per_Connection()
    {
        var builder = new VoicePipelineBuilder();
        builder.UseSentenceAggregator();

        var ctx = new DefaultHttpContext();
        var p1 = builder.ProcessorFactories[0](ctx);
        var p2 = builder.ProcessorFactories[0](ctx);

        Assert.IsType<SentenceAggregator>(p1);
        Assert.NotSame(p1, p2);
    }

    [Fact]
    public void UseTranscriptionFilter_And_UseSilenceGate_Register_Processors()
    {
        var builder = new VoicePipelineBuilder()
            .UseTranscriptionFilter()
            .UseSilenceGate();

        var ctx = new DefaultHttpContext();
        var processors = builder.ProcessorFactories.Select(f => f(ctx)).ToArray();

        Assert.IsType<TranscriptionFilter>(processors[0]);
        Assert.IsType<SilenceGateProcessor>(processors[1]);
    }

    // ── Test stubs ─────────────────────────────────────────────────────────

    private sealed class ScriptedSttEngine : ISpeechToTextEngine
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct) => ValueTask.CompletedTask;
        public IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync(CancellationToken ct)
            => AsyncEnumerable.Empty<TranscriptionResult>();
    }

    private sealed class ScriptedTtsEngine : ITextToSpeechEngine
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(string text, CancellationToken ct)
            => AsyncEnumerable.Empty<ReadOnlyMemory<byte>>();
    }

    private static class AsyncEnumerable
    {
        public static IAsyncEnumerable<T> Empty<T>() => EmptyImpl<T>();
#pragma warning disable CS1998
        private static async IAsyncEnumerable<T> EmptyImpl<T>() { yield break; }
#pragma warning restore CS1998
    }
}
