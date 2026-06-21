using System.Net.WebSockets;
using System.Text;

namespace Voxa.Speech;

/// <summary>One parsed fragment from a streaming STT vendor's message.</summary>
/// <param name="Text">The (partial or finalized) transcript text for the current segment.</param>
/// <param name="IsSegmentFinal">
/// True when the vendor has locked this segment (it won't change). <see cref="WebSocketSttEngine"/> accumulates
/// locked segments and emits a single VAD-gated final on <see cref="WebSocketSttEngine.FlushAsync()"/>; non-final
/// fragments only update the live (<c>IsFinal:false</c>) transcript.
/// </param>
public readonly record struct SttFragment(string Text, bool IsSegmentFinal);

/// <summary>
/// Base for streaming (WebSocket) <see cref="ISpeechToTextEngine"/> vendors — Deepgram, AssemblyAI, Gladia,
/// Speechmatics, etc. Owns the <see cref="ClientWebSocket"/> connect / receive loop / binary-audio send /
/// graceful close; subclasses only describe the vendor: the endpoint (sync <see cref="BuildEndpoint"/> or async
/// <see cref="ResolveEndpointAsync"/>), the auth header (<see cref="ConfigureConnect"/>), an optional post-connect
/// handshake (<see cref="OnConnectedAsync"/>), how to parse one server message into <see cref="SttFragment"/>s
/// (<see cref="ParseMessage"/>), and an optional close control frame (<see cref="BuildCloseMessage"/>).
///
/// <para><b>Turn integration</b> is delegated to <see cref="StreamingTranscriptAccumulator"/> (shared with the
/// SDK-backed engines): interims stream for live display, locked segments accumulate, and one VAD-gated final is
/// emitted per utterance on <see cref="FlushAsync()"/> — so a streaming vendor drives exactly one agent turn per
/// utterance (like a batch engine) with no post-speech round-trip, and a late provider final from a flushed turn
/// can't bleed into the next.</para>
/// </summary>
public abstract class WebSocketSttEngine : ISpeechToTextEngine
{
    private readonly StreamingTranscriptAccumulator _acc = new();
    private long _audioChunksSent;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveLoop;

    /// <summary>Optional BCP-47 language tag stamped on emitted results.</summary>
    protected virtual string? Language => null;

    /// <summary>Count of binary audio chunks sent so far (for vendors whose close frame needs a sequence number).</summary>
    protected long AudioChunksSent => Interlocked.Read(ref _audioChunksSent);

    /// <summary>The vendor WebSocket URL (model, encoding, sample rate …). Override this for a static endpoint,
    /// or <see cref="ResolveEndpointAsync"/> when the URL needs an async pre-flight.</summary>
    protected virtual Uri BuildEndpoint()
        => throw new NotSupportedException("Override BuildEndpoint() or ResolveEndpointAsync().");

    /// <summary>Resolve the WebSocket endpoint, with an optional async pre-flight (e.g. Gladia's session-init
    /// POST that returns the socket URL). Default returns <see cref="BuildEndpoint"/>.</summary>
    protected virtual Task<Uri> ResolveEndpointAsync(CancellationToken ct) => Task.FromResult(BuildEndpoint());

    /// <summary>Set request headers (typically the auth header) before the socket connects.</summary>
    protected virtual void ConfigureConnect(ClientWebSocket ws) { }

    /// <summary>Optional post-connect handshake sent before audio flows (e.g. Speechmatics <c>StartRecognition</c>).</summary>
    protected virtual Task OnConnectedAsync(ClientWebSocket ws, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Parse one server text message into zero or more fragments. Must be pure (no I/O) and not throw —
    /// a throw is swallowed and the message ignored so a single malformed frame can't kill the receive loop.</summary>
    protected abstract IEnumerable<SttFragment> ParseMessage(string message);

    /// <summary>Optional text control frame to send on graceful stop (e.g. Deepgram <c>CloseStream</c>). Built
    /// per call so vendors can include dynamic data (e.g. Speechmatics <c>EndOfStream</c> with <see cref="AudioChunksSent"/>).</summary>
    protected virtual ReadOnlyMemory<byte>? BuildCloseMessage() => null;

    public async Task StartAsync(CancellationToken ct)
    {
        if (_ws is not null) return; // idempotent
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var endpoint = await ResolveEndpointAsync(ct).ConfigureAwait(false);
        var ws = new ClientWebSocket();
        ConfigureConnect(ws);
        await ws.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        _ws = ws;
        await OnConnectedAsync(ws, ct).ConfigureAwait(false);
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    public async ValueTask WriteAudioAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open) return;
        try
        {
            await ws.SendAsync(pcm, WebSocketMessageType.Binary, endOfMessage: true, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _audioChunksSent);
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException
                                      or OperationCanceledException or InvalidOperationException)
        {
            // Socket dropped / closing / disposed — drop this chunk rather than fault the pipeline.
        }
    }

    public IAsyncEnumerable<TranscriptionResult> ReadTranscriptsAsync(CancellationToken ct) => _acc.ReadAllAsync(ct);

    /// <summary>VAD-gated final: <see cref="SpeechToTextProcessor"/> calls this at <c>UserStoppedSpeakingFrame</c>.</summary>
    public Task FlushAsync()
    {
        _acc.Flush(Language);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        var ws = _ws;
        if (ws is not null && ws.State == WebSocketState.Open && BuildCloseMessage() is { } cm)
        {
            try { await ws.SendAsync(cm, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false); }
            catch { /* best-effort close signal */ }
        }
        _cts?.Cancel();
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        _acc.Complete();
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var ws = _ws!;
        var buffer = new byte[16 * 1024];
        using var msg = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                ValueWebSocketReceiveResult result;
                try { result = await ws.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch (WebSocketException) { break; }

                if (result.MessageType == WebSocketMessageType.Close) break;
                msg.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                if (result.MessageType == WebSocketMessageType.Text)
                    Ingest(Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length));
                msg.SetLength(0);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private void Ingest(string json)
    {
        List<SttFragment>? frags;
        try { frags = ParseMessage(json).ToList(); }
        catch { return; } // ignore a malformed frame rather than kill the loop

        foreach (var f in frags)
            _acc.OnFragment(f.Text, f.IsSegmentFinal, Language);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.ConfigureAwait(false); } catch { /* shutdown */ }
        }
        _ws?.Dispose();
        _cts?.Dispose();
        _acc.Complete();
        GC.SuppressFinalize(this);
    }
}
