using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Voxa.Frames;
using Voxa.Transports.WebSocket.Protocol;

namespace Voxa.Transports.WebSocket.Tests;

/// <summary>
/// VDX-005 W1.3: <c>clients/voxa-client/voxa-wire.schema.json</c> is generated from the C# wire
/// envelope records — the single source of truth both wire directions and the JS client's types
/// derive from. This golden test regenerates the schema and fails on drift, so an envelope change
/// that isn't reflected in the committed schema (and the TypeScript generated from it) breaks CI,
/// not a consumer at runtime. Regenerate with <c>VOXA_REGEN_GOLDEN=1</c>.
/// </summary>
public class WireSchemaGoldenTests
{
    [Fact]
    public void Committed_Wire_Schema_Matches_The_Envelope_Records()
    {
        var generated = WireSchema.Generate();
        var path = Path.Combine(FindRepoRoot(), "clients", "voxa-client", "voxa-wire.schema.json");

        if (Environment.GetEnvironmentVariable("VOXA_REGEN_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, generated);
            return;
        }

        Assert.True(File.Exists(path),
            $"Missing committed wire schema at {path}. Regenerate: VOXA_REGEN_GOLDEN=1 dotnet test " +
            "tests/Voxa.Transports.WebSocket.Tests --filter WireSchemaGolden");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), generated);
    }

    [Fact]
    public void Schema_Type_Constants_Match_The_Codec()
    {
        // The generator's wire-type table must say what the codec actually puts on (and accepts
        // from) the wire — outbound consts are read back from the real builders, inbound consts
        // are proven accepted by the real parser.
        Assert.Equal("session", TypeOf(WireProtocol.BuildSession(new SessionInfoFrame(16000, 24000))));
        Assert.Equal("transcription", TypeOf(WireProtocol.BuildTranscription(new TranscriptionFrame("x", IsFinal: true))));
        Assert.Equal("text", TypeOf(WireProtocol.BuildText("x")));
        Assert.Equal("toolCall", TypeOf(WireProtocol.BuildToolCall(new ToolCallRequestFrame("c", "n", "{}"))));
        Assert.Equal("speaking", TypeOf(WireProtocol.BuildSpeaking("bot", true)));
        Assert.Equal("interruption", TypeOf(WireProtocol.BuildInterruption()));
        Assert.Equal("end", TypeOf(WireProtocol.BuildEnd()));
        Assert.Equal("status", TypeOf(WireProtocol.BuildStatus("x")));
        Assert.Equal("error", TypeOf(WireProtocol.BuildError("x")));

        Assert.IsType<EndFrame>(WireProtocol.TryParseClientMessage("{\"type\":\"end\"}"));
        Assert.IsType<TextFrame>(WireProtocol.TryParseClientMessage("{\"type\":\"text\",\"text\":\"hi\"}"));
        Assert.IsType<ToolCallResultFrame>(WireProtocol.TryParseClientMessage(
            "{\"type\":\"toolResult\",\"callId\":\"c\",\"resultJson\":\"{}\"}"));
    }

    private static string TypeOf(byte[] utf8Json)
    {
        using var doc = JsonDocument.Parse(utf8Json);
        return doc.RootElement.GetProperty("type").GetString()!;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Voxa.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Voxa.slnx not found above test bin dir.");
    }
}

/// <summary>
/// Builds the wire-schema document from the envelope records via <see cref="JsonSchemaExporter"/>.
/// Lives in the test project (not shipped) — the schema is a build-time artifact, not a runtime one.
/// </summary>
internal static class WireSchema
{
    private static readonly JsonSerializerOptions ExportOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Reflection resolver: the exporter needs one, and the source-generated WireJsonContext is
        // serialization-shaped; build-time reflection is fine here (test project, never shipped).
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
    };

    // Def name -> (record type, wire-type const). TypeOnlyEnvelope and MessageEnvelope each carry
    // two wire types, so they appear twice under distinct def names. The consts are cross-checked
    // against the real codec by Schema_Type_Constants_Match_The_Codec.
    private static readonly (string Def, Type Record, string WireType, bool ServerToClient)[] Envelopes =
    [
        ("Session",          typeof(SessionEnvelope),       "session",       true),
        ("Transcription",    typeof(TranscriptionEnvelope), "transcription", true),
        ("Text",             typeof(TextEnvelope),          "text",          true),
        ("ToolCall",         typeof(ToolCallEnvelope),      "toolCall",      true),
        ("Speaking",         typeof(SpeakingEnvelope),      "speaking",      true),
        ("Interruption",     typeof(TypeOnlyEnvelope),      "interruption",  true),
        ("Status",           typeof(MessageEnvelope),       "status",        true),
        ("Error",            typeof(MessageEnvelope),       "error",         true),
        ("End",              typeof(TypeOnlyEnvelope),      "end",           true),
        ("ClientEnd",        typeof(EndClientEnvelope),     "end",           false),
        ("ClientText",       typeof(TextClientEnvelope),    "text",          false),
        ("ClientToolResult", typeof(ToolResultClientEnvelope), "toolResult", false),
    ];

    public static string Generate()
    {
        var defs = new JsonObject();

        foreach (var (def, record, wireType, _) in Envelopes)
        {
            var node = JsonSchemaExporter.GetJsonSchemaAsNode(ExportOptions, record).AsObject();
            var props = node["properties"]!.AsObject();
            props["type"] = new JsonObject { ["const"] = wireType };
            if (record == typeof(SpeakingEnvelope))
                props["who"] = new JsonObject { ["enum"] = new JsonArray("bot", "user") };
            defs[def] = node;
        }

        defs["ServerMessage"] = OneOf(true);
        defs["ClientMessage"] = OneOf(false);

        var root = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$comment"] =
                "AUTO-GENERATED from the C# wire envelope records (src/Voxa.Transports.WebSocket/Protocol/" +
                "WireMessages.cs). Do not edit by hand. Regenerate: VOXA_REGEN_GOLDEN=1 dotnet test " +
                "tests/Voxa.Transports.WebSocket.Tests --filter WireSchemaGolden",
            ["title"] = "Voxa voice WebSocket wire protocol",
            ["wireVersion"] = new SessionInfoFrame(16000, 24000).ProtocolVersion,
            ["$defs"] = defs,
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true })
            .ReplaceLineEndings("\n") + "\n";
    }

    private static JsonObject OneOf(bool serverToClient)
    {
        var refs = new JsonArray();
        foreach (var (def, _, _, s2c) in Envelopes)
            if (s2c == serverToClient)
                refs.Add(new JsonObject { ["$ref"] = $"#/$defs/{def}" });
        return new JsonObject { ["oneOf"] = refs };
    }
}
