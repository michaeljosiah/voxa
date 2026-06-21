using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Voxa.AspNetCore;
using Voxa.Frames;
using Voxa.Speech;
using Voxa.Testing.Processors;

namespace Voxa.Transports.Twilio.Tests;

/// <summary>
/// End-to-end tests for <c>MapVoxaTwilioVoice</c> over TestServer (VTL-001 T2.3/T2.4): the TwiML webhook
/// (signature gate + Stream URL), and the media WebSocket route composing the SAME pipeline as the native
/// route and decoding inbound Twilio media into AudioRawFrames at the announced rate.
/// </summary>
public class MapVoxaTwilioVoiceTests
{
    private static async Task<IHost> StartHostAsync(
        Action<IServiceCollection> configureServices,
        Action<IEndpointRouteBuilder> mapRoutes,
        IEnumerable<KeyValuePair<string, string?>>? appConfig = null)
    {
        return await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureAppConfiguration(c => { if (appConfig is not null) c.AddInMemoryCollection(appConfig); })
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    configureServices(services);
                })
                .Configure(app =>
                {
                    app.UseWebSockets();
                    app.UseRouting();
                    app.UseEndpoints(mapRoutes);
                }))
            .StartAsync();
    }

    private static IConfiguration Config(params (string Key, string Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();

    private static KeyValuePair<string, string?>[] Pairs(params (string Key, string Value)[] pairs)
        => pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)).ToArray();

    // ── Webhook (TwiML) ─────────────────────────────────────────────────────

    [Fact]
    public async Task Webhook_Returns_TwiML_Pointing_At_The_Media_Route()
    {
        using var host = await StartHostAsync(
            _ => { },
            ep => ep.MapVoxaTwilioVoice("/twilio", o => o.ValidateSignature = false));

        var client = host.GetTestServer().CreateClient();
        var response = await client.GetAsync("/twilio");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/xml", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("<Connect>", body);
        Assert.Contains("<Stream url=\"wss://localhost/twilio/media\"", body);
    }

    [Fact]
    public async Task Webhook_Binds_ValidateSignature_From_Configuration()
    {
        // No code override — ValidateSignature=false comes from Voxa:Telephony:Twilio in app config.
        using var host = await StartHostAsync(
            _ => { },
            ep => ep.MapVoxaTwilioVoice("/twilio"),
            appConfig: Pairs(("Voxa:Telephony:Twilio:ValidateSignature", "false")));

        var response = await host.GetTestServer().CreateClient().GetAsync("/twilio");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_Honors_PublicWssBaseUrl()
    {
        using var host = await StartHostAsync(
            _ => { },
            ep => ep.MapVoxaTwilioVoice("/twilio", o =>
            {
                o.ValidateSignature = false;
                o.PublicWssBaseUrl = "wss://abc123.ngrok.io";
            }));

        var body = await host.GetTestServer().CreateClient().GetStringAsync("/twilio");
        Assert.Contains("<Stream url=\"wss://abc123.ngrok.io/twilio/media\"", body);
    }

    [Fact]
    public async Task Webhook_FailsClosed_With_500_When_Validation_On_But_Token_Missing()
    {
        // Default options: ValidateSignature=true, no AuthToken → must not wave requests through.
        using var host = await StartHostAsync(_ => { }, ep => ep.MapVoxaTwilioVoice("/twilio"));

        var response = await host.GetTestServer().CreateClient().GetAsync("/twilio");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_Rejects_Request_With_Invalid_Signature()
    {
        using var host = await StartHostAsync(
            _ => { },
            ep => ep.MapVoxaTwilioVoice("/twilio", o =>
            {
                o.ValidateSignature = true;
                o.AuthToken = "test-token";
            }));

        var client = host.GetTestServer().CreateClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["CallSid"] = "CA1" });
        // No (valid) X-Twilio-Signature header → reject.
        var response = await client.PostAsync("/twilio", form);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ── Media route ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Media_Route_Requires_WebSocket_Upgrade()
    {
        using var host = await StartHostAsync(
            _ => { },
            ep => ep.MapVoxaTwilioVoice("/twilio", o => o.ValidateSignature = false));

        var response = await host.GetTestServer().CreateClient().GetAsync("/twilio/media");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Media_Route_Composes_Pipeline_And_Decodes_Inbound_Twilio_Media()
    {
        // The capturing processor stands in as the VAD — first in the composed chain — so it sees the
        // AudioRawFrames exactly as the telephony source decoded them from the Twilio media events.
        var capture = new CapturingProcessor();
        var config = Config(
            ("Voxa:Stt", "FakeStt"),
            ("Voxa:Tts", "FakeTts"),
            ("Voxa:Vad:Engine", "CapturingVad"));

        using var host = await StartHostAsync(
            services =>
            {
                services.AddVoxa(config, voxa =>
                {
                    voxa.AddProvider(new VoxaSttDescriptor(
                        Name: "FakeStt", ConfigSection: "FakeStt", PreferredInputSampleRate: 16000,
                        Validate: _ => [],
                        CreateProcessor: (_, _) => new SpeechToTextProcessor(new FakeSttEngine())));
                    voxa.AddProvider(new VoxaTtsDescriptor(
                        Name: "FakeTts", ConfigSection: "FakeTts", OutputSampleRate: 24000,
                        Validate: _ => [],
                        CreateProcessor: (_, _) => new TextToSpeechProcessor(new FakeTtsEngine())));
                    voxa.AddProvider(new VoxaVadDescriptor(
                        Name: "CapturingVad",
                        CreateProcessor: (_, _) => capture));
                });
                services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(new NoopChatClient());
            },
            ep => ep.MapVoxaTwilioVoice("/twilio", o => o.ValidateSignature = false));

        var client = host.GetTestServer().CreateWebSocketClient();
        using var socket = await client.ConnectAsync(new Uri("ws://localhost/twilio/media"), CancellationToken.None);

        await SendTextAsync(socket, "{\"event\":\"start\",\"start\":{\"streamSid\":\"MZ1\",\"mediaFormat\":{\"encoding\":\"audio/x-mulaw\",\"sampleRate\":8000,\"channels\":1}},\"streamSid\":\"MZ1\"}");

        // 160 μ-law samples of 0x80 (decodes to +32124) — one 20 ms inbound chunk.
        var chunk = new byte[160];
        Array.Fill(chunk, (byte)0x80);
        await SendTextAsync(socket, $"{{\"event\":\"media\",\"media\":{{\"track\":\"inbound\",\"payload\":\"{Convert.ToBase64String(chunk)}\"}},\"streamSid\":\"MZ1\"}}");

        await capture.WaitForAsync(f => f is AudioRawFrame, TimeSpan.FromSeconds(5));

        var audio = capture.Captured.OfType<AudioRawFrame>().FirstOrDefault();
        Assert.NotNull(audio);
        Assert.Equal(16000, audio!.SampleRate);   // composed input rate (FakeStt) — source resampled 8 k → 16 k
        var pcm = MemoryMarshal.Cast<byte, short>(audio.Pcm.Span);
        Assert.Equal((short)32124, pcm[0]);        // 0x80 μ-law decoded through the real Twilio codec

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    private static Task SendTextAsync(System.Net.WebSockets.WebSocket socket, string json)
        => socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    // ── Fakes (mirrors the native VoxaVoiceRoute end-to-end test) ────────────

    private sealed class FakeSttEngine : ISpeechToTextEngine
    {
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.WhenAny(_stopped.Task, Task.Delay(Timeout.Infinite, ct)).ConfigureAwait(false);
            yield break;
        }
        public Task StopAsync() { _stopped.TrySetResult(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTtsEngine : ITextToSpeechEngine
    {
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(string text, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopChatClient : Microsoft.Extensions.AI.IChatClient
    {
        public Task<Microsoft.Extensions.AI.ChatResponse> GetResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<Microsoft.Extensions.AI.ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
            Microsoft.Extensions.AI.ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
