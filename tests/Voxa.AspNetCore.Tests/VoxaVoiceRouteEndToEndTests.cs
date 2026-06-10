using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Voxa.Speech;
using Voxa.Testing.Processors;

namespace Voxa.AspNetCore.Tests;

/// <summary>
/// End-to-end tests for the fluent MapVoxaVoice(pattern) route over TestServer.
/// Covers two defensive behaviors:
/// 1. RequireAuthorization()/RequireCors() inside a per-request Use(...) callback cannot attach
///    endpoint metadata (routing already evaluated it) — the request must fail closed, not serve
///    an endpoint the caller believes is protected.
/// 2. With UseDefaults(), the WebSocketAudioSource must tag inbound PCM with the sample rate the
///    session envelope announced — not the transport default (24 kHz) — so VAD/custom processors
///    relying on AudioRawFrame.SampleRate see the rate clients actually send at.
/// </summary>
public class VoxaVoiceRouteEndToEndTests
{
    private static async Task<IHost> StartHostAsync(
        Action<IServiceCollection> configureServices,
        Action<IEndpointRouteBuilder> mapRoutes)
    {
        return await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
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

    // ── 1. Request-time endpoint policies fail closed ───────────────────────

    [Fact]
    public async Task RequireAuthorization_Inside_Use_Callback_Fails_The_Request()
    {
        using var host = await StartHostAsync(
            _ => { },
            endpoints => endpoints.MapVoxaVoice("/voice")
                .Use((_, b) =>
                {
                    b.RequireAuthorization("MustBeAdmin");
                    b.UseProcessor(() => new PassthroughProcessor());
                }));

        var client = host.GetTestServer().CreateWebSocketClient();
        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => client.ConnectAsync(new Uri("ws://localhost/voice"), CancellationToken.None));
        Assert.Contains("500", ex.Message);
    }

    [Fact]
    public async Task Use_Callback_Without_Endpoint_Policies_Connects_Fine()
    {
        using var host = await StartHostAsync(
            _ => { },
            endpoints => endpoints.MapVoxaVoice("/voice")
                .Use((_, b) => b.UseProcessor(() => new PassthroughProcessor())));

        var client = host.GetTestServer().CreateWebSocketClient();
        using var socket = await client.ConnectAsync(new Uri("ws://localhost/voice"), CancellationToken.None);

        Assert.Equal(WebSocketState.Open, socket.State);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Route_Level_RequireAuthorization_Attaches_Endpoint_Metadata()
    {
        using var host = await StartHostAsync(
            services => services.AddAuthorization(),
            endpoints => endpoints.MapVoxaVoice("/voice")
                .Use((_, b) => b.UseProcessor(() => new PassthroughProcessor()))
                .RequireAuthorization("MustBeAdmin"));

        var endpoint = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.Single();

        var authData = endpoint.Metadata.GetMetadata<IAuthorizeData>();
        Assert.NotNull(authData);
        Assert.Equal("MustBeAdmin", authData!.Policy);
    }

    // ── 2. Source tags audio with the announced session rate ────────────────

    private sealed class FakeSttEngine : ISpeechToTextEngine
    {
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct) => ValueTask.CompletedTask;
        public async IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync(
            [EnumeratorCancellation] CancellationToken ct)
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
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(
            string text, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task UseDefaults_Source_Tags_Audio_With_The_Announced_Input_Rate()
    {
        // The capturing processor stands in as the VAD — first in the default chain, so it
        // observes AudioRawFrames exactly as the source tagged them.
        var capture = new CapturingProcessor();

        var config = Config(
            ("Voxa:Stt", "FakeStt"),
            ("Voxa:Tts", "FakeTts"),
            ("Voxa:Vad:Engine", "CapturingVad"),
            // Override away from BOTH the descriptor default (16000) and the transport
            // default (24000) so a pass can't be a coincidence of either.
            ("Voxa:FakeStt:InputSampleRate", "8000"));

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
                // DI agent wins over any IVoiceAgentFactory; never actually invoked here.
                services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(new NoopChatClient());
            },
            endpoints => endpoints.MapVoxaVoice("/voice").UseDefaults());

        var client = host.GetTestServer().CreateWebSocketClient();
        using var socket = await client.ConnectAsync(new Uri("ws://localhost/voice"), CancellationToken.None);

        // First text message must be the session envelope announcing the effective rate.
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Text, result.MessageType);
        using var envelope = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
        Assert.Equal("session", envelope.RootElement.GetProperty("type").GetString());
        Assert.Equal(8000, envelope.RootElement.GetProperty("inputSampleRate").GetInt32());

        // Audio sent at the announced rate must be tagged with that rate downstream.
        await socket.SendAsync(new byte[320], WebSocketMessageType.Binary, true, CancellationToken.None);
        await capture.WaitForAsync(f => f is Voxa.Frames.AudioRawFrame, TimeSpan.FromSeconds(5));

        var audio = capture.Captured.OfType<Voxa.Frames.AudioRawFrame>().FirstOrDefault();
        Assert.NotNull(audio);
        Assert.Equal(8000, audio!.SampleRate);

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
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
