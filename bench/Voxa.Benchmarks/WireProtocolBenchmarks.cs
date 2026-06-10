using BenchmarkDotNet.Attributes;
using Voxa.Frames;
using Voxa.Transports.WebSocket.Protocol;

namespace Voxa.Benchmarks;

/// <summary>
/// Outbound wire-serialization cost. After WS3 the Build* methods return UTF-8 byte[] via
/// source generation (1 allocation, no reflection); before WS3 they returned a JSON string built
/// from an anonymous type via reflection (3–4 allocations including the later UTF8 encode).
/// </summary>
[MemoryDiagnoser]
public class WireProtocolBenchmarks
{
    private readonly TranscriptionFrame _transcription =
        new("the quick brown fox jumps over the lazy dog", IsFinal: true, Language: "en");
    private readonly ToolCallRequestFrame _toolCall =
        new("call_123", "get_weather", "{\"location\":\"London\",\"unit\":\"celsius\"}");

    [Benchmark]
    public int BuildTranscription() => WireProtocol.BuildTranscription(_transcription).Length;

    [Benchmark]
    public int BuildText() => WireProtocol.BuildText("hello from the synthesized response").Length;

    [Benchmark]
    public int BuildToolCall() => WireProtocol.BuildToolCall(_toolCall).Length;

    [Benchmark]
    public int BuildInterruption() => WireProtocol.BuildInterruption().Length;
}
