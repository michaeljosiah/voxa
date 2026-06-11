using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Voxa.AspNetCore;

namespace Voxa.LocalSpeech.Tests;

/// <summary>
/// The VLS-001 flagship test (WS5.4): a complete voice conversation with ZERO credentials and —
/// once the model cache is warm — zero network. Real Silero VAD, real whisper.cpp (tiny.en), the
/// Echo agent, and real Piper (en_US-amy-low), composed exactly the way a user would compose them:
/// <c>AddVoxa(config)</c> + <c>MapVoxaVoice("/voice").UseDefaults()</c>.
///
/// Excluded from the default suite by the LocalModels trait; the CI lane restores the model cache
/// and runs this with outbound network blocked — the enforcement of the zero-network claim.
/// </summary>
public class LocalConversationEndToEndTests
{
    private static readonly TimeSpan ConversationDeadline = TimeSpan.FromSeconds(120);

    /// <summary>Captures host-side logs so a failure shows WHERE the pipeline died.</summary>
    private sealed class CollectingLoggerProvider : ILoggerProvider, ILogger
    {
        public List<string> Lines { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add($"[{logLevel}] {formatter(state, exception)}{(exception is null ? "" : $" :: {exception}")}");
        }
        public void Dispose() { }
    }

    [Fact]
    [Trait("Category", "LocalModels")]
    public async Task Local_Conversation_Roundtrip_With_Zero_Credentials()
    {
        var logs = new CollectingLoggerProvider();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Voxa:Profile"] = "LowLatency",
                ["Voxa:Stt"] = "WhisperCpp",
                ["Voxa:WhisperCpp:Model"] = "tiny.en", // smallest — CI cache budget
                ["Voxa:Tts"] = "Piper",
                ["Voxa:Piper:Voice"] = "en_US-amy-low", // smallest — CI cache budget
                ["Voxa:Agent:Provider"] = "Echo",       // keyless parrot closes the loop
            })
            .Build();

        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Debug));
                    services.AddSingleton(configuration);
                    services.AddVoxa(configuration); // the real meta-package entry point
                })
                .Configure(app =>
                {
                    app.UseWebSockets();
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapVoxaVoice("/voice").UseDefaults());
                }))
            .StartAsync();

        var client = host.GetTestServer().CreateWebSocketClient();
        using var socket = await client.ConnectAsync(new Uri("ws://localhost/voice"), CancellationToken.None);

        // ── 1. Session envelope announces the local providers' true rates ──
        var (sessionJson, _) = await ReceiveAsync(socket);
        using (var session = JsonDocument.Parse(sessionJson!))
        {
            Assert.Equal("session", session.RootElement.GetProperty("type").GetString());
            Assert.Equal(16000, session.RootElement.GetProperty("inputSampleRate").GetInt32()); // whisper
            Assert.Equal(16000, session.RootElement.GetProperty("outputSampleRate").GetInt32()); // amy-low
        }

        // ── 2. Stream the JFK fixture like a mic would (20 ms frames), then silence so the real
        //       Silero VAD closes the utterance ──
        var pcm = ReadWavPcm(Path.Combine(AppContext.BaseDirectory, "fixtures", "jfk.wav"));
        const int frameBytes = 2 * 16000 / 50; // 20 ms @ 16 kHz mono PCM16
        for (int i = 0; i < pcm.Length; i += frameBytes)
        {
            await socket.SendAsync(
                pcm.AsMemory(i, Math.Min(frameBytes, pcm.Length - i)),
                WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);
        }
        var silence = new byte[frameBytes];
        for (int i = 0; i < 100; i++) // 2 s of silence — well past any profile's VAD stop duration
        {
            await socket.SendAsync(silence, WebSocketMessageType.Binary, true, CancellationToken.None);
        }

        // ── 3. The conversation comes back: transcript → echo text → synthesized audio ──
        var transcripts = new StringBuilder();
        var botText = new StringBuilder();
        long botAudioBytes = 0;
        var diagnostics = new List<string>(); // every received envelope — dumped on failure

        var deadline = DateTime.UtcNow + ConversationDeadline;
        while (DateTime.UtcNow < deadline)
        {
            using var cts = new CancellationTokenSource(deadline - DateTime.UtcNow);
            string? text;
            int binaryLength;
            try { (text, binaryLength) = await ReceiveAsync(socket, cts.Token); }
            catch (OperationCanceledException) { break; }

            if (text is not null)
            {
                diagnostics.Add(text);
                using var doc = JsonDocument.Parse(text);
                var type = doc.RootElement.GetProperty("type").GetString();
                if (type == "transcription" && doc.RootElement.TryGetProperty("text", out var t))
                    transcripts.Append(t.GetString()).Append(' ');
                else if (type == "text" && doc.RootElement.TryGetProperty("text", out var b))
                    botText.Append(b.GetString());
            }
            else
            {
                if (botAudioBytes == 0 && binaryLength > 0) diagnostics.Add($"<first binary frame: {binaryLength} bytes>");
                botAudioBytes += binaryLength;
            }

            // Done when every leg of the round trip has been observed.
            if (transcripts.ToString().Contains("country", StringComparison.OrdinalIgnoreCase)
                && botText.ToString().Contains("You said", StringComparison.OrdinalIgnoreCase)
                && botAudioBytes >= 32_000) // ≥ 1 s of 16 kHz PCM16 — Piper actually spoke
            {
                break;
            }
        }

        string received;
        lock (logs.Lines)
        {
            received =
                $"received envelopes:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics)}" +
                $"{Environment.NewLine}host logs (tail):{Environment.NewLine}" +
                string.Join(Environment.NewLine, logs.Lines.TakeLast(80));
        }
        Assert.True(transcripts.ToString().Contains("country", StringComparison.OrdinalIgnoreCase),
            $"no transcript containing 'country' — {received}");
        Assert.True(botText.ToString().Contains("You said", StringComparison.OrdinalIgnoreCase),
            $"no echo text — {received}");
        Assert.True(botAudioBytes >= 32_000,
            $"expected ≥ 1 s of synthesized audio, got {botAudioBytes} bytes — {received}");

        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    /// <summary>One WebSocket message: (json, 0) for text frames, (null, byteCount) for binary.</summary>
    private static async Task<(string? Text, int BinaryLength)> ReceiveAsync(
        WebSocket socket, CancellationToken ct = default)
    {
        var buffer = new byte[64 * 1024];
        var total = 0;
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(total).AsArraySegment(), ct);
            total += result.Count;
        } while (!result.EndOfMessage && total < buffer.Length);

        return result.MessageType == WebSocketMessageType.Text
            ? (Encoding.UTF8.GetString(buffer, 0, total), 0)
            : (null, total);
    }

    private static byte[] ReadWavPcm(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var chunkId = Encoding.ASCII.GetString(bytes, offset, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 4, 4));
            if (chunkId == "data") return bytes.AsSpan(offset + 8, chunkSize).ToArray();
            offset += 8 + chunkSize + (chunkSize & 1);
        }
        throw new InvalidDataException($"{path} has no data chunk.");
    }
}

internal static class MemoryExtensions
{
    public static ArraySegment<byte> AsArraySegment(this Memory<byte> memory)
        => System.Runtime.InteropServices.MemoryMarshal.TryGetArray<byte>(memory, out var segment)
            ? segment
            : throw new InvalidOperationException("non-array-backed memory");
}
