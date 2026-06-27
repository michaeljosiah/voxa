using System.Net;
using System.Text;
using Voxa.Speech;
using Voxa.Speech.Mistral;
using Voxa.Speech.Voices;

namespace Voxa.Speech.Mistral.Tests;

/// <summary>
/// VVL-001 WS2: Mistral voice catalog/clone (Bearer, /v1/audio/voices) and the Voxtral STT engine —
/// utterance-buffered, one POST to /v1/audio/transcriptions per flush yielding one final transcript.
/// All offline via a fake handler.
/// </summary>
public class MistralVoiceAndSttTests
{
    private static MistralSpeechOptions Keyed() => new()
    {
        ApiKey = "ml-test",
        ApiBaseUrl = "https://api.mistral.ai/v1",
        InputSampleRate = 16000,
        SttBufferSeconds = 0,   // disable the backstop timer; the test drives FlushAsync explicitly
    };

    private sealed record Captured(HttpMethod Method, Uri Uri, string Auth, byte[] Body, string? ContentType);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<Captured> Snapshots { get; } = new();
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            var auth = request.Headers.Authorization?.ToString() ?? "";
            Snapshots.Add(new Captured(request.Method, request.RequestUri!, auth, body, request.Content?.Headers.ContentType?.MediaType));
            return Respond(request);
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    [Fact] // WS2-A1
    public async Task ListVoicesAsync_Maps_BuiltIn_As_Standard_And_Custom_As_Cloned()
    {
        var handler = new CapturingHandler
        {
            Respond = _ => Json("""
            { "voices": [ { "name": "alloy" }, { "id": "my-voice", "name": "My Voice", "description": "me" } ] }
            """),
        };
        var catalog = new MistralVoiceCatalog(Keyed(), new HttpClient(handler));

        var voices = await catalog.ListVoicesAsync(default);

        var snap = Assert.Single(handler.Snapshots);
        Assert.Equal(HttpMethod.Get, snap.Method);
        Assert.EndsWith("/audio/voices", snap.Uri.ToString());
        Assert.Equal("Bearer ml-test", snap.Auth);

        Assert.Collection(voices,
            v => { Assert.Equal("alloy", v.Id); Assert.Equal(VoiceKind.Standard, v.Kind); },
            v => { Assert.Equal("my-voice", v.Id); Assert.Equal("My Voice", v.DisplayName); Assert.Equal(VoiceKind.Cloned, v.Kind); });
    }

    [Fact] // WS2-A1
    public async Task CreateVoiceAsync_Posts_Multipart_And_Never_Leaks_The_Key_In_The_Body()
    {
        var handler = new CapturingHandler { Respond = _ => Json("""{ "id": "cloned-1" }""") };
        var catalog = new MistralVoiceCatalog(Keyed(), new HttpClient(handler));

        var voice = await catalog.CreateVoiceAsync(
            new VoiceCloneRequest("Hugo", [new VoiceSample("ref.wav", new byte[] { 7, 8, 9 })]), default);

        var snap = Assert.Single(handler.Snapshots);
        Assert.Equal(HttpMethod.Post, snap.Method);
        Assert.EndsWith("/audio/voices", snap.Uri.ToString());
        Assert.Equal("Bearer ml-test", snap.Auth);
        Assert.StartsWith("multipart/form-data", snap.ContentType);
        Assert.DoesNotContain("ml-test", Encoding.Latin1.GetString(snap.Body));

        Assert.Equal("cloned-1", voice.Id);
        Assert.Equal(VoiceKind.Cloned, voice.Kind);
    }

