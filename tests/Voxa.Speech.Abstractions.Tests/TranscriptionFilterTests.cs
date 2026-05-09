using Voxa.Frames;
using Voxa.Pipelines;
using Voxa.Processors;
using Voxa.Speech;
using Voxa.Testing.Processors;

namespace Voxa.Speech.Abstractions.Tests;

public class TranscriptionFilterTests
{
    private static (PipelineRunner Runner, CapturingProcessor Captured, Pipeline Pipeline, TranscriptionFilter Filter) Build(
        TranscriptionFilter? filter = null)
    {
        filter ??= new TranscriptionFilter();
        var captured = new CapturingProcessor();
        var pipeline = Pipeline.Build()
            .Source(new PipelineSource())
            .Then(filter)
            .Then(captured)
            .Sink(new PipelineSink());
        return (new PipelineRunner(pipeline), captured, pipeline, filter);
    }

    [Theory]
    [InlineData("Thank you.")]
    [InlineData("thank you")]
    [InlineData("THANK YOU")]
    [InlineData("Bye.")]
    [InlineData("you")]
    [InlineData(".")]
    [InlineData("Subscribe")]
    [InlineData("Thanks for watching!")]
    public async Task Drops_Common_Whisper_Hallucinations(string hallucination)
    {
        var (runner, captured, pipeline, filter) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);
            await pipeline.Source.IngestAsync(new TranscriptionFrame(hallucination, IsFinal: true));
            await Task.Delay(80);

            Assert.DoesNotContain(captured.Captured, f => f is TranscriptionFrame);
            Assert.Equal(1, filter.DropCount);
        }
    }

    [Theory]
    [InlineData("Hello, how are you today?")]
    [InlineData("Can you help me set up an invoice?")]
    [InlineData("What's the weather in Lagos?")]
    public async Task Passes_Real_Speech_Through(string utterance)
    {
        var (runner, captured, pipeline, filter) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);
            await pipeline.Source.IngestAsync(new TranscriptionFrame(utterance, IsFinal: true));
            await Task.Delay(80);

            var passed = captured.Captured.OfType<TranscriptionFrame>().FirstOrDefault();
            Assert.NotNull(passed);
            Assert.Equal(utterance, passed!.Text);
            Assert.Equal(0, filter.DropCount);
        }
    }

    [Fact]
    public async Task Forwards_Non_Transcription_Frames_Unchanged()
    {
        var (runner, captured, pipeline, _) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);
            await pipeline.Source.IngestAsync(new UserStoppedSpeakingFrame());
            await pipeline.Source.IngestAsync(new TextFrame("This is text not a transcription"));
            await Task.Delay(80);

            Assert.Contains(captured.Captured, f => f is UserStoppedSpeakingFrame);
            Assert.Contains(captured.Captured, f => f is TextFrame txt && txt.Text == "This is text not a transcription");
        }
    }

    [Fact]
    public async Task Does_Not_Filter_Interim_Transcriptions()
    {
        // Interim (non-final) results from streaming STT shouldn't be filtered — they're just status,
        // not yet committed text. The filter only operates on IsFinal=true.
        var (runner, captured, pipeline, _) = Build();
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);
            await pipeline.Source.IngestAsync(new TranscriptionFrame("you", IsFinal: false));
            await Task.Delay(80);

            Assert.Contains(captured.Captured, f => f is TranscriptionFrame t && !t.IsFinal);
        }
    }

    [Fact]
    public async Task Custom_Blocklist_Replaces_Defaults()
    {
        var customFilter = new TranscriptionFilter
        {
            ExactBlocklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "potato" },
            SubstringBlocklist = Array.Empty<string>(),
        };
        var (runner, captured, pipeline, filter) = Build(customFilter);
        await using (runner)
        {
            await runner.StartAsync();
            await Task.Delay(40);
            // Default-blocked, should NOW pass because we replaced the blocklist.
            await pipeline.Source.IngestAsync(new TranscriptionFrame("Thank you.", IsFinal: true));
            // Custom-blocked, should drop.
            await pipeline.Source.IngestAsync(new TranscriptionFrame("potato", IsFinal: true));
            await Task.Delay(80);

            Assert.Contains(captured.Captured, f => f is TranscriptionFrame t && t.Text == "Thank you.");
            Assert.DoesNotContain(captured.Captured, f => f is TranscriptionFrame t && t.Text == "potato");
            Assert.Equal(1, filter.DropCount);
        }
    }
}