    [Fact] // WS2-A1 (missing key is typed, not an HTTP exception)
    public async Task A_Blank_Key_Throws_A_Typed_Missing_Key_Result()
    {
        var handler = new CapturingHandler();
        var catalog = new MistralVoiceCatalog(Keyed() with { ApiKey = "" }, new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<VoiceProviderException>(() => catalog.ListVoicesAsync(default));
        Assert.True(ex.MissingApiKey);
        Assert.Empty(handler.Snapshots);
    }

    [Fact] // WS2-A2
    public async Task Stt_Buffers_Two_Writes_And_Posts_Once_On_Flush_Yielding_One_Final()
    {
        var handler = new CapturingHandler { Respond = _ => Json("""{ "text": "hello world" }""") };
        await using var engine = new MistralSpeechToTextEngine(Keyed() with { SttStreaming = false }, new HttpClient(handler));
        await engine.StartAsync(default);

        await engine.WriteAudioAsync(new byte[320], default);   // 10 ms @ 16 kHz PCM16
        await engine.WriteAudioAsync(new byte[320], default);
        Assert.Empty(handler.Snapshots);                        // nothing posted while buffering

        await engine.FlushAsync();

        var snap = Assert.Single(handler.Snapshots);            // exactly one round-trip for the utterance
        Assert.Equal(HttpMethod.Post, snap.Method);
        Assert.EndsWith("/audio/transcriptions", snap.Uri.ToString());
        Assert.Equal("Bearer ml-test", snap.Auth);
        Assert.StartsWith("multipart/form-data", snap.ContentType);

        // One final transcript is readable.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = engine.ReadTranscriptsAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("hello world", reader.Current.Text);
        Assert.True(reader.Current.IsFinal);
    }

    [Fact] // WS2-A2 (empty buffer flush is a no-op)
    public async Task Flush_With_No_Audio_Posts_Nothing()
    {
        var handler = new CapturingHandler();
        await using var engine = new MistralSpeechToTextEngine(Keyed(), new HttpClient(handler));
        await engine.StartAsync(default);

        await engine.FlushAsync();

        Assert.Empty(handler.Snapshots);
    }

    /// <summary>Blocks in SendAsync until its token fires — proves the engine threads a live session token in.</summary>
    private sealed class BlockingHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool WasCancelled;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { WasCancelled = true; throw; }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
    };

    [Fact] // VVL-001 WS2 streaming: stream=true surfaces delta interims then one done final
    public async Task Streaming_Stt_Emits_Delta_Interims_Then_A_Done_Final()
    {
        var handler = new CapturingHandler
        {
            Respond = _ => Sse(
                "data: {\"type\":\"transcription.text.delta\",\"text\":\"the quick \"}\n\n" +
                "data: {\"type\":\"transcription.text.delta\",\"text\":\"brown fox\"}\n\n" +
                "data: {\"type\":\"transcription.done\",\"text\":\"the quick brown fox\",\"language\":\"en\"}\n\n" +
                "data: [DONE]\n\n"),
        };
        await using var engine = new MistralSpeechToTextEngine(Keyed(), new HttpClient(handler));
        await engine.StartAsync(default);

        await engine.WriteAudioAsync(new byte[320], default);
        await engine.FlushAsync();

        var snap = Assert.Single(handler.Snapshots);
        Assert.EndsWith("/audio/transcriptions", snap.Uri.ToString());
        Assert.Contains("name=stream", Encoding.Latin1.GetString(snap.Body));   // stream=true was sent

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var results = new List<TranscriptionResult>();
        await using var reader = engine.ReadTranscriptsAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        while (await reader.MoveNextAsync())
        {
            results.Add(reader.Current);
            if (reader.Current.IsFinal) break;
        }

        Assert.Equal("the quick ", results[0].Text);             // first delta as an interim
        Assert.False(results[0].IsFinal);
        Assert.Equal("the quick brown fox", results[1].Text);    // running interim accumulates
        Assert.False(results[1].IsFinal);
        var final = results[^1];
        Assert.True(final.IsFinal);
        Assert.Equal("the quick brown fox", final.Text);
        Assert.Equal("en", final.Language);
    }

    [Fact] // streaming with no done event — accumulated deltas still settle as the final
    public async Task Streaming_Stt_Settles_Accumulated_Deltas_When_No_Done_Arrives()
    {
        var handler = new CapturingHandler
        {
            Respond = _ => Sse("data: {\"type\":\"transcription.text.delta\",\"text\":\"partial only\"}\n\n"),
        };
        await using var engine = new MistralSpeechToTextEngine(Keyed(), new HttpClient(handler));
        await engine.StartAsync(default);

        await engine.WriteAudioAsync(new byte[320], default);
        await engine.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        TranscriptionResult? last = null;
        await using var reader = engine.ReadTranscriptsAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        while (await reader.MoveNextAsync())
        {
            last = reader.Current;
            if (reader.Current.IsFinal) break;
        }

        Assert.NotNull(last);
        Assert.True(last!.IsFinal);
        Assert.Equal("partial only", last.Text);
    }

    [Fact] // a second/per-segment done must not fire a second final (one utterance == one agent turn)
    public async Task Streaming_Stt_Emits_Exactly_One_Final_When_Multiple_Done_Events_Arrive()
    {
        var handler = new CapturingHandler
        {
            Respond = _ => Sse(
                "data: {\"type\":\"transcription.text.delta\",\"text\":\"hello\"}\n\n" +
                "data: {\"type\":\"transcription.done\",\"text\":\"hello\"}\n\n" +
                "data: {\"type\":\"transcription.done\",\"text\":\"hello again\"}\n\n"),   // stray second done
        };
        await using var engine = new MistralSpeechToTextEngine(Keyed(), new HttpClient(handler));
        await engine.StartAsync(default);

        await engine.WriteAudioAsync(new byte[320], default);
        await engine.FlushAsync();
        await engine.StopAsync();   // completes the channel so we can drain to the end

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var finals = new List<string>();
        await foreach (var r in engine.ReadTranscriptsAsync(cts.Token))
            if (r.IsFinal) finals.Add(r.Text);

        Assert.Equal(["hello"], finals);   // the stray second done is ignored
    }

    [Fact] // a [DONE] sentinel with no transcription.done must still settle accumulated deltas as the final
    public async Task Streaming_Stt_Settles_Deltas_On_A_Bare_Done_Sentinel()
    {
        var handler = new CapturingHandler
        {
            Respond = _ => Sse(
                "data: {\"type\":\"transcription.text.delta\",\"text\":\"sentinel only\"}\n\n" +
                "data: [DONE]\n\n"),   // ends the stream WITHOUT a transcription.done event
        };
        await using var engine = new MistralSpeechToTextEngine(Keyed(), new HttpClient(handler));
        await engine.StartAsync(default);

        await engine.WriteAudioAsync(new byte[320], default);
        await engine.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        TranscriptionResult? last = null;
        await using var reader = engine.ReadTranscriptsAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        while (await reader.MoveNextAsync())
        {
            last = reader.Current;
            if (reader.Current.IsFinal) break;
        }

        Assert.NotNull(last);
        Assert.True(last!.IsFinal);
        Assert.Equal("sentinel only", last.Text);
    }

    [Fact] // SSE spec: one event's data may span multiple data: lines, joined by '\n'
    public async Task Streaming_Stt_Joins_A_Multi_Line_Data_Field_Before_Parsing()
    {
        var handler = new CapturingHandler
        {
            // The JSON object is split across two data: lines — only the joined payload is valid JSON.
            Respond = _ => Sse("data: {\"type\":\"transcription.done\",\ndata: \"text\":\"joined ok\"}\n\n"),
        };
        await using var engine = new MistralSpeechToTextEngine(Keyed(), new HttpClient(handler));
        await engine.StartAsync(default);

        await engine.WriteAudioAsync(new byte[320], default);
        await engine.FlushAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        TranscriptionResult? final = null;
        await using var reader = engine.ReadTranscriptsAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        while (await reader.MoveNextAsync())
        {
            if (reader.Current.IsFinal) { final = reader.Current; break; }
        }

        Assert.NotNull(final);
        Assert.Equal("joined ok", final!.Text);
    }

    [Fact] // CQ-008: parity with the OpenAI engine — an aborted session cancels the in-flight transcription
    public async Task Flush_Cancels_The_In_Flight_Request_When_The_Session_Is_Torn_Down()
    {
        var handler = new BlockingHandler();
        await using var engine = new MistralSpeechToTextEngine(Keyed(), new HttpClient(handler));
        using var session = new CancellationTokenSource();
        await engine.StartAsync(session.Token);
        await engine.WriteAudioAsync(new byte[16000], default);

        var flush = engine.FlushAsync();                                 // in-flight; handler blocks on its ct
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));    // the request reached SendAsync

        session.Cancel();                                                // simulate pipeline teardown

        await flush.WaitAsync(TimeSpan.FromSeconds(5));                   // completes (cancelled), not ~100 s timeout
        Assert.True(handler.WasCancelled);
    }
}
